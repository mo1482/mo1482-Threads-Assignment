using OrderProcessingSystem.Processing;
using OrderProcessingSystem.Storage;

static (int Phase, int? CancelAfterMs, bool CompareSync) ParseArgs(string[] args)
{
    int phase = 1;
    int? cancelAfter = null;
    bool compareSync = false;
    foreach (var arg in args)
    {
        if (arg.Equals("phase1", StringComparison.OrdinalIgnoreCase)) phase = 1;
        else if (arg.Equals("phase2", StringComparison.OrdinalIgnoreCase)) phase = 2;
        else if (arg.Equals("phase3", StringComparison.OrdinalIgnoreCase)) phase = 3;
        else if (arg.Equals("--sync-comparison", StringComparison.OrdinalIgnoreCase)) compareSync = true;
    }
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--cancel-after", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int milliseconds) || milliseconds < 0)
                throw new ArgumentException("--cancel-after requires a non-negative integer number of milliseconds.");
            cancelAfter = milliseconds;
        }
    }
    return (phase, cancelAfter, compareSync);
}

try
{
    var (phase, cancelAfterMs, compareSync) = ParseArgs(args);
    using var runCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        runCts.Cancel();
    };

    var store = new JsonFileStore();
    var seeder = new DataSeeder(store);
    Console.WriteLine($"=== Order Processing System | Phase {phase} ===");
    await seeder.ResetAsync(runCts.Token);

    // The auto-cancel window begins after reset/seeding, so every phase starts with a complete valid data set.
    if (cancelAfterMs.HasValue) runCts.CancelAfter(cancelAfterMs.Value);

    var runner = new PhaseRunner(store);
    await runner.RunAsync(phase, compareSync, runCts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Run cancelled — 0 orders finished, 0 cancelled");
    Console.WriteLine("Cancellation happened during setup; seed files were restored to valid JSON.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    Environment.ExitCode = 1;
}
