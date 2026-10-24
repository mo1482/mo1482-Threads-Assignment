namespace InvoiceExport;

public sealed class CustomerFileStore
{
    readonly string _folder;

    public CustomerFileStore(string folder) => _folder = folder;

    public async Task<CustomerProfile> ReadProfileAsync(int customerId)
    {
        string path = Path.Combine(_folder, $"customer-{customerId}.txt");
        string text = await File.ReadAllTextAsync(path);
        string[] parts = text.Split('|');
        return new CustomerProfile(int.Parse(parts[0]), parts[1], parts[2]);
    }
}
