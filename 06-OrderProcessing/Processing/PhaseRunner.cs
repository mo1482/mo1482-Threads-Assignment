using System.Diagnostics;
using OrderProcessingSystem.Models;
using OrderProcessingSystem.Repositories;
using OrderProcessingSystem.Services;
using OrderProcessingSystem.Storage;
using OrderProcessingSystem.Utilities;

namespace OrderProcessingSystem.Processing;

public sealed class PhaseRunner
{
    private readonly JsonFileStore _store;
    public PhaseRunner(JsonFileStore store) => _store = store;

    public async Task RunAsync(int phase, bool compareSync, CancellationToken ct)
    {
        int finished = 0;
        int cancelled = 0;
        try
        {
            var customers = new CustomerRepository(_store);
            if (compareSync)
            {
                if (phase != 1) throw new ArgumentException("--sync-comparison is available only in phase1.");
                await RunSyncComparisonAsync(customers, ct);
                Console.WriteLine("Synchronous comparison complete. No orders were processed in comparison-only mode.");
                return;
            }

            var incoming = await _store.ReadAsync<Order>(DataFiles.IncomingOrders, ct);
            var productRepo = new ProductRepository(_store);
            var couponRepo = new CouponRepository(_store);
            var orderRepo = new OrderRepository(_store, phase);
            var paymentRepo = new PaymentRepository(_store);
            var emailRepo = new EmailRepository(_store);
            var pricing = new PricingService(phase);
            var shipping = new ShippingService();
            var paymentService = new PaymentService(phase);
            var emailService = new EmailService(phase, emailRepo);
            var artifacts = new ArtifactService(_store, orderRepo);
            var processor = new OrderProcessor(phase, customers, productRepo, couponRepo, orderRepo,
                paymentRepo, pricing, shipping, paymentService, emailService, artifacts);

            int degree = phase == 1 ? 1 : 4;
            Log.Info($"PhaseRunner -> Phase {phase} starting {incoming.Count} orders with max concurrency {degree}.");

            int nextIndex = 0;
            object takeGate = new();
            async Task WorkerAsync(CancellationToken workerCt)
            {
                while (true)
                {
                    Order order;
                    lock (takeGate)
                    {
                        // Re-check while holding the dispatch lock, so cancelled runs don't intentionally dequeue new work.
                        if (workerCt.IsCancellationRequested || nextIndex >= incoming.Count) return;
                        order = incoming[nextIndex++];
                    }

                    var result = await processor.ProcessAsync(order, workerCt);
                    if (result.Cancelled) Interlocked.Increment(ref cancelled);
                    else Interlocked.Increment(ref finished);
                }
            }

            var workers = Enumerable.Range(0, degree).Select(_ => WorkerAsync(ct)).ToArray();
            await Task.WhenAll(workers);

            if (!ct.IsCancellationRequested && phase >= 2)
            {
                // Invoices are emitted by the processor for completed orders. The report is a phase-level artifact.
                var report = await artifacts.GenerateReportAsync(ct);
                Log.Success($"PhaseRunner -> Report: {report.TotalOrders} saved orders, {report.CompletedCount} completed, revenue {report.TotalRevenue:C}.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The final summary is emitted below; worker processing performs per-order compensation.
        }
        finally
        {
            if (ct.IsCancellationRequested)
                Console.WriteLine($"Run cancelled — {finished} orders finished, {cancelled} cancelled");
            else
                Console.WriteLine($"Run complete — {finished} orders finished, {cancelled} cancelled");
        }
    }

    private static async Task RunSyncComparisonAsync(CustomerRepository repository, CancellationToken ct)
    {
        Log.Info("PhaseRunner -> Comparing synchronous and asynchronous customer lookup latency.");
        // Query a small deterministic set so the output stays useful without mutating seeded data.
        int[] ids = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 99 };
        var syncWatch = Stopwatch.StartNew();
        foreach (int id in ids) _ = repository.GetById(id, ct);
        syncWatch.Stop();

        var asyncWatch = Stopwatch.StartNew();
        _ = await Task.WhenAll(ids.Select(id => repository.GetByIdAsync(id, ct)));
        asyncWatch.Stop();

        Console.WriteLine($"Sync lookup total:  {syncWatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Async lookup total: {asyncWatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine("This mode is a sync/async lookup comparison only; use 'phase1' for order processing.");
    }
}
