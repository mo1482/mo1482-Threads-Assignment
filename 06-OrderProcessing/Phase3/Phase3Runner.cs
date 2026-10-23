using System.Collections.Concurrent;
using System.Diagnostics;
using OrderProcessingSystem.Shared.Exceptions;
using OrderProcessingSystem.Shared.Logging;
using OrderProcessingSystem.Shared.Models;
using OrderProcessingSystem.Shared.Repositories;
using OrderProcessingSystem.Shared.Storage;
using OrderProcessingSystem.Phase2;
using OrderProcessingSystem.Phase4;

namespace OrderProcessingSystem.Phase3;

public interface IPaymentGateway { Task<PaymentResult> ProcessPaymentAsync(int orderId,decimal amount,CancellationToken ct); Task RefundAsync(string transactionId,CancellationToken ct); }
public interface IEmailService { Task SendConfirmationAsync(Order order,Customer customer,CancellationToken ct); }
public sealed class CustomerService
{
    private readonly CustomerRepository _repo;public CustomerService(CustomerRepository r)=>_repo=r;
    public async Task<Customer> GetCustomerAsync(int id,CancellationToken ct)=>await _repo.GetByIdAsync(id,ct)??throw new CustomerNotFoundException($"Customer #{id} not found");
}
public sealed class InventoryService
{
    private readonly ProductRepository _repo;public InventoryService(ProductRepository r)=>_repo=r;
    public Task<bool> ReserveStockAsync(Order order,CancellationToken ct)=>_repo.ReserveItemsAsync(order.Items,ct);
    public Task ReleaseStockAsync(Order order,CancellationToken ct)=>_repo.ReleaseItemsAsync(order.Items,ct);
}
public sealed class PricingServiceV3
{
    private readonly OrderProcessingSystem.Phase2.PricingService _inner;
    public PricingServiceV3(JsonFileStore store,CustomerRepository c,CouponRepository k)=>_inner=new(store,c,k,Array.Empty<int>());
    public async Task<decimal> CalculateTotalAsync(Order order,CancellationToken ct){await _inner.PriceOrderAsync(order,ct);return order.TotalAmount;}
}
public sealed class PaymentGateway : IPaymentGateway
{
    private readonly JsonFileStore _store;private readonly HashSet<int> _timeout;private readonly HashSet<int> _decline;
    public PaymentGateway(JsonFileStore s,IEnumerable<int>? timeoutOrderIds=null,IEnumerable<int>? declineOrderIds=null){_store=s;_timeout=(timeoutOrderIds??Array.Empty<int>()).ToHashSet();_decline=(declineOrderIds??Array.Empty<int>()).ToHashSet();}
    public async Task<PaymentResult> ProcessPaymentAsync(int orderId,decimal amount,CancellationToken ct)
    {
        string tx="TXN-"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await Latency.StepAsync("PaymentGateway",$"Authorize Order #{orderId}...",800,ct);await Latency.StepAsync("PaymentGateway",$"Fraud check Order #{orderId}...",500,ct);
        if(_decline.Contains(orderId)){var declined=new PaymentTransaction{OrderId=orderId,Amount=amount,Status=PaymentStatus.Declined,TransactionId=tx,CreatedAt=DateTime.Now};await _store.UpdateAsync<PaymentTransaction>(DataFiles.Payments,rows=>{rows.Add(declined);return rows;},ct);return new(){Success=false,TransactionId=tx,Status=PaymentStatus.Declined,Message="Card declined by issuer"};}
        await Latency.StepAsync("PaymentGateway",$"Capture Order #{orderId}...",_timeout.Contains(orderId)?5000:700,ct);
        var payment=new PaymentTransaction{OrderId=orderId,Amount=amount,Status=PaymentStatus.Captured,TransactionId=tx,CreatedAt=DateTime.Now};await _store.UpdateAsync<PaymentTransaction>(DataFiles.Payments,rows=>{rows.Add(payment);return rows;},ct);
        return new(){Success=true,TransactionId=tx,Status=PaymentStatus.Captured,Message="Captured"};
    }
    public async Task RefundAsync(string transactionId,CancellationToken ct){await Latency.StepAsync("PaymentGateway","Refund DNS/handshake...",150,ct);await Latency.StepAsync("PaymentGateway",$"Refunding transaction {transactionId}...",300,ct);await _store.UpdateAsync<PaymentTransaction>(DataFiles.Payments,data=>{var source=data.FirstOrDefault(x=>x.TransactionId==transactionId);if(source is not null)source.Status=PaymentStatus.Refunded;return data;},ct);}
}
public sealed class EmailService : IEmailService
{
    private readonly JsonFileStore _store;private readonly HashSet<int> _failFor;private readonly Dictionary<int,int> _configuredFailures;private readonly ConcurrentDictionary<int,int> _attempts=new();
    public EmailService(JsonFileStore s,IReadOnlyDictionary<int,int>? failAttempts=null,bool disableFailures=false){_store=s;_configuredFailures=disableFailures?new():((failAttempts??new Dictionary<int,int>()).ToDictionary(kv=>kv.Key,kv=>kv.Value));_failFor=_configuredFailures.Keys.ToHashSet();}
    public async Task SendConfirmationAsync(Order order,Customer customer,CancellationToken ct)
    {
        int attempt=_attempts.AddOrUpdate(order.Id,1,(_,v)=>v+1);await Latency.StepAsync("EmailService",$"Render template for Order #{order.Id}, attempt {attempt}...",200,ct);await Latency.StepAsync("EmailService","Connect to SMTP...",300,ct);await Latency.StepAsync("EmailService",$"Send email for Order #{order.Id}...",500,ct);
        if(_failFor.Contains(order.Id)&&attempt<=_configuredFailures[order.Id])throw new SmtpUnavailableException($"SMTP unavailable for Order #{order.Id}, attempt {attempt}");
        var msg=new EmailMessage{Id=Guid.NewGuid(),OrderId=order.Id,To=customer.Email,Subject=$"Order #{order.Id} confirmation",Body=$"Your order #{order.Id} is confirmed. Total: {order.TotalAmount:F2}",SentAt=DateTime.Now,Attempts=attempt};
        await _store.UpdateAsync<EmailMessage>(DataFiles.Emails,rows=>{if(!rows.Any(x=>x.OrderId==order.Id))rows.Add(msg);return rows;},ct);
    }
    public async Task SendConfirmationWithRetryAsync(Order order,Customer customer,CancellationToken ct)
    {
        int[] retryDelays={500,1000,2000};string? error=null;
        for(int attempt=1;attempt<=4;attempt++){
            if(attempt>1)await Task.Delay(retryDelays[attempt-2],ct);
            try{await SendConfirmationAsync(order,customer,ct);if(attempt>1)Log.Success($"Email for Order #{order.Id} succeeded on attempt {attempt}");return;}
            catch(SmtpUnavailableException ex){error=ex.Message;Log.Error($"Email for Order #{order.Id} attempt {attempt} failed: {ex.Message}");if(attempt==4){var failed=new EmailMessage{Id=Guid.NewGuid(),OrderId=order.Id,To=customer.Email,Subject=$"Order #{order.Id} confirmation",Body=$"Order #{order.Id} confirmation queued for retry",SentAt=DateTime.Now,Attempts=4,LastError=error};await _store.UpdateAsync<EmailMessage>(DataFiles.EmailRetry,rows=>{int i=rows.FindIndex(x=>x.OrderId==order.Id);if(i<0)rows.Add(failed);else rows[i]=failed;return rows;},ct);Log.Warning($"Email for Order #{order.Id} queued for retry later");return;}}
        }
    }
}
public sealed class OrderProcessor
{
    private readonly CustomerService _customers;private readonly InventoryService _inventory;private readonly PricingServiceV3 _pricing;private readonly IPaymentGateway _payments;private readonly OrderRepository _orders;private readonly IEmailService _emails;private readonly EmailService _retryEmails;private readonly ProductRepository _products;
    public OrderProcessor(CustomerService c,InventoryService i,PricingServiceV3 p,IPaymentGateway payment,OrderRepository orders,IEmailService emails,EmailService retryEmails,ProductRepository products){_customers=c;_inventory=i;_pricing=p;_payments=payment;_orders=orders;_emails=emails;_retryEmails=retryEmails;_products=products;}
    public async Task<bool> ProcessOrderAsync(Order order,CancellationToken ct)
    {
        var total=Stopwatch.StartNew();bool stock=false;string? tx=null;Customer? customer=null;bool captured=false;
        try{
            var step=Stopwatch.StartNew();Log.Info($"Order #{order.Id} -> Step 1/6 Customer lookup ...");try{customer=await _customers.GetCustomerAsync(order.CustomerId,ct);}catch(CustomerNotFoundException ex){order.Status=OrderStatus.Rejected;order.FailureReason=ex.Message;await _orders.SaveAsync(order,ct);Log.Error($"Order #{order.Id} -> Step 1/6 Customer lookup REJECTED in {step.ElapsedMilliseconds} ms");return false;}if(!customer.IsActive){order.Status=OrderStatus.Rejected;order.FailureReason="Customer is inactive";await _orders.SaveAsync(order,ct);Log.Error($"Order #{order.Id} -> Step 1/6 Customer inactive in {step.ElapsedMilliseconds} ms");return false;}Log.Success($"Order #{order.Id} -> Step 1/6 Customer OK in {step.ElapsedMilliseconds} ms");
            step.Restart();Log.Info($"Order #{order.Id} -> Step 2/6 Inventory reservation ...");stock=await _inventory.ReserveStockAsync(order,ct);if(!stock){var p=await FindInsufficientProductAsync(order,ct);order.Status=OrderStatus.Rejected;order.FailureReason=$"Insufficient stock for '{p}'";await _orders.SaveAsync(order,ct);Log.Error($"Order #{order.Id} -> Step 2/6 Inventory REJECTED in {step.ElapsedMilliseconds} ms");return false;}Log.Success($"Order #{order.Id} -> Step 2/6 Inventory OK in {step.ElapsedMilliseconds} ms");
            step.Restart();Log.Info($"Order #{order.Id} -> Step 3/6 Pricing ...");try{await _pricing.CalculateTotalAsync(order,ct);}catch(InvalidCouponException ex){await _inventory.ReleaseStockAsync(order,CancellationToken.None);stock=false;order.Status=OrderStatus.Rejected;order.FailureReason=ex.Message;await _orders.SaveAsync(order,ct);Log.Error($"Order #{order.Id} -> Step 3/6 Pricing REJECTED in {step.ElapsedMilliseconds} ms");return false;}Log.Success($"Order #{order.Id} -> Step 3/6 Pricing OK in {step.ElapsedMilliseconds} ms (total {order.TotalAmount:F2})");
            step.Restart();Log.Info($"Order #{order.Id} -> Step 4/6 Payment ...");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);PaymentResult payment;
            try{payment=await _payments.ProcessPaymentAsync(order.Id,order.TotalAmount,linked.Token);}catch(OperationCanceledException) when(!ct.IsCancellationRequested){await _inventory.ReleaseStockAsync(order,CancellationToken.None);stock=false;order.Status=OrderStatus.PaymentFailed;order.FailureReason="Payment gateway timeout (3s)";await _orders.SaveAsync(order,CancellationToken.None);Log.Error($"Order #{order.Id} -> Step 4/6 Payment TIMEOUT after {step.ElapsedMilliseconds} ms, released");return false;}
            if(!payment.Success){await _inventory.ReleaseStockAsync(order,CancellationToken.None);stock=false;order.Status=OrderStatus.PaymentFailed;order.FailureReason=payment.Message;order.TransactionId=payment.TransactionId;await _orders.SaveAsync(order,ct);Log.Error($"Order #{order.Id} -> Step 4/6 Payment DECLINED in {step.ElapsedMilliseconds} ms: {payment.Message}");return false;}
            tx=payment.TransactionId;captured=true;order.TransactionId=tx;Log.Success($"Order #{order.Id} -> Step 4/6 Payment OK ({tx}) in {step.ElapsedMilliseconds} ms");
            step.Restart();order.Status=OrderStatus.Completed;order.FailureReason=null;try{await _orders.SaveAsync(order,ct);}catch(DatabaseWriteException){if(ct.IsCancellationRequested){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(captured&&tx is not null)try{await _payments.RefundAsync(tx,cleanup.Token);}catch(Exception e){Log.Error($"Order #{order.Id} cancellation refund failed: {e.Message}");}captured=false;if(stock)try{await _inventory.ReleaseStockAsync(order,cleanup.Token);}catch(Exception e){Log.Error($"Order #{order.Id} cancellation stock release failed: {e.Message}");}stock=false;order.Status=OrderStatus.Pending;order.FailureReason="Cancelled";try{await _orders.SaveCompensationAsync(order,cleanup.Token);}catch(Exception e){Log.Error($"Order #{order.Id} cancellation save failed: {e.Message}");}return false;}await _payments.RefundAsync(tx!,CancellationToken.None);captured=false;await _inventory.ReleaseStockAsync(order,CancellationToken.None);stock=false;order.Status=OrderStatus.Failed;order.FailureReason=$"Database save failed after payment; payment {tx} refunded";await _orders.SaveCompensationAsync(order,CancellationToken.None);Log.Error($"Order #{order.Id} -> Step 5/6 Save failed; transaction {tx} refunded in {step.ElapsedMilliseconds} ms");return false;}Log.Success($"Order #{order.Id} -> Step 5/6 Save Completed in {step.ElapsedMilliseconds} ms");
            step.Restart();try{if(_emails is EmailService es)await es.SendConfirmationWithRetryAsync(order,customer,ct);else await _emails.SendConfirmationAsync(order,customer,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch(Exception ex){Log.Error($"Order #{order.Id} email failure did not change paid order status: {ex.Message}");}Log.Info($"Order #{order.Id} -> Step 6/6 Email finished in {step.ElapsedMilliseconds} ms; whole order {total.ElapsedMilliseconds} ms");stock=false;return true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(captured&&tx is not null)try{await _payments.RefundAsync(tx,cleanup.Token);}catch(Exception e){Log.Error($"Order #{order.Id} refund compensation failed: {e.Message}");}if(stock)try{await _inventory.ReleaseStockAsync(order,cleanup.Token);}catch(Exception e){Log.Error($"Order #{order.Id} stock compensation failed: {e.Message}");}order.Status=OrderStatus.Pending;order.FailureReason="Cancelled";try{await _orders.SaveCompensationAsync(order,cleanup.Token);}catch(Exception e){Log.Error($"Order #{order.Id} cancellation save failed: {e.Message}");}Log.Warning($"Order #{order.Id} cancelled and compensated after {total.ElapsedMilliseconds} ms");return false;}
        catch(Exception ex){using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(captured&&tx is not null)try{await _payments.RefundAsync(tx,cleanup.Token);}catch(Exception refundError){Log.Error($"Order #{order.Id} refund compensation failed: {refundError.Message}");}if(stock)try{await _inventory.ReleaseStockAsync(order,cleanup.Token);}catch(Exception stockError){Log.Error($"Order #{order.Id} stock compensation failed: {stockError.Message}");}if(ct.IsCancellationRequested){order.Status=OrderStatus.Pending;order.FailureReason="Cancelled";}else{order.Status=OrderStatus.Failed;order.FailureReason=ex.Message;}try{await _orders.SaveCompensationAsync(order,cleanup.Token);}catch(Exception saveError){Log.Error($"Order #{order.Id} compensation save failed: {saveError.Message}");}if(ct.IsCancellationRequested)Log.Warning($"Order #{order.Id} cancelled and compensated after {total.ElapsedMilliseconds} ms");else Log.Error($"Order #{order.Id} -> Failed: {ex.Message}");return false;}
    }
    private async Task<string> FindInsufficientProductAsync(Order o,CancellationToken ct){foreach(var i in o.Items){var p=await _products.GetByIdAsync(i.ProductId,ct);if(p is null||p.StockQuantity<i.Quantity)return p?.Name??$"Product #{i.ProductId}";}return o.Items.FirstOrDefault() is { } x?(await _products.GetByIdAsync(x.ProductId,ct))?.Name??$"Product #{x.ProductId}":"unknown product";}
}
public sealed class Phase3Runner
{
    private readonly JsonFileStore _store;private readonly DataSeeder _seeder;public Phase3Runner(JsonFileStore s,DataSeeder d){_store=s;_seeder=d;}
    public async Task RunAsync(CancellationToken ct)
    {
        await _seeder.ResetAsync(ct);var sw=Stopwatch.StartNew();var incoming=await new IncomingOrderRepository(_store).GetAllAsync(ct);var product=new ProductRepository(_store);var customer=new CustomerRepository(_store);var coupons=new CouponRepository(_store);var orders=new OrderRepository(_store,new[]{13});var email=new EmailService(_store,new Dictionary<int,int>{{3,2},{17,int.MaxValue}});var processor=new OrderProcessor(new CustomerService(customer),new InventoryService(product),new PricingServiceV3(_store,customer,coupons),new PaymentGateway(_store,new[]{5},new[]{8}),orders,email,email,product);
        var tasks=incoming.Select(o=>processor.ProcessOrderAsync(o,ct)).ToList();for(int i=0;i<tasks.Count;i++){try{await tasks[i];}catch(OperationCanceledException) when(ct.IsCancellationRequested){/* OrderProcessor persists cancellation compensation. */}catch(Exception ex){Log.Error($"Unhandled Order #{incoming[i].Id}: {ex.Message}");}}
        sw.Stop();var saved=await orders.GetAllAsync(CancellationToken.None);Console.WriteLine("===== Phase 3 Final Orders =====");Console.WriteLine("Order | Status | Total | Reason");foreach(var o in saved.OrderBy(x=>x.Id))Console.WriteLine($"{o.Id,5} | {o.Status,-13} | {o.TotalAmount,8:F2} | {o.FailureReason}");Console.WriteLine($"Phase 3 elapsed {sw.Elapsed.TotalSeconds:F2}s; ThreadPool.ThreadCount={ThreadPool.ThreadCount}; saved={saved.Count}");if(ct.IsCancellationRequested){int cancelled=saved.Count(x=>x.Status==OrderStatus.Pending&&x.FailureReason=="Cancelled");Console.WriteLine($"Run cancelled — {saved.Count-cancelled} orders finished, {cancelled} cancelled");return;}PrintSummary(saved);SnapshotCheckout();await RunSyncAsyncComparisonAsync(ct);
    }
    private static void PrintSummary(List<Order> x)=>Console.WriteLine($"Summary: Completed={x.Count(o=>o.Status==OrderStatus.Completed)}, Rejected={x.Count(o=>o.Status==OrderStatus.Rejected)}, PaymentFailed={x.Count(o=>o.Status==OrderStatus.PaymentFailed)}, Failed={x.Count(o=>o.Status==OrderStatus.Failed)}, Pending={x.Count(o=>o.Status==OrderStatus.Pending)}");
    private void SnapshotCheckout(){try{foreach(var folder in new[]{"phase3","phase3-checkout"}){var target=Path.Combine(Environment.CurrentDirectory,"sample-output",folder);if(Directory.Exists(target))Directory.Delete(target,true);Copy(_store.DataDirectory,target);}Console.WriteLine("[Snapshot] Full Phase 3 checkout files copied to sample-output/phase3 and phase3-checkout");}catch(Exception ex){Log.Warning($"Phase 3 checkout snapshot warning: {ex.Message}");}}
    private static void Copy(string src,string dst){Directory.CreateDirectory(dst);foreach(var f in Directory.GetFiles(src)){if(f.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))continue;File.Copy(f,Path.Combine(dst,Path.GetFileName(f)),true);}foreach(var d in Directory.GetDirectories(src)){if(Path.GetFileName(d)=="fireforget-demo")continue;Copy(d,Path.Combine(dst,Path.GetFileName(d)));}}
    private async Task RunSyncAsyncComparisonAsync(CancellationToken ct)
    {
        Console.WriteLine("===== Phase 3.6 Sync (10 dedicated threads) vs async (10 tasks) =====");await _seeder.ResetAsync(ct);var first=(await new IncomingOrderRepository(_store).GetAllAsync(ct)).Take(10).ToList();var syncProcessor=new SyncOrderProcessor(_store);Log.ResetThreadTracking();int poolBefore=ThreadPool.ThreadCount;var threads=first.Select(o=>new Thread(()=>{try{syncProcessor.Process(o,ct);}catch(OperationCanceledException){}}){IsBackground=false}).ToArray();var watch=Stopwatch.StartNew();foreach(var t in threads)t.Start();foreach(var t in threads)t.Join();watch.Stop();int syncThreads=Log.DistinctThreads;int poolAfterSync=ThreadPool.ThreadCount;var syncSaved=await new OrderRepository(_store).GetAllAsync(CancellationToken.None);Console.WriteLine($"SYNC 10 threads: {watch.Elapsed.TotalSeconds:F2}s; distinct logged threads={syncThreads}; ThreadPool.ThreadCount {poolBefore}->{poolAfterSync}; Completed={syncSaved.Count(o=>o.Status==OrderStatus.Completed)}, Rejected={syncSaved.Count(o=>o.Status==OrderStatus.Rejected)}");
        if(ct.IsCancellationRequested){int cancelled=syncSaved.Count(o=>o.Status==OrderStatus.Pending&&o.FailureReason=="Cancelled");Console.WriteLine($"Run cancelled — {syncSaved.Count-cancelled} orders finished, {cancelled} cancelled");return;}await _seeder.ResetAsync(ct);first=(await new IncomingOrderRepository(_store).GetAllAsync(ct)).Take(10).ToList();var pr=new ProductRepository(_store);var cr=new CustomerRepository(_store);var em=new EmailService(_store,disableFailures:true);var or=new OrderRepository(_store);var proc=new OrderProcessor(new CustomerService(cr),new InventoryService(pr),new PricingServiceV3(_store,cr,new CouponRepository(_store)),new PaymentGateway(_store),or,em,em,pr);Log.ResetThreadTracking();poolBefore=ThreadPool.ThreadCount;watch.Restart();var tasks=first.Select(o=>proc.ProcessOrderAsync(o,ct)).ToArray();foreach(var t in tasks){try{await t;}catch(OperationCanceledException) when(ct.IsCancellationRequested){}}watch.Stop();int asyncThreads=Log.DistinctThreads;int poolAfterAsync=ThreadPool.ThreadCount;var asyncSaved=await or.GetAllAsync(CancellationToken.None);Console.WriteLine($"ASYNC 10 tasks: {watch.Elapsed.TotalSeconds:F2}s; distinct logged threads={asyncThreads}; ThreadPool.ThreadCount {poolBefore}->{poolAfterAsync}; Completed={asyncSaved.Count(o=>o.Status==OrderStatus.Completed)}, Rejected={asyncSaved.Count(o=>o.Status==OrderStatus.Rejected)}");if(ct.IsCancellationRequested){int cancelled=asyncSaved.Count(o=>o.Status==OrderStatus.Pending&&o.FailureReason=="Cancelled");Console.WriteLine($"Run cancelled — {asyncSaved.Count-cancelled} orders finished, {cancelled} cancelled");}
    }
}
