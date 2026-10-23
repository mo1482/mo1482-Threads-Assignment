using MenuViews;

var counter = new MenuViewCounter();
const int threads = 8;
const int viewsPerThread = 50_000;
int expected = threads * viewsPerThread;

Thread[] workers = new Thread[threads];
for (int t = 0; t < threads; t++)
{
    workers[t] = new Thread(() =>
    {
        for (int i = 0; i < viewsPerThread; i++)
            counter.RegisterView();
    });
    workers[t].Start();
}

foreach (Thread w in workers)
    w.Join();

Console.WriteLine($"Expected: {expected}");
Console.WriteLine($"Actual:   {counter.Views}");
