using OrderProcessingSystem.Models;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Repositories;

public sealed class CouponRepository
{
    private readonly JsonFileStore _store;
    public CouponRepository(JsonFileStore store) => _store = store;

    public async Task<Coupon?> GetByCodeAsync(string code, CancellationToken ct)
    {
        const string source = "CouponRepository";
        await DelayStep.RunAsync(source, "Opening connection...", 200, ct);
        await DelayStep.RunAsync(source, $"Executing query for Coupon '{code}'...", Random.Shared.Next(300, 601), ct);
        var coupon = (await _store.ReadAsync<Coupon>(DataFiles.Coupons, ct))
            .FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        Log.Info(coupon is null ? $"{source} -> Coupon '{code}' not found." : $"{source} -> Coupon '{code}' found.");
        return coupon;
    }
}
