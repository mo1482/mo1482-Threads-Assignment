using System.Diagnostics;
using OrderProcessingSystem.Shared.Logging;
using OrderProcessingSystem.Shared.Models;
using OrderProcessingSystem.Shared.Repositories;
using OrderProcessingSystem.Shared.Storage;

namespace OrderProcessingSystem.Phase1;

public sealed class OrderValidator
{
    private readonly CustomerRepository _customers; private readonly ProductRepository _products; private readonly CouponRepository _coupons;
    public OrderValidator(CustomerRepository c,ProductRepository p,CouponRepository k){_customers=c;_products=p;_coupons=k;}
    public async Task<Order> ValidateOrderAsync(Order order,CancellationToken ct)
    {
        var customer=await _customers.GetByIdAsync(order.CustomerId,ct);
        if(customer is null)return Fail(order,$"Customer #{order.CustomerId} not found");
        if(!customer.IsActive)return Fail(order,"Customer is inactive");
        var products=new Dictionary<int,Product>();
        foreach(var item in order.Items){var p=await _products.GetByIdAsync(item.ProductId,ct);if(p is null)return Fail(order,$"Product #{item.ProductId} not found");products[item.ProductId]=p;}
        foreach(var item in order.Items)if(products[item.ProductId].StockQuantity<item.Quantity)return Fail(order,$"Insufficient stock for '{products[item.ProductId].Name}'");
        if(order.CouponCode is not null){var coupon=await _coupons.GetByCodeAsync(order.CouponCode,ct);if(coupon is null)return Fail(order,$"Coupon '{order.CouponCode}' does not exist");if(coupon.ExpiryDate.Date<DateTime.Today)return Fail(order,$"Coupon '{order.CouponCode}' has expired");}
        order.Status=OrderStatus.Validated;order.FailureReason=null;return order;
    }
    private static Order Fail(Order o,string reason){o.Status=OrderStatus.Rejected;o.FailureReason=reason;return o;}
}
public sealed class SyncOrderValidator
{
    private readonly SyncCustomerRepository _customers;private readonly SyncProductRepository _products;private readonly SyncCouponRepository _coupons;
    public SyncOrderValidator(SyncCustomerRepository c,SyncProductRepository p,SyncCouponRepository k){_customers=c;_products=p;_coupons=k;}
    public Order ValidateOrder(Order order,CancellationToken ct)
    {
        var customer=_customers.GetById(order.CustomerId,ct);if(customer is null)return Fail(order,$"Customer #{order.CustomerId} not found");if(!customer.IsActive)return Fail(order,"Customer is inactive");
        var products=new Dictionary<int,Product>();foreach(var item in order.Items){var p=_products.GetById(item.ProductId,ct);if(p is null)return Fail(order,$"Product #{item.ProductId} not found");products[item.ProductId]=p;}
        foreach(var item in order.Items)if(products[item.ProductId].StockQuantity<item.Quantity)return Fail(order,$"Insufficient stock for '{products[item.ProductId].Name}'");
        if(order.CouponCode is not null){var c=_coupons.GetByCode(order.CouponCode,ct);if(c is null)return Fail(order,$"Coupon '{order.CouponCode}' does not exist");if(c.ExpiryDate.Date<DateTime.Today)return Fail(order,$"Coupon '{order.CouponCode}' has expired");}
        order.Status=OrderStatus.Validated;order.FailureReason=null;return order;
    }
    private static Order Fail(Order o,string s){o.Status=OrderStatus.Rejected;o.FailureReason=s;return o;}
}
public sealed class Phase1Runner
{
    private readonly JsonFileStore _store; private readonly DataSeeder _seeder;
    public Phase1Runner(JsonFileStore store,DataSeeder seeder){_store=store;_seeder=seeder;}
    public async Task RunAsync(CancellationToken ct)
    {
        await _seeder.ResetAsync(ct);
        var incoming=await new IncomingOrderRepository(_store).GetAllAsync(ct);
        var syncValidator=new SyncOrderValidator(new SyncCustomerRepository(_store),new SyncProductRepository(_store),new SyncCouponRepository(_store));
        Log.ResetThreadTracking();int syncPoolBefore=ThreadPool.ThreadCount;var sw=Stopwatch.StartNew();foreach(var source in incoming){ct.ThrowIfCancellationRequested();var copy=Clone(source);syncValidator.ValidateOrder(copy,ct);}sw.Stop();var sync=sw.Elapsed;int syncThreads=Log.DistinctThreads;int syncPoolAfter=ThreadPool.ThreadCount;
        await _seeder.ResetAsync(ct);
        incoming=await new IncomingOrderRepository(_store).GetAllAsync(ct);
        var validator=new OrderValidator(new CustomerRepository(_store),new ProductRepository(_store),new CouponRepository(_store));var orders=new OrderRepository(_store);
        Log.ResetThreadTracking();int asyncPoolBefore=ThreadPool.ThreadCount;sw=Stopwatch.StartNew();foreach(var order in incoming){ct.ThrowIfCancellationRequested();try{await validator.ValidateOrderAsync(order,ct);await orders.SaveAsync(order,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){order.Status=OrderStatus.Pending;order.FailureReason="Cancelled";try{await orders.SaveCompensationAsync(order,CancellationToken.None);}catch(Exception ex){Log.Error($"Could not save cancellation state for Order #{order.Id}: {ex.Message}");}throw;}}sw.Stop();var asyncTime=sw.Elapsed;int asyncThreads=Log.DistinctThreads;int asyncPoolAfter=ThreadPool.ThreadCount;
        var valid=await orders.GetByStatusAsync(OrderStatus.Validated,ct);var rejected=await orders.GetByStatusAsync(OrderStatus.Rejected,ct);var all=await orders.GetAllAsync(ct);
        Console.WriteLine("===== Validation Summary =====");Console.WriteLine($"Total Orders: {all.Count}");Console.WriteLine($"Validated:    {valid.Count}");Console.WriteLine($"Rejected:     {rejected.Count}");foreach(var o in rejected.OrderBy(x=>x.Id))Console.WriteLine($"  - Order #{o.Id}:  {o.FailureReason}");Console.WriteLine($"Sync Time:  {sync.TotalSeconds:F1} seconds; distinct logged threads={syncThreads}; ThreadPool.ThreadCount {syncPoolBefore}->{syncPoolAfter}");Console.WriteLine($"Async Time: {asyncTime.TotalSeconds:F1} seconds; distinct logged threads={asyncThreads}; ThreadPool.ThreadCount {asyncPoolBefore}->{asyncPoolAfter}");Console.WriteLine($"orders.json now contains {all.Count} records.");
    }
    private static Order Clone(Order o)=>new(){Id=o.Id,CustomerId=o.CustomerId,Items=o.Items.Select(x=>new OrderItem{ProductId=x.ProductId,Quantity=x.Quantity,UnitPrice=x.UnitPrice}).ToList(),CouponCode=o.CouponCode,Status=o.Status,CreatedAt=o.CreatedAt};
}
