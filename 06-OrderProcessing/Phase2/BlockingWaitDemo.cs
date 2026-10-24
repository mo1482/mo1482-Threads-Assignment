namespace OrderProcessingSystem.Phase2;

/// <summary>Intentionally isolated example of blocking an async operation. Do not use this pattern in normal services.</summary>
public static class BlockingWaitDemo
{
    public static void Run(CancellationToken ct)
    {
        Console.WriteLine("===== Blocking .Result/.Wait demo (isolated console-only example) =====");
        Task<int> resultTask = Task.Run(async () => { await Task.Delay(50, ct); return 42; }, ct);
        try { Console.WriteLine($"Demonstration only: .Result = {resultTask.Result}"); }
        catch (Exception) when (ct.IsCancellationRequested && resultTask.IsCanceled) { throw new OperationCanceledException(ct); }
        Task waitTask = Task.Run(async () => { await Task.Delay(50, ct); Console.WriteLine("Wait task finished."); }, ct);
        try { waitTask.Wait(); }
        catch (Exception) when (ct.IsCancellationRequested && waitTask.IsCanceled) { throw new OperationCanceledException(ct); }
        Console.WriteLine("These calls block this thread; production code should await instead.");
    }
}
