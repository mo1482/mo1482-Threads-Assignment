# Assignment 02 — Answers
## What is wrong?

The original `Book` method is not thread-safe. The program starts 40 customer threads, so several threads can execute `Book` at the same time.

The check `if (TablesLeft <= 0)` and the updates `TablesLeft--` and `Booked++` are separate operations. A race condition can happen when multiple threads read the same positive value of `TablesLeft` before any of them decrements it. More customers can then be told they booked a table than the five tables available. The two counters can also become inconsistent because increment/decrement operations are read-modify-write sequences.

`Thread.Sleep(20)` makes it more likely that several threads pass the availability check before updating the counters, but removing the sleep alone would not make the code thread-safe. The real issue is the lack of synchronization around the check-and-update operation.

## Original run results (before the fix)

The original results are **nondeterministic**: they depend on thread scheduling, so there is no single correct set of numbers to report for every run. Run the original version several times and copy the exact final line from each run into this table before submitting.

| Run | Final output (`Booked`, `TablesLeft`) |
|---|---|
| 1 | Record the observed values from the original version |
| 2 | Record the observed values from the original version |
| 3 | Record the observed values from the original version |

Note: I could not execute `dotnet run --project TableBooking` in the preparation environment because the .NET CLI is not installed, so I have not invented before-fix measurements.

## How you fixed it

- Added a private lock object, `_bookingLock`.
- Moved the availability check, decrement of `TablesLeft`, increment of `Booked`, and success message into the same `lock` block.
- Kept the simulated delay outside the lock. Customers can wait concurrently, but only one thread at a time can check availability and reserve a table.

The lock makes the check-and-update sequence mutually exclusive. Once a thread enters the critical section, no other thread can change the counters until it leaves. After the fifth successful booking, all remaining threads see `TablesLeft == 0` and return without booking.

## Expected results after the fix

Run `dotnet run --project TableBooking` at least three times. Each run should end with:

```text
Booked=5, TablesLeft=0
```

The order of customer names in the success messages can vary between runs, but exactly five customers should book a table every time. The final totals should never change.
