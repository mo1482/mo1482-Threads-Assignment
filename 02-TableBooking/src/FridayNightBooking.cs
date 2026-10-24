namespace TableBooking;

public sealed class FridayNightBooking
{
    private readonly object _bookingLock = new();

    public int TablesLeft { get; private set; } = 5;
    public int Booked { get; private set; }

    public void Book(string customerName)
    {
        // Simulate work before trying to reserve a table.
        Thread.Sleep(20);

        // The check and both updates must be one atomic operation.
        lock (_bookingLock)
        {
            if (TablesLeft <= 0)
                return;

            TablesLeft--;
            Booked++;

            Console.WriteLine($"{customerName} booked a table. Left={TablesLeft}");
        }
    }
}
