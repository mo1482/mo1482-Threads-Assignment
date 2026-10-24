namespace InvoiceExport;

public sealed record Order(int Id, int CustomerId, decimal Total, string Restaurant);

public sealed record CustomerProfile(int Id, string Name, string Email);
