# Assignment 01 — Answers

Student name: ____________________
Group / ID: ____________________

## Problems with the current design

- **Synchronous waiting:** `Thread.Sleep` simulates slow database/file operations and blocks a thread while waiting. Real synchronous I/O has the same scalability problem.
- **Sequential processing:** Each order is fully loaded, its customer file read, and its invoice written before the next order starts. Independent orders therefore wait unnecessarily.
- **N+1 database access pattern:** The program loads the pending-order list, then makes another database call for every order. With many orders, the repeated calls add latency.
- **Synchronous file I/O:** `File.ReadAllText` and `File.WriteAllText` block threads while files are being read/written.
- **Output order is not guaranteed after the fix:** concurrent orders may finish in a different order; invoice filenames and invoice contents remain associated with the correct order.

## What you changed and why

- Changed database methods to `GetPendingOrdersAsync` and `GetOrderByIdAsync`, and used `await Task.Delay(...)` for the simulated latency instead of blocking with `Thread.Sleep`.
- Changed customer reading and invoice writing to `File.ReadAllTextAsync` and `File.WriteAllTextAsync`.
- Changed the export service to async methods. `ExportOneAsync` retains the required business flow for every order: **load order → read customer → write invoice**.
- Started each order export and awaited them together with `Task.WhenAll`. This allows independent database and file operations to overlap instead of processing all five orders serially.
- Updated `Program.cs` to await `ExportAllPendingAsync`.
- Kept the order data, customer data, invoice text/template, invoice filenames, and five-order set unchanged.

## Console output before and after

### Before (original design)

The original run prints the five `Wrote .../invoice-####.txt` lines one after another, followed by `Export finished in ... s`. The simulated waits alone total about **5.8 seconds**: 0.8 seconds to get pending orders, plus 5 × (0.4 seconds to load an order + 0.3 seconds to read a customer + 0.3 seconds to write an invoice). Actual wall-clock time can vary.

Example shape (paths and exact timing vary):

```text
Starting invoice export...
Customer files: .../Data
Invoice output: .../invoices

Wrote .../invoice-1001.txt
Wrote .../invoice-1002.txt
Wrote .../invoice-1003.txt
Wrote .../invoice-1004.txt
Wrote .../invoice-1005.txt
Export finished in 5.8 s
```

### After (updated design)

The five invoice writes can complete in a different order because exports run concurrently. The output still contains one `Wrote` line for each of the same five invoice files, then the elapsed time. With the artificial delays in this sample, the expected time is roughly **1.8 seconds** (0.8 seconds to load the list, then approximately 1.0 second for the per-order steps to overlap); actual timing depends on the machine and file system.

Example shape (completion order and exact timing vary):

```text
Starting invoice export...
Customer files: .../Data
Invoice output: .../invoices

Wrote .../invoice-1002.txt
Wrote .../invoice-1001.txt
Wrote .../invoice-1003.txt
Wrote .../invoice-1004.txt
Wrote .../invoice-1005.txt
Export finished in 1.8 s
```

**Note:** The output above is illustrative, not a measured run. The .NET CLI was not available in the environment used to prepare this solution, so run `dotnet run --project InvoiceExport` locally and replace the example timings/order with your actual before-and-after console output if your instructor expects measured output.
