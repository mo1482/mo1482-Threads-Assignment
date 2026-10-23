namespace MenuViews;

public sealed class MenuViewCounter
{
    public int Views;

    public void RegisterView()
    {
        Interlocked.Increment(ref Views);
    }
}
