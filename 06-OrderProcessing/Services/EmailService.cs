using OrderProcessingSystem.Models;
using OrderProcessingSystem.Repositories;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Services;

public sealed class EmailService
{
    private readonly int _phase;
    private readonly EmailRepository _emails;
    public EmailService(int phase, EmailRepository emails) { _phase = phase; _emails = emails; }

    public async Task<bool> SendConfirmationAsync(Order order, Customer customer, CancellationToken ct)
    {
        const string source = "EmailService";
        const int maxAttempts = 3;
        string? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await DelayStep.RunAsync(source, $"Rendering confirmation template for Order #{order.Id} (attempt {attempt})...", 200, ct);
            await DelayStep.RunAsync(source, $"Connecting to SMTP for Order #{order.Id}...", 300, ct);
            await DelayStep.RunAsync(source, $"Sending email for Order #{order.Id}...", 500, ct);

            bool shouldFail = _phase == 3 &&
                ((order.Id == 3 && attempt <= 2) || order.Id == 17);
            if (!shouldFail)
            {
                var sent = new EmailMessage
                {
                    Id = Guid.NewGuid(), OrderId = order.Id, To = customer.Email,
                    Subject = $"Order #{order.Id} confirmation",
                    Body = $"Hello {customer.Name}, your order #{order.Id} is confirmed. Total: {order.TotalAmount:C}.",
                    SentAt = DateTime.Now, Attempts = attempt, LastError = null
                };
                await _emails.SaveSentAsync(sent, ct);
                Log.Success($"{source} -> Confirmation email sent for Order #{order.Id} on attempt {attempt}.");
                return true;
            }

            lastError = order.Id == 17
                ? "Simulated permanent SMTP failure for Order #17."
                : $"Simulated SMTP failure on attempt {attempt} for Order #3.";
            Log.Error($"{source} -> {lastError}");
        }

        var failed = new EmailMessage
        {
            Id = Guid.NewGuid(), OrderId = order.Id, To = customer.Email,
            Subject = $"Order #{order.Id} confirmation",
            Body = $"Confirmation for order #{order.Id}; order total {order.TotalAmount:C}.",
            SentAt = DateTime.Now, Attempts = maxAttempts, LastError = lastError
        };
        await _emails.SaveRetryAsync(failed, ct);
        Log.Error($"{source} -> Email for Order #{order.Id} moved to email_retry.json after {maxAttempts} attempts.");
        return false;
    }
}
