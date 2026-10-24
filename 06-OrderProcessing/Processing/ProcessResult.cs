namespace OrderProcessingSystem.Processing;

public sealed class ProcessResult
{
    public int OrderId { get; init; }
    public bool Cancelled { get; init; }
    public string Status { get; init; } = "";
}
