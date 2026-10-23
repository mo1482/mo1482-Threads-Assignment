using OrderProcessingSystem.Models;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Repositories;

public sealed class PaymentRepository
{
    private readonly JsonFileStore _store;
    public PaymentRepository(JsonFileStore store) => _store = store;

    public async Task SaveAsync(PaymentTransaction transaction, CancellationToken ct)
    {
        const string source = "PaymentRepository";
        await DelayStep.RunAsync(source, $"Opening connection for Order #{transaction.OrderId}...", 200, ct);
        await DelayStep.RunAsync(source, "Beginning transaction...", 100, ct);
        await DelayStep.RunAsync(source, $"Inserting payment for Order #{transaction.OrderId}...", 400, ct);
        await _store.UpdateAsync<PaymentTransaction>(DataFiles.Payments, rows =>
        {
            int index = rows.FindIndex(p => p.TransactionId == transaction.TransactionId && p.TransactionId != null);
            if (index >= 0) rows[index] = transaction;
            else rows.Add(transaction);
            return rows;
        }, ct);
        await DelayStep.RunAsync(source, "Committing payment transaction...", 200, ct);
        Log.Success($"{source} -> Payment for Order #{transaction.OrderId} saved ({transaction.Status}).");
    }

    public async Task RefundAsync(int orderId, decimal amount, string transactionId, CancellationToken ct)
    {
        const string source = "PaymentRepository";
        await DelayStep.RunAsync(source, $"Opening connection to refund Order #{orderId}...", 200, ct);
        await DelayStep.RunAsync(source, "Beginning refund transaction...", 100, ct);
        await DelayStep.RunAsync(source, $"Updating payment for Order #{orderId} to Refunded...", 400, ct);
        await _store.UpdateAsync<PaymentTransaction>(DataFiles.Payments, rows =>
        {
            int index = rows.FindIndex(p => p.TransactionId == transactionId);
            if (index >= 0)
            {
                rows[index].Status = PaymentStatus.Refunded;
                rows[index].Amount = amount;
            }
            else
            {
                rows.Add(new PaymentTransaction
                {
                    OrderId = orderId, Amount = amount, Status = PaymentStatus.Refunded,
                    TransactionId = transactionId, CreatedAt = DateTime.Now
                });
            }
            return rows;
        }, ct);
        await DelayStep.RunAsync(source, $"Committing refund for Order #{orderId}...", 200, ct);
        Log.Success($"{source} -> Refund recorded for Order #{orderId}.");
    }
}
