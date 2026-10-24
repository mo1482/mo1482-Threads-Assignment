using OrderProcessingSystem.Models;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Repositories;

public sealed class EmailRepository
{
    private readonly JsonFileStore _store;
    public EmailRepository(JsonFileStore store) => _store = store;

    public Task SaveSentAsync(EmailMessage message, CancellationToken ct) => SaveAsync(DataFiles.Emails, message, ct);
    public Task SaveRetryAsync(EmailMessage message, CancellationToken ct) => SaveAsync(DataFiles.EmailRetry, message, ct);

    private async Task SaveAsync(string file, EmailMessage message, CancellationToken ct)
    {
        const string source = "EmailRepository";
        await DelayStep.RunAsync(source, $"Opening connection to save email for Order #{message.OrderId}...", 200, ct);
        await DelayStep.RunAsync(source, "Beginning outbox transaction...", 100, ct);
        await DelayStep.RunAsync(source, $"Inserting email into {file}...", 400, ct);
        await _store.UpdateAsync<EmailMessage>(file, rows =>
        {
            rows.Add(message);
            return rows;
        }, ct);
        await DelayStep.RunAsync(source, "Committing email record...", 200, ct);
        Log.Success($"{source} -> Email record for Order #{message.OrderId} saved in {file}.");
    }
}
