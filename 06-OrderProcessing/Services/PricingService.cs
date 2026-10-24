using OrderProcessingSystem.Models;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Services;

public sealed class PricingService
{
    private readonly int _phase;
    public PricingService(int phase) => _phase = phase;

    public async Task CalculateAsync(Order order, IReadOnlyDictionary<int, Product> products, Coupon? coupon, CancellationToken ct)
    {
        const string source = "PricingService";
        await DelayStep.RunAsync(source, $"DNS/handshake for Order #{order.Id}...", 150, ct);
        await DelayStep.RunAsync(source, $"Sending pricing/discount request for Order #{order.Id}...", 300, ct);
        await DelayStep.RunAsync(source, $"Waiting for pricing response for Order #{order.Id}...", Random.Shared.Next(500, 1501), ct);

        if (_phase == 2 && order.Id == 7)
            throw new InvalidOperationException("Simulated pricing service failure for Order #7 (Phase 2 requirement).");

        decimal subTotal = 0m;
        foreach (var item in order.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
                throw new InvalidOperationException($"Product #{item.ProductId} was not found during pricing.");
            item.UnitPrice = product.Price;
            subTotal += product.Price * item.Quantity;
        }
        order.SubTotal = subTotal;
        order.Discount = coupon is null ? 0m : Math.Round(subTotal * coupon.DiscountPercent / 100m, 2, MidpointRounding.AwayFromZero);
        Log.Info($"{source} -> Order #{order.Id}: subtotal {order.SubTotal:C}, discount {order.Discount:C}.");
    }
}
