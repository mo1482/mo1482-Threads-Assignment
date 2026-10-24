using OrderProcessingSystem.Models;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Services;

public sealed class PaymentGatewayTimeoutException : TimeoutException
{
    public string TransactionId { get; }
    public PaymentGatewayTimeoutException(string message, string transactionId) : base(message)
        => TransactionId = transactionId;
}

public sealed class PaymentService
{
    private readonly int _phase;
    public PaymentService(int phase) => _phase = phase;

    public async Task<PaymentResult> PayAsync(Order order, CancellationToken ct)
    {
        const string source = "PaymentService";
        string tx = $"TX-{order.Id:D4}-{Guid.NewGuid():N}";
        await DelayStep.RunAsync(source, $"Authorizing payment for Order #{order.Id}...", 800, ct);
        await DelayStep.RunAsync(source, $"Running fraud check for Order #{order.Id}...", 500, ct);

        if (_phase == 3 && order.Id == 5)
        {
            Log.Error($"{source} -> Payment gateway timed out for Order #{order.Id}.");
            throw new PaymentGatewayTimeoutException("Simulated payment gateway timeout for Order #5.", tx);
        }

        if (_phase == 3 && order.Id == 8)
        {
            Log.Error($"{source} -> Payment declined for Order #{order.Id}.");
            return new PaymentResult { Success = false, TransactionId = tx, Status = PaymentStatus.Declined, Message = "Payment was declined by the gateway." };
        }

        await DelayStep.RunAsync(source, $"Capturing payment for Order #{order.Id}...", 700, ct);
        Log.Success($"{source} -> Payment captured for Order #{order.Id} ({order.TotalAmount:C}).");
        return new PaymentResult { Success = true, TransactionId = tx, Status = PaymentStatus.Captured, Message = "Payment captured successfully." };
    }
}
