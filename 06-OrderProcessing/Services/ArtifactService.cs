using System.Text;
using System.Text.Json;
using OrderProcessingSystem.Models;
using OrderProcessingSystem.Repositories;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Services;

public sealed class ArtifactService
{
    private readonly JsonFileStore _store;
    private readonly OrderRepository _orders;
    public ArtifactService(JsonFileStore store, OrderRepository orders) { _store = store; _orders = orders; }

    public async Task WriteInvoiceAsync(Order order, CancellationToken ct)
    {
        await DelayStep.RunAsync("InvoiceService", $"Preparing invoice for Order #{order.Id}...", 50, ct);
        await DelayStep.RunAsync("InvoiceService", $"Formatting invoice for Order #{order.Id}...", 50, ct);
        string dir = Path.Combine(_store.DataDirectory, DataFiles.InvoicesDirectory);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"invoice_{order.Id}.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"INVOICE — ORDER #{order.Id}");
        sb.AppendLine($"Customer ID: {order.CustomerId}");
        sb.AppendLine($"Created: {order.CreatedAt:O}");
        sb.AppendLine("Items:");
        foreach (var item in order.Items)
            sb.AppendLine($"  Product #{item.ProductId} x {item.Quantity} @ {item.UnitPrice:C} = {(item.UnitPrice * item.Quantity):C}");
        sb.AppendLine($"Subtotal: {order.SubTotal:C}");
        sb.AppendLine($"Discount: {order.Discount:C}");
        sb.AppendLine($"Shipping: {order.ShippingCost:C}");
        sb.AppendLine($"TOTAL: {order.TotalAmount:C}");
        sb.AppendLine($"Status: {order.Status}");
        await WriteAtomicallyAsync(path, sb.ToString(), ct);
        Log.Success($"InvoiceService -> Wrote {Path.GetFileName(path)}.");
    }

    public async Task<Report> GenerateReportAsync(CancellationToken ct)
    {
        await DelayStep.RunAsync("ReportService", "Preparing report query...", 100, ct);
        await DelayStep.RunAsync("ReportService", "Aggregating order statistics...", 100, ct);
        var orders = await _orders.GetAllAsync(ct);
        var products = await _store.ReadAsync<Product>(DataFiles.Products, ct);
        var completed = orders.Where(o => o.Status == OrderStatus.Completed).ToList();
        var selling = completed.SelectMany(o => o.Items)
            .GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(i => i.Quantity) })
            .OrderByDescending(x => x.Quantity).ThenBy(x => x.ProductId).FirstOrDefault();
        var top = selling is null ? null : products.FirstOrDefault(p => p.Id == selling.ProductId);
        var report = new Report
        {
            TotalOrders = orders.Count,
            TotalRevenue = completed.Sum(o => o.TotalAmount),
            CompletedCount = orders.Count(o => o.Status == OrderStatus.Completed),
            RejectedCount = orders.Count(o => o.Status == OrderStatus.Rejected),
            FailedCount = orders.Count(o => o.Status == OrderStatus.Failed),
            PaymentFailedCount = orders.Count(o => o.Status == OrderStatus.PaymentFailed),
            TopSellingProduct = top?.Name ?? "None",
            TopSellingQuantity = selling?.Quantity ?? 0,
            AverageOrderValue = completed.Count == 0 ? 0m : Math.Round(completed.Sum(o => o.TotalAmount) / completed.Count, 2),
            GeneratedAt = DateTime.Now
        };
        string dir = Path.Combine(_store.DataDirectory, DataFiles.ReportsDirectory);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        string json = JsonSerializer.Serialize(report, JsonFileStore.JsonOptions);
        await WriteAtomicallyAsync(path, json, ct);
        Log.Success($"ReportService -> Wrote {Path.GetFileName(path)}.");
        return report;
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(path)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
