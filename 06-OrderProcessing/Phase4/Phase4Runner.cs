using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using OrderProcessingSystem.Phase2;
using OrderProcessingSystem.Phase3;
using OrderProcessingSystem.Shared.Exceptions;
using OrderProcessingSystem.Shared.Logging;
using OrderProcessingSystem.Shared.Models;
using OrderProcessingSystem.Shared.Repositories;
using OrderProcessingSystem.Shared.Storage;

namespace OrderProcessingSystem.Phase4;

public sealed class CustomerDashboardService
{
    private readonly JsonFileStore _store;private readonly CustomerRepository _customers;private readonly OrderRepository _orders;private readonly PaymentRepository _payments;private readonly EmailRepository _emails;private readonly HashSet<int> _failPayments;
    public CustomerDashboardService(JsonFileStore s,CustomerRepository c,OrderRepository o,PaymentRepository p,EmailRepository e,IEnumerable<int>? failPaymentsForCustomerIds=null){_store=s;_customers=c;_orders=o;_payments=p;_emails=e;_failPayments=(failPaymentsForCustomerIds??Array.Empty<int>()).ToHashSet();}
    public async Task<CustomerDashboard> LoadAsync(int customerId,CancellationToken ct)
    {
        var sw=Stopwatch.StartNew();var result=new CustomerDashboard();
        var profile=LoadProfile(ct);var orders=LoadOrders(ct);var payments=LoadPayments(ct);var emails=LoadEmails(ct);
        var all=Task.WhenAll(profile,orders,payments,emails);
        try{await all;}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{foreach(var task in new Task[]{profile,orders,payments,emails})if(task.IsFaulted&&task.Exception is not null)foreach(var ex in task.Exception.Flatten().InnerExceptions)result.Errors.Add(ex.Message);}
        if(profile.Status==TaskStatus.RanToCompletion)result.Customer=await profile;
        if(orders.Status==TaskStatus.RanToCompletion)result.Orders=await orders;
        if(payments.Status==TaskStatus.RanToCompletion)result.Payments=await payments;
        if(emails.Status==TaskStatus.RanToCompletion)result.Emails=await emails;
        sw.Stop();result.ElapsedMs=sw.Elapsed.TotalMilliseconds;return result;
        async Task<Customer> LoadProfile(CancellationToken token){var x=await _customers.GetByIdAsync(customerId,token);return x??throw new CustomerNotFoundException($"Customer #{customerId} not found");}
        async Task<List<Order>> LoadOrders(CancellationToken token){var x=await _orders.GetAllAsync(token);return x.Where(o=>o.CustomerId==customerId).OrderBy(o=>o.Id).ToList();}
        async Task<List<PaymentTransaction>> LoadPayments(CancellationToken token){if(_failPayments.Contains(customerId)){await Latency.QueryAsync("Dashboard.Payments",$"Loading payments for Customer #{customerId}...",token);throw new TimeoutException("Payments store timeout");}var x=await _payments.GetAllAsync(token);var ids=(await _store.ReadAsync<Order>(DataFiles.Orders,token)).Where(o=>o.CustomerId==customerId).Select(o=>o.Id).ToHashSet();return x.Where(p=>ids.Contains(p.OrderId)).ToList();}
        async Task<List<EmailMessage>> LoadEmails(CancellationToken token){var x=await _emails.GetAllAsync(token);var ids=(await _store.ReadAsync<Order>(DataFiles.Orders,token)).Where(o=>o.CustomerId==customerId).Select(o=>o.Id).ToHashSet();return x.Where(e=>ids.Contains(e.OrderId)).ToList();}
    }
}

public interface IShippingCarrier { string Name { get; } Task<decimal> GetQuoteAsync(Order order,CancellationToken ct); }
public sealed class ShippingCarrier : IShippingCarrier
{
    private readonly decimal _factor;private readonly int _min,_max;private readonly HashSet<int> _fail;private readonly bool _cairoRule;
    public string Name { get; }
    public ShippingCarrier(string name,decimal factor,int min,int max,IEnumerable<int>? failForOrderIds=null,bool cairoEvenFails=false){Name=name;_factor=factor;_min=min;_max=max;_fail=(failForOrderIds??Array.Empty<int>()).ToHashSet();_cairoRule=cairoEvenFails;}
    public async Task<decimal> GetQuoteAsync(Order order,CancellationToken ct){try{await Latency.StepAsync(Name,"Handshake...",150,ct);if(_fail.Contains(order.Id)||(_cairoRule&&order.Id%2==0))throw new ShippingProviderUnavailableException($"{Name} unavailable for Order #{order.Id}");await Task.Delay(Random.Shared.Next(_min,_max+1),ct);decimal weight=Weight(order);int qty=order.Items.Sum(x=>x.Quantity);decimal basePrice=order.SubTotal>=1000m?0m:Math.Round(5m+1.50m*qty+2m*weight,2,MidpointRounding.AwayFromZero);return Math.Round(basePrice*_factor,2,MidpointRounding.AwayFromZero);}catch(OperationCanceledException){Log.Warning($"[{Name}] cancelled");throw;}}
    private static decimal Weight(Order o){var weights=new Dictionary<int,decimal>{{1,2.2m},{2,.4m},{3,.1m},{4,.9m},{5,.2m},{6,.3m},{7,5m},{8,.2m}};return o.Items.Sum(i=>weights.GetValueOrDefault(i.ProductId)*i.Quantity);}
}
public sealed class ShippingQuoteService
{
    private readonly List<IShippingCarrier> _carriers;public List<string> LastCancelledCarriers { get; }=new();
    public ShippingQuoteService(IEnumerable<IShippingCarrier> carriers)=>_carriers=carriers.ToList();
    public async Task<ShippingQuote> GetFastestQuoteAsync(Order order,CancellationToken ct)
    {
        LastCancelledCarriers.Clear();var sw=Stopwatch.StartNew();using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);var channel=Channel.CreateUnbounded<(IShippingCarrier Carrier,decimal? Price,Exception? Error)>();
        var tasks=_carriers.Select(c=>Fetch(c,linked.Token)).ToArray();int failed=0;
        try{
            while(true){ct.ThrowIfCancellationRequested();var read=channel.Reader.ReadAsync(linked.Token).AsTask();var timer=Task.Delay(Math.Max(1,1200-(int)sw.ElapsedMilliseconds),ct);var done=await Task.WhenAny(read,timer);ct.ThrowIfCancellationRequested();if(done==timer && !read.IsCompleted){
                // At the deadline, accept a successful quote already queued by a carrier; do not
                // discard it merely because the timeout task won Task.WhenAny at the same instant.
                ShippingQuote? alreadyArrived=null;
                while(channel.Reader.TryRead(out var pending))
                {
                    if(pending.Price.HasValue && alreadyArrived is null) alreadyArrived=new ShippingQuote(pending.Carrier.Name,pending.Price.Value,Math.Min(1200,sw.ElapsedMilliseconds));
                    else if(pending.Error is not null && pending.Error is not OperationCanceledException) failed++;
                }
                if(alreadyArrived is not null)
                {
                    var winner=alreadyArrived.Carrier;var running=_carriers.Zip(tasks).Where(x=>x.First.Name!=winner&&!x.Second.IsCompleted).Select(x=>x.First.Name).ToList();linked.Cancel();await Task.WhenAll(tasks.Select(t=>Observe(t,CancellationToken.None)));LastCancelledCarriers.AddRange(running.Where(n=>!LastCancelledCarriers.Contains(n)));return alreadyArrived;
                }
                var stillRunning=_carriers.Zip(tasks).Where(x=>!x.Second.IsCompleted).Select(x=>x.First.Name).ToList();linked.Cancel();await Task.WhenAll(tasks.Select(t=>Observe(t,CancellationToken.None)));
                while(channel.Reader.TryRead(out var pendingAfterCancel))if(pendingAfterCancel.Error is not null&&pendingAfterCancel.Error is not OperationCanceledException)failed++;
                LastCancelledCarriers.AddRange(stillRunning.Where(n=>!LastCancelledCarriers.Contains(n)));
                if(failed==_carriers.Count)throw new ShippingProviderUnavailableException("All carriers failed");
                return new ShippingQuote("Flat rate",25.00m,Math.Min(1200,sw.ElapsedMilliseconds));
            }var r=await read;if(r.Price.HasValue){long arrivedAt=sw.ElapsedMilliseconds;var losers=_carriers.Zip(tasks).Where(x=>x.First.Name!=r.Carrier.Name&&!x.Second.IsCompleted).Select(x=>x.First.Name).ToList();linked.Cancel();await Task.WhenAll(tasks.Select(t=>Observe(t,CancellationToken.None)));LastCancelledCarriers.AddRange(losers.Where(n=>!LastCancelledCarriers.Contains(n)));sw.Stop();return new ShippingQuote(r.Carrier.Name,r.Price.Value,arrivedAt);}if(r.Error is not OperationCanceledException)failed++;if(failed==_carriers.Count){linked.Cancel();await Task.WhenAll(tasks.Select(t=>Observe(t,CancellationToken.None)));throw new ShippingProviderUnavailableException("All carriers failed");}}
        }catch(OperationCanceledException) when(ct.IsCancellationRequested){linked.Cancel();await Task.WhenAll(tasks.Select(t=>Observe(t,CancellationToken.None)));throw;}
        async Task Fetch(IShippingCarrier carrier,CancellationToken token){try{var price=await carrier.GetQuoteAsync(order,token);await channel.Writer.WriteAsync((carrier,price,null),CancellationToken.None);}catch(Exception ex){if(ex is not OperationCanceledException)await channel.Writer.WriteAsync((carrier,null,ex),CancellationToken.None);}}
        static async Task Observe(Task t,CancellationToken ct){try{await t;}catch(OperationCanceledException) when(ct.IsCancellationRequested){}catch{}}
    }
}
public static class FraudScorer
{
    public static int Score(int orderId){long h=orderId;for(int i=0;i<100_000;i++)h=(h*31+i)%1_000_000_007;return (int)(h%100);}
    public static async Task RunAsync(CancellationToken ct)
    {
        var ids=Enumerable.Range(1,10_000).ToArray();int poolBefore=ThreadPool.ThreadCount;int sequentialThread=Environment.CurrentManagedThreadId;var seqWatch=Stopwatch.StartNew();var sequential=new List<int>();foreach(int id in ids){ct.ThrowIfCancellationRequested();if(Score(id)>=90)sequential.Add(id);}seqWatch.Stop();var parWatch=Stopwatch.StartNew();var parallel=new ConcurrentBag<int>();var threads=new ConcurrentDictionary<int,byte>();await Parallel.ForEachAsync(ids,new ParallelOptions{MaxDegreeOfParallelism=Environment.ProcessorCount,CancellationToken=ct},(id,token)=>{token.ThrowIfCancellationRequested();threads.TryAdd(Environment.CurrentManagedThreadId,0);if(Score(id)>=90)parallel.Add(id);return ValueTask.CompletedTask;});parWatch.Stop();var p=parallel.OrderBy(x=>x).ToArray();var s=sequential.OrderBy(x=>x).ToArray();Console.WriteLine($"Fraud scoring: flagged={s.Length}; sequential={seqWatch.Elapsed.TotalSeconds:F2}s (1 thread id {sequentialThread}); parallel={parWatch.Elapsed.TotalSeconds:F2}s; speed-up={(parWatch.Elapsed.TotalMilliseconds==0?0:seqWatch.Elapsed.TotalMilliseconds/parWatch.Elapsed.TotalMilliseconds):F2}x; parallel threads={threads.Count}; ProcessorCount={Environment.ProcessorCount}; ThreadPool.ThreadCount {poolBefore}->{ThreadPool.ThreadCount}");Console.WriteLine($"Same flag set: {s.SequenceEqual(p)}; first five: {string.Join(", ",s.Take(5))}");
    }
}

public sealed class SyncOrderProcessor
{
    private readonly JsonFileStore _store;private readonly CustomerRepository _customers;private readonly ProductRepository _products;private readonly CouponRepository _coupons;private readonly OrderRepository _orders;
    public SyncOrderProcessor(JsonFileStore s){_store=s;_customers=new(s);_products=new(s);_coupons=new(s);_orders=new(s);}
    public void Process(Order o,CancellationToken ct)
    {
        bool reserved=false;try{var c=_customers.GetById(o.CustomerId,ct);if(c is null){o.Status=OrderStatus.Rejected;o.FailureReason=$"Customer #{o.CustomerId} not found";_orders.Save(o,ct);return;}if(!c.IsActive){o.Status=OrderStatus.Rejected;o.FailureReason="Customer is inactive";_orders.Save(o,ct);return;}if(!_products.ReserveSync(o.Items,ct)){o.Status=OrderStatus.Rejected;var p=_products.GetById(o.Items.First().ProductId,ct);o.FailureReason=$"Insufficient stock for '{p?.Name??"unknown"}'";_orders.Save(o,ct);return;}reserved=true;var prods=new Dictionary<int,Product>();foreach(var i in o.Items){var p=_products.GetById(i.ProductId,ct)??throw new InvalidOperationException($"Product #{i.ProductId} not found");i.UnitPrice=p.Price;prods[p.Id]=p;}o.SubTotal=OrderProcessingSystem.Phase2.PricingService.Round(o.Items.Sum(i=>i.UnitPrice*i.Quantity));int pct=c.LoyaltyTier switch{LoyaltyTier.Bronze=>0,LoyaltyTier.Silver=>5,LoyaltyTier.Gold=>10,_=>0};if(o.CouponCode is not null){var cp=_coupons.GetByCode(o.CouponCode,ct);if(cp is null||cp.ExpiryDate.Date<DateTime.Today)throw new InvalidCouponException($"Coupon '{o.CouponCode}' does not exist or has expired");pct+=cp.DiscountPercent;}pct=Math.Min(30,pct);o.Discount=OrderProcessingSystem.Phase2.PricingService.Round(o.SubTotal*pct/100m);var weight=o.Items.Sum(i=>prods[i.ProductId].WeightKg*i.Quantity);o.ShippingCost=o.SubTotal>=1000?0:OrderProcessingSystem.Phase2.PricingService.Round(5m+1.5m*o.Items.Sum(i=>i.Quantity)+2m*weight);o.TotalAmount=OrderProcessingSystem.Phase2.PricingService.Round(o.SubTotal-o.Discount+o.ShippingCost);Latency.StepSync("SyncPaymentGateway","Authorize...",800,ct);Latency.StepSync("SyncPaymentGateway","Fraud check...",500,ct);Latency.StepSync("SyncPaymentGateway","Capture...",700,ct);var tx="TXN-"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();_store.Update<PaymentTransaction>(DataFiles.Payments,rows=>{rows.Add(new PaymentTransaction{OrderId=o.Id,Amount=o.TotalAmount,Status=PaymentStatus.Captured,TransactionId=tx,CreatedAt=DateTime.Now});return rows;});o.TransactionId=tx;o.Status=OrderStatus.Completed;_orders.Save(o,ct);Latency.StepSync("SyncEmail","Render template...",200,ct);Latency.StepSync("SyncEmail","Connect SMTP...",300,ct);Latency.StepSync("SyncEmail","Send...",500,ct);_store.Update<EmailMessage>(DataFiles.Emails,rows=>{rows.Add(new EmailMessage{Id=Guid.NewGuid(),OrderId=o.Id,To=c.Email,Subject=$"Order #{o.Id} confirmation",Body="Sync comparison test",Attempts=1,SentAt=DateTime.Now});return rows;});reserved=false;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){if(reserved)_products.ReleaseSync(o.Items);o.Status=OrderStatus.Pending;o.FailureReason="Cancelled";try{_orders.Save(o,CancellationToken.None);}catch{}return;}
        catch(Exception ex){if(reserved)_products.ReleaseSync(o.Items);o.Status=OrderStatus.Failed;o.FailureReason=ex.Message;try{_orders.Save(o,CancellationToken.None);}catch{}}
    }
}
public sealed class FireAndForgetInvestigation
{
    private readonly OrderRepository _orders;private readonly IEmailService _emails;private readonly CancellationToken _ct;
    public FireAndForgetInvestigation(OrderRepository orders,IEmailService emails,CancellationToken ct){_orders=orders;_emails=emails;_ct=ct;}
    public async Task ProcessOrderAsync(Order order,Customer customer,CancellationToken ct)
    {
        await _orders.SaveAsync(order,ct);
#pragma warning disable CS4014
        _emails.SendConfirmationAsync(order,customer,ct); // Deliberately broken: the task is not awaited.
#pragma warning restore CS4014
        Console.WriteLine("Order processing finished");
    }
    public async Task ProcessOrderFixedAsync(Order order,Customer customer,CancellationToken ct){await _orders.SaveAsync(order,ct);await _emails.SendConfirmationAsync(order,customer,ct);Console.WriteLine("Order processing finished (email completed)");}
}

public sealed class Phase4Runner
{
    private readonly JsonFileStore _store;private readonly DataSeeder _seeder;public Phase4Runner(JsonFileStore s,DataSeeder d){_store=s;_seeder=d;}
    public async Task RunAsync(CancellationToken ct)
    {
        await _seeder.ResetAsync(ct);Console.WriteLine("===== Phase 4.1 Live checkout progress =====");var orders=await new IncomingOrderRepository(_store).GetAllAsync(ct);var products=new ProductRepository(_store);var customers=new CustomerRepository(_store);var ordersRepo=new OrderRepository(_store,new[]{13});var email=new EmailService(_store,new Dictionary<int,int>{{3,2},{17,int.MaxValue}});var processor=new OrderProcessor(new CustomerService(customers),new InventoryService(products),new PricingServiceV3(_store,customers,new CouponRepository(_store)),new PaymentGateway(_store,new[]{5},new[]{8}),ordersRepo,email,email,products);
        var channel=Channel.CreateUnbounded<(Order Order,double Seconds)>();var tasks=orders.Select(async o=>{var sw=Stopwatch.StartNew();await processor.ProcessOrderAsync(o,ct);sw.Stop();await channel.Writer.WriteAsync((o,sw.Elapsed.TotalSeconds),CancellationToken.None);}).ToArray();var printTask=PrintCompletions(channel.Reader,tasks.Length,ct);try{await Task.WhenAll(tasks);}finally{channel.Writer.TryComplete();}await printTask;var saved=await ordersRepo.GetAllAsync(CancellationToken.None);PrintStatusCounts(saved);SnapshotLiveCheckout();if(ct.IsCancellationRequested){Console.WriteLine($"Run cancelled — {saved.Count(x=>x.Status!=OrderStatus.Pending||x.FailureReason!="Cancelled")} orders finished, {saved.Count(x=>x.FailureReason=="Cancelled")} cancelled");return;}
        Console.WriteLine("===== Phase 4.2 Customer dashboard =====");var dashboard=new CustomerDashboardService(_store,customers,ordersRepo,new PaymentRepository(_store),new EmailRepository(_store),new[]{99});Log.ResetThreadTracking();int dashboardPoolBefore=ThreadPool.ThreadCount;var d=await dashboard.LoadAsync(3,ct);int dashboardThreads=Log.DistinctThreads;int dashboardPoolAfter=ThreadPool.ThreadCount;Console.WriteLine($"Customer #3 {d.Customer?.Name} — {d.Orders.Count} orders ({string.Join(", ",d.Orders.Select(x=>$"#{x.Id} {x.Status}"))}), {d.Payments.Count} payments ({string.Join(", ",d.Payments.Select(x=>$"#{x.OrderId} {x.Status}"))}), {d.Emails.Count} email(s). Dashboard time {d.ElapsedMs:F0} ms; distinct threads={dashboardThreads}; ThreadPool.ThreadCount {dashboardPoolBefore}->{dashboardPoolAfter}");var missing=await dashboard.LoadAsync(99,ct);foreach(var e in missing.Errors)Console.WriteLine($"Customer #99 dashboard failure: {e}");Console.WriteLine($"Customer #99 dashboard total time: {missing.ElapsedMs:F0} ms");
        Console.WriteLine("===== Phase 4.3 Fastest shipping quote =====");var quoteService=new ShippingQuoteService(new IShippingCarrier[]{new ShippingCarrier("FastShip",1.00m,500,1500),new ShippingCarrier("CairoExpress",.95m,300,2000,cairoEvenFails:true),new ShippingCarrier("NileCourier",1.10m,800,1200)});var byId=saved.Concat(orders).GroupBy(o=>o.Id).ToDictionary(g=>g.Key,g=>g.First());Log.ResetThreadTracking();int quotePoolBefore=ThreadPool.ThreadCount;var quoteWatch=Stopwatch.StartNew();for(int id=1;id<=5;id++){try{var q=await quoteService.GetFastestQuoteAsync(byId[id],ct);Console.WriteLine($"Order #{id}: {q.Carrier}, {q.Price:F2}, {q.ElapsedMs} ms; cancelled: {string.Join(", ",quoteService.LastCancelledCarriers)}");}catch(Exception ex){Console.WriteLine($"Order #{id}: {ex.Message}");}}quoteWatch.Stop();int quoteThreads=Log.DistinctThreads;int quotePoolAfter=ThreadPool.ThreadCount;Console.WriteLine($"Five quote runs elapsed {quoteWatch.Elapsed.TotalSeconds:F2}s; distinct logged threads={quoteThreads}; ThreadPool.ThreadCount {quotePoolBefore}->{quotePoolAfter}");try{var allFail=new ShippingQuoteService(new IShippingCarrier[]{new ShippingCarrier("FastShip",1m,500,500,new[]{6}),new ShippingCarrier("CairoExpress",.95m,300,300,new[]{6},true),new ShippingCarrier("NileCourier",1.1m,800,800,new[]{6})});await allFail.GetFastestQuoteAsync(new Order{Id=6,Items=new()},ct);}catch(ShippingProviderUnavailableException ex){Console.WriteLine($"All carriers failure demo: {ex.Message}");}
        Console.WriteLine("===== Phase 4.4A Fraud scoring =====");await FraudScorer.RunAsync(ct);
        Console.WriteLine("===== Phase 4.4B Email re-send (Parallel.ForEachAsync max degree 4) =====");await ResendThirtyAsync(ct);
        Console.WriteLine("===== Phase 4.7 Fire-and-forget investigation =====");await RunFireAndForgetDemoAsync(ct);
    }
    private void SnapshotLiveCheckout(){try{var target=Path.Combine(Environment.CurrentDirectory,"sample-output","phase4-checkout");if(Directory.Exists(target))Directory.Delete(target,true);CopyDirectory(_store.DataDirectory,target);Console.WriteLine($"[Snapshot] Phase 4 full checkout copied to {target}");}catch(Exception ex){Log.Warning($"Phase 4 checkout snapshot warning: {ex.Message}");}}
    private static void CopyDirectory(string source,string target){Directory.CreateDirectory(target);foreach(var f in Directory.GetFiles(source)){if(f.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))continue;File.Copy(f,Path.Combine(target,Path.GetFileName(f)),true);}foreach(var d in Directory.GetDirectories(source)){if(Path.GetFileName(d)=="fireforget-demo")continue;CopyDirectory(d,Path.Combine(target,Path.GetFileName(d)));}}
    private static async Task PrintCompletions(ChannelReader<(Order Order,double Seconds)> reader,int total,CancellationToken ct){int n=0;bool cancellationReported=false;await foreach(var r in reader.ReadAllAsync(CancellationToken.None)){if(ct.IsCancellationRequested&&!cancellationReported){Log.Warning("Cancellation requested; draining order completion notifications after compensation.");cancellationReported=true;}n++;var o=r.Order;string reason=string.IsNullOrWhiteSpace(o.FailureReason)?"":$" ({o.FailureReason})";Console.WriteLine($"[{n,2}/{total}] Order #{o.Id} -> {o.Status}{reason} after {r.Seconds:F1} s");}}
    private static void PrintStatusCounts(List<Order> rows){Console.WriteLine("Phase 4 status counts: "+string.Join(", ",Enum.GetValues<OrderStatus>().Select(s=>$"{s}={rows.Count(o=>o.Status==s)}")));}
    private async Task ResendThirtyAsync(CancellationToken ct)
    {
        IEmailService service=new EmailService(_store);var customer=(await new CustomerRepository(_store).GetByIdAsync(1,ct))!;var batch=Enumerable.Range(1001,30).Select(id=>new Order{Id=id,CustomerId=customer.Id,Status=OrderStatus.Completed,TotalAmount=25m,CreatedAt=DateTime.Now}).ToArray();await _store.UpdateAsync<EmailMessage>(DataFiles.Emails,rows=>{rows.RemoveAll(e=>e.OrderId>=1001&&e.OrderId<=1030);return rows;},ct);int active=0,max=0;var threads=new ConcurrentDictionary<int,byte>();var sw=Stopwatch.StartNew();await Parallel.ForEachAsync(batch,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=ct},async(o,token)=>{int now=Interlocked.Increment(ref active);int seen;do{seen=max;}while(now>seen&&(Interlocked.CompareExchange(ref max,now,seen)!=seen));threads.TryAdd(Environment.CurrentManagedThreadId,0);try{await service.SendConfirmationAsync(o,customer,token);}finally{Interlocked.Decrement(ref active);}});sw.Stop();var messages=(await new EmailRepository(_store).GetAllAsync(ct)).Where(e=>e.OrderId>=1001&&e.OrderId<=1030).ToList();Console.WriteLine($"Sent={messages.Count}, highest concurrent sends={max}, elapsed={sw.Elapsed.TotalSeconds:F2}s, distinct observed threads={threads.Count}, ThreadPool.ThreadCount={ThreadPool.ThreadCount}");
    }
    private async Task RunFireAndForgetDemoAsync(CancellationToken ct)
    {
        string dir=Path.Combine(_store.DataDirectory,"fireforget-demo");if(Directory.Exists(dir))Directory.Delete(dir,true);Directory.CreateDirectory(dir);var localStore=new JsonFileStore(dir);var customer=new Customer{Id=1,Name="Demo Customer",Email="demo@example.com",IsActive=true,LoyaltyTier=LoyaltyTier.Gold};var unobserved=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);void Handler(object? _,UnobservedTaskExceptionEventArgs e){unobserved.TrySetResult(e.Exception.GetBaseException().Message);e.SetObserved();}TaskScheduler.UnobservedTaskException+=Handler;
        try{Console.WriteLine("-- Broken version --");var brokenEmail=new EmailService(localStore,new Dictionary<int,int>{{9001,int.MaxValue}});var broken=new FireAndForgetInvestigation(new OrderRepository(localStore),brokenEmail,ct);await broken.ProcessOrderAsync(new Order{Id=9001,CustomerId=1,Status=OrderStatus.Completed,CreatedAt=DateTime.Now},customer,ct);Console.WriteLine("Caller returned; the email task is still running and will fail later.");await Task.Delay(1300,ct);GC.Collect();GC.WaitForPendingFinalizers();if(unobserved.Task.IsCompleted)Console.WriteLine($"Unobserved task exception observed by runtime hook: {await unobserved.Task}");else Console.WriteLine("The background task finished without being awaited; unobserved exceptions are timing/GC dependent.");Console.WriteLine("-- Fixed version --");var fixedEmail=new EmailService(localStore);var fixedProcessor=new FireAndForgetInvestigation(new OrderRepository(localStore),fixedEmail,ct);await fixedProcessor.ProcessOrderFixedAsync(new Order{Id=9002,CustomerId=1,Status=OrderStatus.Completed,CreatedAt=DateTime.Now},customer,ct);}
        finally{TaskScheduler.UnobservedTaskException-=Handler;try{Directory.Delete(dir,true);}catch{}}
    }
}
