using OrderProcessingSystem.Models;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Services;

public sealed class ShippingService
{
    public async Task<decimal> CalculateAsync(Order order, IReadOnlyDictionary<int, Product> products, CancellationToken ct)
    {
        const string source = "ShippingService";
        await DelayStep.RunAsync(source, $"DNS/handshake for Order #{order.Id}...", 150, ct);
        await DelayStep.RunAsync(source, $"Sending shipping quote request for Order #{order.Id}...", 300, ct);
        await DelayStep.RunAsync(source, $"Waiting for shipping response for Order #{order.Id}...", Random.Shared.Next(500, 1501), ct);

        decimal totalWeight = order.Items.Sum(item => products[item.ProductId].WeightKg * item.Quantity);
        // Assignment brief does not provide shipping price bands; deterministic bands are documented in README.
        decimal cost = totalWeight <= 1m ? 5m : totalWeight <= 5m ? 10m : 20m;
        Log.Info($"{source} -> Order #{order.Id}: total weight {totalWeight:0.##} kg, shipping {cost:C}.");
        return cost;
    }
}
