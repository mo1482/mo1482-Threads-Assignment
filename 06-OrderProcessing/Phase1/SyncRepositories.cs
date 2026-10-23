using OrderProcessingSystem.Shared.Logging;
using OrderProcessingSystem.Shared.Models;
using OrderProcessingSystem.Shared.Storage;

namespace OrderProcessingSystem.Phase1;

// Synchronous comparison implementations intentionally use Thread.Sleep through StepSync.
public sealed class SyncCustomerRepository
{
    private readonly JsonFileStore _store;
    public SyncCustomerRepository(JsonFileStore store) => _store = store;
    public Customer? GetById(int id, CancellationToken ct)
    {
        Latency.StepSync(nameof(SyncCustomerRepository), "Opening connection...", 200, ct);
        Latency.StepSync(nameof(SyncCustomerRepository), $"Executing query for Customer #{id}...", Random.Shared.Next(300, 601), ct);
        var result = _store.Read<Customer>(DataFiles.Customers).FirstOrDefault(c => c.Id == id);
        Log.Info(result is null ? $"{nameof(SyncCustomerRepository)} -> Customer #{id} not found." : $"{nameof(SyncCustomerRepository)} -> Customer #{id} found ({result.Name}).");
        return result;
    }
}

public sealed class SyncProductRepository
{
    private readonly JsonFileStore _store;
    public SyncProductRepository(JsonFileStore store) => _store = store;
    public Product? GetById(int id, CancellationToken ct)
    {
        Latency.StepSync(nameof(SyncProductRepository), "Opening connection...", 200, ct);
        Latency.StepSync(nameof(SyncProductRepository), $"Executing query for Product #{id}...", Random.Shared.Next(300, 601), ct);
        return _store.Read<Product>(DataFiles.Products).FirstOrDefault(p => p.Id == id);
    }
}

public sealed class SyncCouponRepository
{
    private readonly JsonFileStore _store;
    public SyncCouponRepository(JsonFileStore store) => _store = store;
    public Coupon? GetByCode(string code, CancellationToken ct)
    {
        Latency.StepSync(nameof(SyncCouponRepository), "Opening connection...", 200, ct);
        Latency.StepSync(nameof(SyncCouponRepository), $"Executing query for Coupon '{code}'...", Random.Shared.Next(300, 601), ct);
        return _store.Read<Coupon>(DataFiles.Coupons).FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
    }
}
