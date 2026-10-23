using OrderProcessingSystem.Models;
using OrderProcessingSystem.Repositories;
using OrderProcessingSystem.Services;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Processing;

public sealed class OrderProcessor
{
    private readonly int _phase;
    private readonly CustomerRepository _customers;
    private readonly ProductRepository _products;
    private readonly CouponRepository _coupons;
    private readonly OrderRepository _orders;
    private readonly PaymentRepository _payments;
    private readonly PricingService _pricing;
    private readonly ShippingService _shipping;
    private readonly PaymentService _paymentService;
    private readonly EmailService _emailService;
    private readonly ArtifactService _artifacts;

    public OrderProcessor(int phase, CustomerRepository customers, ProductRepository products,
        CouponRepository coupons, OrderRepository orders, PaymentRepository payments,
        PricingService pricing, ShippingService shipping, PaymentService paymentService,
        EmailService emailService, ArtifactService artifacts)
    {
        _phase = phase; _customers = customers; _products = products; _coupons = coupons;
        _orders = orders; _payments = payments; _pricing = pricing; _shipping = shipping;
        _paymentService = paymentService; _emailService = emailService; _artifacts = artifacts;
    }

    public async Task<ProcessResult> ProcessAsync(Order order, CancellationToken ct)
    {
        bool stockReserved = false;
        bool paymentCaptured = false;
        Customer? customer = null;
        string? txId = null;

        try
        {
            Log.Info($"OrderProcessor -> Starting Order #{order.Id} for Customer #{order.CustomerId}.");
            customer = await _customers.GetByIdAsync(order.CustomerId, ct);
            if (customer is null)
                return await RejectAsync(order, $"Customer #{order.CustomerId} does not exist.", ct);
            if (!customer.IsActive)
                return await RejectAsync(order, $"Customer #{customer.Id} is inactive.", ct);

            var productMap = new Dictionary<int, Product>();
            foreach (var item in order.Items)
            {
                if (item.Quantity <= 0)
                    return await RejectAsync(order, $"Invalid quantity for Product #{item.ProductId}.", ct);
                if (!productMap.ContainsKey(item.ProductId))
                {
                    var product = await _products.GetByIdAsync(item.ProductId, ct);
                    if (product is null)
                        return await RejectAsync(order, $"Product #{item.ProductId} does not exist.", ct);
                    productMap.Add(item.ProductId, product);
                }
            }

            Coupon? coupon = null;
            if (!string.IsNullOrWhiteSpace(order.CouponCode))
            {
                coupon = await _coupons.GetByCodeAsync(order.CouponCode, ct);
                if (coupon is null)
                    return await RejectAsync(order, $"Coupon '{order.CouponCode}' does not exist.", ct);
                if (coupon.ExpiryDate.Date < DateTime.Today)
                    return await RejectAsync(order, $"Coupon '{coupon.Code}' expired on {coupon.ExpiryDate:yyyy-MM-dd}.", ct);
            }

            stockReserved = await _products.ReserveAsync(order.Id, order.Items, ct);
            if (!stockReserved)
                return await RejectAsync(order, "Insufficient stock for one or more products.", ct);

            order.Status = OrderStatus.Validated;
            order.FailureReason = null;
            await _orders.SaveAsync(order, ct);
            await _pricing.CalculateAsync(order, productMap, coupon, ct);
            order.ShippingCost = await _shipping.CalculateAsync(order, productMap, ct);
            order.TotalAmount = Math.Max(0m, order.SubTotal - order.Discount + order.ShippingCost);

            PaymentResult payment;
            try
            {
                payment = await _paymentService.PayAsync(order, ct);
            }
            catch (PaymentGatewayTimeoutException ex)
            {
                txId = ex.TransactionId;
                order.TransactionId = txId;
                await _payments.SaveAsync(new PaymentTransaction
                {
                    OrderId = order.Id, Amount = order.TotalAmount, Status = PaymentStatus.Declined,
                    TransactionId = txId, CreatedAt = DateTime.Now
                }, ct);
                order.Status = OrderStatus.PaymentFailed;
                order.FailureReason = ex.Message;
                await _products.ReleaseAsync(order.Id, order.Items, ct);
                stockReserved = false;
                await _orders.SaveAsync(order, ct);
                Log.Error($"OrderProcessor -> Order #{order.Id} payment timed out.");
                return new ProcessResult { OrderId = order.Id, Cancelled = false, Status = order.Status.ToString() };
            }
            catch (TimeoutException ex)
            {
                order.Status = OrderStatus.PaymentFailed;
                order.FailureReason = ex.Message;
                await _products.ReleaseAsync(order.Id, order.Items, ct);
                stockReserved = false;
                await _orders.SaveAsync(order, ct);
                Log.Error($"OrderProcessor -> Order #{order.Id} payment timed out.");
                return new ProcessResult { OrderId = order.Id, Cancelled = false, Status = order.Status.ToString() };
            }

            if (payment.TransactionId is not null) txId = payment.TransactionId;
            if (!payment.Success)
            {
                if (payment.Status == PaymentStatus.Declined)
                {
                    await _payments.SaveAsync(new PaymentTransaction
                    {
                        OrderId = order.Id, Amount = order.TotalAmount, Status = PaymentStatus.Declined,
                        TransactionId = payment.TransactionId, CreatedAt = DateTime.Now
                    }, ct);
                }
                order.Status = OrderStatus.PaymentFailed;
                order.FailureReason = payment.Message;
                order.TransactionId = payment.TransactionId;
                await _products.ReleaseAsync(order.Id, order.Items, ct);
                stockReserved = false;
                await _orders.SaveAsync(order, ct);
                Log.Error($"OrderProcessor -> Order #{order.Id} failed payment: {payment.Message}");
                return new ProcessResult { OrderId = order.Id, Cancelled = false, Status = order.Status.ToString() };
            }

            paymentCaptured = true;
            order.TransactionId = txId;
            await _payments.SaveAsync(new PaymentTransaction
            {
                OrderId = order.Id, Amount = order.TotalAmount, Status = PaymentStatus.Captured,
                TransactionId = txId, CreatedAt = DateTime.Now
            }, ct);

            order.Status = OrderStatus.Completed;
            order.FailureReason = null;
            await _orders.SaveAsync(order, ct);

            // Stock remains reserved until all cancellable order work is complete. A cancellation
            // after capture triggers both refund and stock release.
            await _emailService.SendConfirmationAsync(order, customer, ct);
            if (_phase >= 2) await _artifacts.WriteInvoiceAsync(order, ct);

            stockReserved = false; // completed sale: stock is consumed, not a reservation anymore
            Log.Success($"OrderProcessor -> Order #{order.Id} completed successfully.");
            return new ProcessResult { OrderId = order.Id, Cancelled = false, Status = order.Status.ToString() };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CompensateCancellationAsync(order, stockReserved, paymentCaptured, txId, ct);
            return new ProcessResult { OrderId = order.Id, Cancelled = true, Status = OrderStatus.Pending.ToString() };
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                await CompensateCancellationAsync(order, stockReserved, paymentCaptured, txId, ct);
                return new ProcessResult { OrderId = order.Id, Cancelled = true, Status = OrderStatus.Pending.ToString() };
            }

            Log.Error($"OrderProcessor -> Order #{order.Id} failed: {ex.Message}");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (paymentCaptured && !string.IsNullOrWhiteSpace(txId))
                await BestEffortAsync(() => _payments.RefundAsync(order.Id, order.TotalAmount, txId!, cleanup.Token), "refund", order.Id, cleanup.Token);
            if (stockReserved)
                await BestEffortAsync(() => _products.ReleaseAsync(order.Id, order.Items, cleanup.Token), "stock release", order.Id, cleanup.Token);
            order.Status = OrderStatus.Failed;
            order.FailureReason = ex.Message;
            try { await _orders.SaveCompensationAsync(order, cleanup.Token); }
            catch (Exception saveEx) { Log.Error($"OrderProcessor -> Could not persist failed Order #{order.Id}: {saveEx.Message}"); }
            return new ProcessResult { OrderId = order.Id, Cancelled = false, Status = order.Status.ToString() };
        }
    }

    private async Task<ProcessResult> RejectAsync(Order order, string reason, CancellationToken ct)
    {
        order.Status = OrderStatus.Rejected;
        order.FailureReason = reason;
        await _orders.SaveAsync(order, ct);
        Log.Error($"OrderProcessor -> Order #{order.Id} rejected: {reason}");
        return new ProcessResult { OrderId = order.Id, Cancelled = false, Status = order.Status.ToString() };
    }

    private async Task CompensateCancellationAsync(Order order, bool stockReserved, bool paymentCaptured, string? txId, CancellationToken ct)
    {
        Log.Error($"OrderProcessor -> Cancelling Order #{order.Id}; performing compensation (run cancellation requested: {ct.IsCancellationRequested}).");
        // The run token is already cancelled, so compensation uses a separate bounded token; otherwise
        // cancellation itself would prevent the required release/refund/persistence operations.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        if (paymentCaptured && !string.IsNullOrWhiteSpace(txId))
            await BestEffortAsync(() => _payments.RefundAsync(order.Id, order.TotalAmount, txId!, cleanup.Token), "refund", order.Id, cleanup.Token);
        if (stockReserved)
            await BestEffortAsync(() => _products.ReleaseAsync(order.Id, order.Items, cleanup.Token), "stock release", order.Id, cleanup.Token);
        order.Status = OrderStatus.Pending;
        order.FailureReason = "Cancelled";
        try { await _orders.SaveCompensationAsync(order, cleanup.Token); }
        catch (Exception ex) { Log.Error($"OrderProcessor -> Could not persist cancelled Order #{order.Id}: {ex.Message}"); }
    }

    private static async Task BestEffortAsync(Func<Task> action, string operation, int orderId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await action();
        }
        catch (Exception ex) { Log.Error($"OrderProcessor -> Compensation {operation} failed for Order #{orderId}: {ex.Message}"); }
    }
}
