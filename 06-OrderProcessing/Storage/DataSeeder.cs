using OrderProcessingSystem.Models;

namespace OrderProcessingSystem.Storage;

public sealed class DataSeeder
{
    private readonly JsonFileStore _store;
    public DataSeeder(JsonFileStore store) => _store = store;

    public async Task ResetAsync(CancellationToken ct)
    {
        try
        {
            await ResetCoreAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // If Ctrl+C arrives during seeding, complete a valid, fully seeded file set before propagating cancellation.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ResetCoreAsync(recovery.Token);
            throw;
        }
    }

    private async Task ResetCoreAsync(CancellationToken ct)
    {
        string root = _store.DataDirectory;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, DataFiles.InvoicesDirectory));
        Directory.CreateDirectory(Path.Combine(root, DataFiles.ReportsDirectory));

        await SeedFileAsync(DataFiles.Customers, CreateCustomers(), ct);
        await SeedFileAsync(DataFiles.Products, CreateProducts(), ct);
        await SeedFileAsync(DataFiles.Coupons, CreateCoupons(), ct);
        await SeedFileAsync(DataFiles.IncomingOrders, CreateIncomingOrders(), ct);
        await SeedFileAsync<Order>(DataFiles.Orders, new(), ct);
        await SeedFileAsync<PaymentTransaction>(DataFiles.Payments, new(), ct);
        await SeedFileAsync<EmailMessage>(DataFiles.Emails, new(), ct);
        await SeedFileAsync<EmailMessage>(DataFiles.EmailRetry, new(), ct);
    }

    private async Task SeedFileAsync<T>(string fileName, List<T> records, CancellationToken ct)
    {
        await _store.WriteAsync(fileName, records, ct);
        Console.WriteLine($"[Seeder] {fileName} -> {records.Count} records");
    }

    private static List<Customer> CreateCustomers() => new()
    {
        new() { Id=1, Name="Ahmed Ali", Email="ahmed.ali@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Gold },
        new() { Id=2, Name="Sara Mohamed", Email="sara.mohamed@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Silver },
        new() { Id=3, Name="Omar Hassan", Email="omar.hassan@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Bronze },
        new() { Id=4, Name="Mona Ibrahim", Email="mona.ibrahim@example.com", IsActive=false, LoyaltyTier=LoyaltyTier.Silver },
        new() { Id=5, Name="Youssef Adel", Email="youssef.adel@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Gold },
        new() { Id=6, Name="Nour Khaled", Email="nour.khaled@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Bronze },
        new() { Id=7, Name="Karim Samir", Email="karim.samir@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Silver },
        new() { Id=8, Name="Laila Mahmoud", Email="laila.mahmoud@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Gold },
        new() { Id=9, Name="Hassan Tarek", Email="hassan.tarek@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Bronze },
        new() { Id=10, Name="Dina Fathy", Email="dina.fathy@example.com", IsActive=true, LoyaltyTier=LoyaltyTier.Silver }
    };

    private static List<Product> CreateProducts() => new()
    {
        new() { Id=1, Name="Laptop", Price=1200.00m, StockQuantity=10, WeightKg=2.2m },
        new() { Id=2, Name="Smartphone", Price=800.00m, StockQuantity=15, WeightKg=0.4m },
        new() { Id=3, Name="Wireless Mouse", Price=25.00m, StockQuantity=2, WeightKg=0.1m },
        new() { Id=4, Name="Mechanical Keyboard", Price=90.00m, StockQuantity=1, WeightKg=0.9m },
        new() { Id=5, Name="USB-C Hub", Price=45.00m, StockQuantity=30, WeightKg=0.2m },
        new() { Id=6, Name="Headphones", Price=150.00m, StockQuantity=12, WeightKg=0.3m },
        new() { Id=7, Name="27\" Monitor", Price=350.00m, StockQuantity=6, WeightKg=5.0m },
        new() { Id=8, Name="Webcam", Price=70.00m, StockQuantity=20, WeightKg=0.2m }
    };

    private static List<Coupon> CreateCoupons()
    {
        DateTime today = DateTime.Today;
        return new()
        {
            new() { Code="SAVE10", DiscountPercent=10, ExpiryDate=today.AddDays(180) },
            new() { Code="WELCOME15", DiscountPercent=15, ExpiryDate=today.AddDays(90) },
            new() { Code="FLASH20", DiscountPercent=20, ExpiryDate=today.AddDays(7) },
            new() { Code="OLD20", DiscountPercent=20, ExpiryDate=today.AddDays(-30) }
        };
    }

    private static List<Order> CreateIncomingOrders()
    {
        DateTime now = DateTime.Now;
        return new()
        {
            O(1, 1, "SAVE10", now, (1,1), (3,1)),
            O(2, 2, null, now, (2,1)),
            O(3, 3, "WELCOME15", now, (5,2), (8,1)),
            O(4, 4, null, now, (6,1)),
            O(5, 5, null, now, (7,1)),
            O(6, 6, "FLASH20", now, (5,1)),
            O(7, 7, null, now, (6,1), (8,1)),
            O(8, 8, "SAVE10", now, (1,1)),
            O(9, 9, null, now, (3,5)),
            O(10, 10, null, now, (4,1)),
            O(11, 1, null, now, (4,1)),
            O(12, 2, "OLD20", now, (2,1)),
            O(13, 3, null, now, (6,2)),
            O(14, 5, "WELCOME15", now, (8,2), (5,1)),
            O(15, 99, null, now, (2,1)),
            O(16, 6, null, now, (7,1), (5,1)),
            O(17, 7, "SAVE10", now, (2,1)),
            O(18, 8, "FAKE50", now, (6,1)),
            O(19, 9, null, now, (8,1)),
            O(20, 10, "FLASH20", now, (1,1), (6,1))
        };
    }

    private static Order O(int id, int customerId, string? coupon, DateTime now, params (int productId, int quantity)[] items)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            Items = items.Select(x => new OrderItem { ProductId = x.productId, Quantity = x.quantity, UnitPrice = 0m }).ToList(),
            CouponCode = coupon,
            SubTotal = 0m,
            Discount = 0m,
            ShippingCost = 0m,
            TotalAmount = 0m,
            Status = OrderStatus.Pending,
            CreatedAt = now,
            FailureReason = null,
            TransactionId = null
        };
}
