using ViewsAndEmails;

var hub = new AnalyticsAndEmailJobs();

Thread[] viewers = new Thread[10];
for (int t = 0; t < viewers.Length; t++)
{
    viewers[t] = new Thread(() =>
    {
        for (int i = 0; i < 5_000; i++)
            hub.RecordView("koshary-bowl");
    });
    viewers[t].Start();
}

Thread[] producers = new Thread[20];
for (int i = 0; i < producers.Length; i++)
{
    int id = i + 1;
    producers[i] = new Thread(() => hub.PlaceOrder(id));
    producers[i].Start();
}

Thread w1 = new(hub.EmailWorker);
Thread w2 = new(hub.EmailWorker);
w1.Start();
w2.Start();

foreach (Thread t in viewers)
    t.Join();
foreach (Thread t in producers)
    t.Join();

hub.ProducersDone = true;
w1.Join();
w2.Join();

int viewExpected = 10 * 5_000;
int viewActual = hub.Views.TryGetValue("koshary-bowl", out int v) ? v : 0;
Console.WriteLine($"Views expected={viewExpected}, actual={viewActual}");
Console.WriteLine($"Email jobs placed=20, unique sent={hub.SentEmails.Distinct().Count()}, total sends={hub.SentEmails.Count}");
