namespace OrderProcessingSystem.Utilities;

public static class DelayStep
{
    public static async Task RunAsync(string source, string description, int milliseconds, CancellationToken ct)
    {
        Log.Info($"{source} -> {description}");
        await Task.Delay(milliseconds, ct);
    }

    public static void RunSync(string source, string description, int milliseconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log.Info($"{source} -> {description}");
        Thread.Sleep(milliseconds);
        ct.ThrowIfCancellationRequested();
    }
}
