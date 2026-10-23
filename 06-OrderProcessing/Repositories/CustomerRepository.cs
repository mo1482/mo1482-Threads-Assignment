using OrderProcessingSystem.Models;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Repositories;

public sealed class CustomerRepository
{
    private readonly JsonFileStore _store;
    public CustomerRepository(JsonFileStore store) => _store = store;

    public async Task<Customer?> GetByIdAsync(int id, CancellationToken ct)
    {
        const string source = "CustomerRepository";
        await DelayStep.RunAsync(source, "Opening connection...", 200, ct);
        await DelayStep.RunAsync(source, $"Executing query for Customer #{id}...", Random.Shared.Next(300, 601), ct);
        var customers = await _store.ReadAsync<Customer>(DataFiles.Customers, ct);
        var customer = customers.FirstOrDefault(c => c.Id == id);
        Log.Info(customer is null ? $"{source} -> Customer #{id} not found." : $"{source} -> Customer #{id} found ({customer.Name}).");
        return customer;
    }

    // Used only by Phase 1's explicit synchronous comparison mode.
    public Customer? GetById(int id, CancellationToken ct)
    {
        const string source = "CustomerRepository [sync comparison]";
        DelayStep.RunSync(source, "Opening connection...", 200, ct);
        DelayStep.RunSync(source, $"Executing query for Customer #{id}...", Random.Shared.Next(300, 601), ct);
        var customer = _store.Read<Customer>(DataFiles.Customers).FirstOrDefault(c => c.Id == id);
        Log.Info(customer is null ? $"{source} -> Customer #{id} not found." : $"{source} -> Customer #{id} found ({customer.Name}).");
        return customer;
    }
}
