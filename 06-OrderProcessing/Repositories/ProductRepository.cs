using OrderProcessingSystem.Models;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Repositories;

public sealed class ProductRepository
{
    private readonly JsonFileStore _store;
    public ProductRepository(JsonFileStore store) => _store = store;

    public async Task<Product?> GetByIdAsync(int id, CancellationToken ct)
    {
        const string source = "ProductRepository";
        await DelayStep.RunAsync(source, "Opening connection...", 200, ct);
        await DelayStep.RunAsync(source, $"Executing query for Product #{id}...", Random.Shared.Next(300, 601), ct);
        var product = (await _store.ReadAsync<Product>(DataFiles.Products, ct)).FirstOrDefault(p => p.Id == id);
        Log.Info(product is null ? $"{source} -> Product #{id} not found." : $"{source} -> Product #{id} found ({product.Name}).");
        return product;
    }

    public async Task<bool> ReserveAsync(int orderId, IReadOnlyCollection<OrderItem> items, CancellationToken ct)
    {
        const string source = "ProductRepository";
        await DelayStep.RunAsync(source, $"Opening connection to reserve stock for Order #{orderId}...", 200, ct);
        await DelayStep.RunAsync(source, "Beginning stock transaction...", 100, ct);
        await DelayStep.RunAsync(source, $"Updating stock for Order #{orderId}...", 400, ct);
        bool reserved = false;
        await _store.UpdateAsync<Product>(DataFiles.Products, products =>
        {
            var quantities = items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
            bool available = quantities.All(q => products.Any(p => p.Id == q.Key && p.StockQuantity >= q.Value));
            if (!available) return products;
            foreach (var q in quantities)
                products.Single(p => p.Id == q.Key).StockQuantity -= q.Value;
            reserved = true;
            return products;
        }, ct);
        try
        {
            await DelayStep.RunAsync(source, $"Committing stock reservation for Order #{orderId}...", 200, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && reserved)
        {
            // The stock update already committed; return the reservation fact so the processor can release it.
            Log.Error($"{source} -> Cancellation arrived after reserving stock for Order #{orderId}; handing reservation to compensation.");
        }
        Log.Info(reserved ? $"{source} -> Stock reserved for Order #{orderId}." : $"{source} -> Insufficient stock for Order #{orderId}.");
        return reserved;
    }

    public async Task ReleaseAsync(int orderId, IReadOnlyCollection<OrderItem> items, CancellationToken ct)
    {
        const string source = "ProductRepository";
        await DelayStep.RunAsync(source, $"Opening connection to release stock for Order #{orderId}...", 200, ct);
        await DelayStep.RunAsync(source, "Beginning stock release transaction...", 100, ct);
        await DelayStep.RunAsync(source, $"Updating stock quantities for Order #{orderId}...", 400, ct);
        await _store.UpdateAsync<Product>(DataFiles.Products, products =>
        {
            foreach (var q in items.GroupBy(i => i.ProductId).Select(g => new { ProductId = g.Key, Quantity = g.Sum(i => i.Quantity) }))
            {
                var product = products.FirstOrDefault(p => p.Id == q.ProductId);
                if (product is not null) product.StockQuantity += q.Quantity;
            }
            return products;
        }, ct);
        try
        {
            await DelayStep.RunAsync(source, $"Committing stock release for Order #{orderId}...", 200, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The stock file update already committed, so report release as complete to prevent double release.
            Log.Error($"{source} -> Cancellation arrived after stock release for Order #{orderId}; release remains committed.");
        }
        Log.Success($"{source} -> Stock released for Order #{orderId}.");
    }
}
