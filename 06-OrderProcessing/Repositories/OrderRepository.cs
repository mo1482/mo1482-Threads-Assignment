using OrderProcessingSystem.Models;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Repositories;

public sealed class OrderRepository
{
    private readonly JsonFileStore _store;
    private readonly int _phase;
    private int _order13FailureTriggered;

    public OrderRepository(JsonFileStore store, int phase) { _store = store; _phase = phase; }

    public async Task SaveAsync(Order order, CancellationToken ct) => await SaveCoreAsync(order, allowFault: true, ct: ct);
    public async Task SaveCompensationAsync(Order order, CancellationToken ct) => await SaveCoreAsync(order, allowFault: false, ct: ct);

    private async Task SaveCoreAsync(Order order, bool allowFault, CancellationToken ct)
    {
        const string source = "OrderRepository";
        await DelayStep.RunAsync(source, $"Opening connection to save Order #{order.Id}...", 200, ct);
        await DelayStep.RunAsync(source, "Beginning transaction...", 100, ct);
        await DelayStep.RunAsync(source, $"Inserting/updating Order #{order.Id}...", 400, ct);

        if (allowFault && _phase == 3 && order.Id == 13 && order.Status == OrderStatus.Completed &&
            !string.IsNullOrWhiteSpace(order.TransactionId) && Interlocked.CompareExchange(ref _order13FailureTriggered, 1, 0) == 0)
            throw new IOException("Simulated database save failure after payment capture for Order #13.");

        await _store.UpdateAsync<Order>(DataFiles.Orders, rows =>
        {
            int index = rows.FindIndex(x => x.Id == order.Id);
            if (index >= 0) rows[index] = order;
            else rows.Add(order);
            return rows;
        }, ct);
        await DelayStep.RunAsync(source, $"Committing Order #{order.Id}...", 200, ct);
        Log.Success($"{source} -> Order #{order.Id} saved with status {order.Status}.");
    }

    public async Task<List<Order>> GetAllAsync(CancellationToken ct)
    {
        const string source = "OrderRepository";
        await DelayStep.RunAsync(source, "Opening connection...", 200, ct);
        await DelayStep.RunAsync(source, "Executing query for saved orders...", Random.Shared.Next(300, 601), ct);
        return await _store.ReadAsync<Order>(DataFiles.Orders, ct);
    }
}
