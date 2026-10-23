using System.Collections.Concurrent;

namespace ViewsAndEmails;

public sealed class AnalyticsAndEmailJobs
{
    // Protect the compound dictionary read/modify/write operation.
    public readonly Dictionary<string, int> Views = new();
    readonly object _viewsSync = new();

    // Multiple producers and consumers may access this queue concurrently.
    public readonly ConcurrentQueue<string> EmailJobs = new();
    public readonly List<string> SentEmails = new();

    readonly object _sentSync = new();
    public volatile bool ProducersDone;

    public void RecordView(string product)
    {
        lock (_viewsSync)
        {
            if (!Views.TryGetValue(product, out int count))
                count = 0;

            Views[product] = count + 1;
        }
    }

    public void PlaceOrder(int orderId)
    {
        EmailJobs.Enqueue($"Email: order {orderId}");
    }

    public void EmailWorker()
    {
        while (true)
        {
            if (EmailJobs.TryDequeue(out string? job))
            {
                lock (_sentSync)
                    SentEmails.Add(job);
                continue;
            }

            // ProducersDone is set only after all producer threads have joined,
            // so once it is true no more jobs can be added.
            if (ProducersDone && EmailJobs.IsEmpty)
                return;

            Thread.Sleep(1);
        }
    }
}
