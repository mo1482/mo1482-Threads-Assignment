# E-Commerce Order Processing System — JSON File Storage Edition

Console application targeting **.NET 10 (`net10.0`)**, written in C# with `async`/`await`, cancellation tokens, file-backed JSON collections, simulated latency, constructor injection, and four runnable phases. There is no real database or external HTTP/SMTP/payment system.

> **Build verification note:** this project was assembled in an environment without the .NET SDK, and outbound package/network access was unavailable. It has therefore not been compiled or executed in this environment. Run `dotnet build` with the .NET 10 SDK before submitting, then capture genuine console screenshots from your run. `sample-output/` contains labelled reference JSON fixtures and is overwritten with runtime snapshots when a phase runs; no fake console screenshots are included.

## 1. Run the application

From the project root:

```bash
dotnet --version                 # must report 10.x
dotnet build
dotnet run                      # numbered menu
dotnet run -- phase1
dotnet run -- phase2
dotnet run -- phase3
dotnet run -- phase4
dotnet run -- all
dotnet run -- phase3 --cancel-after 4000
```

Press **Ctrl+C** to cancel the active run. Each phase owns a `CancellationTokenSource` supplied by `Program`; `--cancel-after <ms>` cancels automatically. Seeding happens at the start of each phase. The data directory is `Path.Combine(AppContext.BaseDirectory, "Data")`, normally `bin/Debug/net10.0/Data/` for a Debug run (or `bin/Release/net10.0/Data/` for Release).

The application snapshots phase output under `sample-output/`. The checked-in folders are clearly labelled reference fixtures (stable illustrative timestamps and transaction IDs), not artifacts captured from an actual run in this SDK-less environment. Running `phase1` overwrites its 20-row validation snapshot; `phase2` overwrites its concurrent result, invoices and report; `phase3-checkout` stores the full 20-order checkout before the separate 10-order sync/async comparison resets data; `phase3-comparison` stores that comparison's final data; `phase4-checkout` captures full checkout; and `phase4` captures the post-dashboard, carrier, fraud, re-send and fire-and-forget data. A snapshot copies files; JSON collection reads and writes are otherwise routed through `JsonFileStore`.

## 2. Project structure and responsibilities

```text
Shared/
  Models/Models.cs                 Domain models, enums, dashboard and quote DTOs
  Storage/DataFiles.cs             Fixed file names
  Storage/JsonFileStore.cs         Sole JSON collection read/write API
  Storage/DataSeeder.cs            Resets and seeds every collection
  Logging/Log.cs                   Thread-safe coloured log and latency helpers
  Exceptions/Exceptions.cs         Required custom exceptions
  Repositories/Repositories.cs     File-backed repositories and sync comparison methods
Phase1/Phase1Runner.cs             Async/sync validation and summary
Phase1/SyncRepositories.cs         Dedicated synchronous customer/product/coupon repositories
Phase2/Phase2Runner.cs             Pricing, invoice/report generation, race demo, V2 processor
Phase3/Phase3Runner.cs             Payment/email interfaces and services, full checkout, 9.6 comparison
Phase4/Phase4Runner.cs             Completion-order channel, dashboard, carriers, fraud, resend, fire-and-forget
Program.cs                        Menu, CLI parsing, cancellation source and output snapshots
sample-output/                    Reference fixtures; runtime runs overwrite phase snapshots
```

No DI container is used: dependencies are passed through constructors. External payment and email boundaries use `IPaymentGateway` and `IEmailService`. Required exceptions are `CustomerNotFoundException`, `InvalidCouponException`, `ShippingProviderUnavailableException`, `DatabaseWriteException`, `SmtpUnavailableException`, and `DataFileCorruptedException`.

## 3. Storage design

Each collection is a JSON array encoded as UTF-8 (without a BOM) and serialized through one shared `JsonSerializerOptions`: `WriteIndented = true`, `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, and `JsonStringEnumConverter`.

| File | Purpose |
|---|---|
| `customers.json` | 10 seeded customers; read-only in phase runs |
| `products.json` | 8 products; stock changes during safe reserve/release |
| `coupons.json` | 4 seeded coupons, expiry dates relative to `DateTime.Today` |
| `incoming_orders.json` | 20 immutable sample orders |
| `orders.json` | Processed orders; upserted by `Id` |
| `payments.json` | Captures, declines and refunds |
| `emails.json` | Email outbox; one row per successfully sent order/test email |
| `email_retry.json` | Emails still failing after attempt four |
| `invoices/` | `invoice_{orderId}.txt`; Phase 2 exception to collection JSON storage |
| `reports/` | `report_{yyyyMMdd_HHmmss}.json`; Phase 2 report output exception |

`JsonFileStore` offers cancellable async `ReadAsync`, `WriteAsync`, and atomic read-modify-write `UpdateAsync` operations, plus sync methods used by the sync comparisons. A static per-full-path `SemaphoreSlim` protects collection operations in this process. `UpdateAsync` holds the lock across read, modification and replacement, so concurrent upserts and stock changes don't overwrite each other. Writes serialize first to a unique temporary file in the same directory, then replace the final file with `File.Move(..., overwrite: true)`. Readers see the previous or new complete JSON rather than a partially-written destination. A missing file reads as an empty list; invalid JSON throws `DataFileCorruptedException` naming the file. This locking guarantees in-process concurrency, not a distributed lock between multiple OS processes.

`DataSeeder.ResetAsync()` deletes and recreates the Data directory, makes `invoices/` and `reports/`, writes all eight collection files, ensures empty files are `[]`, and prints one `[Seeder] ... -> N records` line per file. If cancellation interrupts seeding, a short independent recovery token completes a valid seed set before cancellation is rethrown.

All simulated DB/API/SMTP delays belong in repositories/services, not in `JsonFileStore`. Repository query timings are 200 ms + 300–600 ms; DB writes are 200 + 100 + 400 ms, perform the file mutation, then 200 ms commit. API calls use 150 + 300 + 500–1500 ms; payment steps use 800 + 500 + 700 ms; SMTP uses 200 + 300 + 500 ms. The logs use `[HH:mm:ss.fff] [Thread id] ...`, and `Log` serializes console colour changes so concurrent lines do not mix colours.

## 4. Phase implementation notes

### Phase 1 — Async data access

`OrderValidator.ValidateOrderAsync` checks, in order: customer exists; customer active; all products exist; stock is sufficient without reserving; optional coupon exists; coupon expiry is today or later. It stops at the first failure and uses the exact reason strings from the assignment. Validated and rejected orders are both saved with order-ID upsert. The synchronous comparison uses `Thread.Sleep` and the store's sync methods on object copies, without saving the sync result; the phase then resets and executes the true async validation/save pass. The displayed status totals are read back from `orders.json`, not derived from in-memory task results.

### Phase 2 — Concurrent processing

Pricing follows the exact formula:

- `SubTotal = Σ(UnitPrice × Quantity)` (unit prices are read from products)
- discount percent = loyalty tier (Bronze 0, Silver 5, Gold 10) + coupon percent, capped at 30
- `Discount = SubTotal × percent / 100`
- `ShippingCost = 0` when subtotal ≥ 1000; otherwise `5.00 + 1.50 × item quantity + 2.00 × total item weight (kg)`
- `TotalAmount = SubTotal − Discount + ShippingCost`

All money calculations use `Math.Round(..., 2, MidpointRounding.AwayFromZero)`. Order #1 evaluates to `1225.00 / 245.00 / 0.00 / 980.00`; order #2 evaluates to `800.00 / 40.00 / 7.30 / 767.30`.

`PriceOrderAsync` obtains subtotal first and then overlaps the discount and shipping calls. The sequential method awaits them one after the other. The phase demonstrates stale-read overselling with `ReserveStockUnsafeAsync`, then demonstrates the fixed per-file atomic reservation (only one of orders 10/11 can take the last keyboard). Validated orders are processed concurrently. Each order's invoice rendering uses one `Task.Run` CPU-bound loop (20,000,000 iterations); task faults are handled per order so one failed task does not stop the rest. The report is computed from saved completed orders; top-selling ties break on the lowest product ID.

### Phase 3 — Full checkout

The payment gateway creates transaction IDs as `TXN-` plus 12 uppercase hex characters. Order #5 spends five seconds in the capture step but the checkout applies a three-second timeout. It has no payment record if capture does not finish. Order #8 is declined after fraud check. Order #13's Completed save throws `DatabaseWriteException`; checkout updates that payment to Refunded, releases inventory and persists a Failed order with the transaction ID in the reason. Order #3's first two email attempts fail and attempt three succeeds. Order #17's four total attempts fail and the last email is queued in `email_retry.json`; that email failure does not change a paid order's Completed status. The sync/async comparison runs with injected failures disabled and uses orders 1–10.

For cancellation, a token is passed through the service calls and every `Task.Delay`. Per-order cancellation compensation uses a separate bounded cleanup token because the already-cancelled run token cannot be used to release stock, refund a capture, or persist `Pending / Cancelled`. Completed operations that have already left the in-progress path stay completed; an in-progress cancelled order is not marked Failed or PaymentFailed.

### Phase 4 — Scale-up tools

- Live checkout completion lines are written from a `Channel<T>` as workers report completion; the output is not produced by rescanning pending tasks.
- The customer dashboard starts profile, order, payment and email loads together with `Task.WhenAll`. On error it inspects each faulted task to print every failure (plain `await` normally throws one exception to the caller, while the `Task.WhenAll` task's `Exception` aggregates failures).
- Shipping quote tasks start concurrently. The first successful quote wins; outstanding carriers are cancelled. A 1.2-second budget returns the Flat rate fallback unless every quote failed, in which case `ShippingProviderUnavailableException("All carriers failed")` is thrown.
- Fraud scoring compares a plain sequential loop with `Parallel.ForEachAsync` limited to `Environment.ProcessorCount`; a `ConcurrentBag` collects flagged IDs without duplicates. Cancellation is checked between scores. The current scoring algorithm's specified checksum is used verbatim.
- Email resend uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4`. The running-count telemetry is only measuring concurrency; it is not used to limit it. All 30 test order IDs are idempotently upserted into the outbox.
- The fire-and-forget demonstration intentionally suppresses CS4014 only around the broken call; the fixed version awaits the email task. Unobserved exception events are runtime/GC timing dependent, so the console notes this rather than promising an event every run.

## 5. Expected output and validation checks

Expected core results from the assignment:

| Run | Expected end state |
|---|---|
| Phase 1 | 20 records; 15 Validated, 5 Rejected (#4, #9, #12, #15, #18) |
| Phase 2 | 20 records; 13 Completed, 6 Rejected, 1 Failed (#7); either #10 or #11 loses the keyboard race |
| Phase 3 full checkout snapshot | 20 records; 11 Completed, 6 Rejected, 2 PaymentFailed (#5, #8), 1 Failed (#13) |
| Phase 3 payments | 13 records: 11 Captured, one Declined (#8), one Refunded (#13); none for timed-out #5 |
| Phase 3 email outbox | 10 successful emails (order #3 Attempts=3); order #17 has 4 failed attempts in `email_retry.json` |
| Phase 3 stock | Only completed sales consume stock; Mechanical Keyboard stock ends at 0 |
| Phase 4 dashboard #3 | Orders #3 Completed and #13 Failed; payment #3 Captured and #13 Refunded; email #3 |
| Phase 4 dashboard #99 | Both `Customer #99 not found` and `Payments store timeout` errors are printed |
| Phase 4 resend | Test order IDs 1001–1030 appear once each in the outbox; observed maximum concurrent sends is 4 |

Phase 4 task execution is CPU/I/O heavy by design because the assignment explicitly requires 10,000 × 100,000 fraud iterations plus SMTP delays. All timing results are environment-dependent; they are printed with `Stopwatch` rather than hard-coded.

### Performance measurements table

The program prints the following measurements during each run. Numeric timings and thread counts must be recorded from a real run on the submission machine; they are deliberately not invented in this reference package because the .NET 10 SDK is unavailable in the build environment. Each timing uses `Stopwatch`, and thread counts use the logger's observed distinct managed thread IDs plus `ThreadPool.ThreadCount` where shown.

| Measurement | Elapsed time | Distinct threads | `ThreadPool.ThreadCount` |
|---|---|---|---|
| Phase 1 — sync validation | Printed by Phase 1 runner | Printed by runner | Before → after, printed by runner |
| Phase 1 — async sequential validation + saves | Printed by Phase 1 runner | Printed by runner | Before → after, printed by runner |
| Phase 2 — sequential pricing of Order #1 | Printed by Phase 2 runner | Printed by runner | Before → after, printed by runner |
| Phase 2 — concurrent pricing of Order #1 | Printed by Phase 2 runner | Printed by runner | Before → after, printed by runner |
| Phase 2 — concurrent order processing | Printed by Phase 2 runner | Printed by runner | Before → after, printed by runner |
| Phase 3 — 10 dedicated sync threads | Printed by Phase 3 runner | Printed by runner | Before → after, printed by runner |
| Phase 3 — 10 async tasks | Printed by Phase 3 runner | Printed by runner | Before → after, printed by runner |
| Phase 4 — customer dashboard | Printed by dashboard runner | Printed by runner | Before → after, printed by runner |
| Phase 4 — shipping quote batch | Printed by quote runner | Printed by runner | Before → after, printed by runner |
| Phase 4 — fraud scoring sequential vs parallel | Both times and speed-up printed by scorer | Sequential thread + parallel distinct threads | Before → after, printed by scorer |
| Phase 4 — resend 30 emails (max concurrency 4) | Printed by resend runner | Observed distinct threads printed by runner | Current count printed by runner |


## 6. Answers to assignment questions

### Phase 1

1. **Why are sequential sync and async times similar?** `await` is still used one order at a time, so the total simulated waiting is the sum of the same query latencies. Async improves thread utilisation, not the elapsed time of a strictly sequential workflow. This assignment adds the async save to the async pass, so its wall time can be somewhat higher.
2. **Is the thread ID before an await always the same after it?** No. If an awaited operation is incomplete, its continuation can resume on a different ThreadPool thread. The log prefix reports the actual current managed thread ID, so compare entries from a single repository call before and after `await`.
3. **What does a thread do during `Task.Delay` vs `Thread.Sleep`?** `Task.Delay` schedules a continuation and releases the current thread while the timer runs. `Thread.Sleep` blocks that OS thread for the duration.
4. **Why `Task<T>` instead of `T`?** It represents an operation that may complete later and carries the result, allowing callers to await it without blocking a thread.
5. **`File.ReadAllText` vs `File.ReadAllTextAsync`:** the sync method blocks the caller until I/O completes. The async method returns a task and lets the caller suspend; normal asynchronous file I/O does not keep a dedicated thread blocked for the entire disk wait, although runtime/platform internals may use worker threads for parts of I/O.

### Phase 2

1. **Starting a task vs awaiting it:** calling an async method starts its synchronous prefix and returns a task; `await` asynchronously waits for that task and retrieves its result or rethrows its exception.
2. **Does every Task create a thread? Does `Task.Delay` use one while waiting?** No. A Task is an operation representation, not a thread. `Task.Delay` uses a timer/continuation and does not need a dedicated sleeping thread.
3. **`.Result` / `.Wait()`:** they block the calling thread; UI/single-thread synchronization contexts can deadlock when the continuation needs that blocked context, and ThreadPool starvation is also possible. They are appropriate only for a deliberate blocking demonstration, not normal async flow.
4. **Exception from a never-awaited task:** the caller has not observed it; the operation may fail silently from the caller's perspective, and an unobserved exception may be surfaced later by runtime finalization/event mechanisms. Do not rely on that.
5. **When `Task.Run`?** Use it for CPU-bound work that would otherwise monopolise the caller (invoice rendering and parallel fraud scoring). Avoid wrapping naturally asynchronous file, database or network I/O in `Task.Run`; doing so needlessly consumes a ThreadPool thread while the I/O awaits.
6. **`Task` vs `Task<T>`:** `Task` represents completion/failure without a result; `Task<T>` additionally returns a value of type `T`.
7. **Why can't `lock` contain `await`?** A C# monitor lock must be exited by the same thread that entered it, but an await continuation may resume on another thread. Use an async-aware primitive such as `SemaphoreSlim.WaitAsync`.
8. **Why did unsafe reservation oversell?** Both requests read the same stock value before either wrote; both decided it was available. The safe version holds a per-file lock across read-modify-write and checks/decrements stock within the same atomic update.

### Phase 3

1. **Does async make one order's workflow faster?** Not inherently: the simulated latency steps still occur. Async avoids blocking threads during I/O waits and allows other orders to progress.
2. **Why does async scale better?** Many in-flight requests can suspend during I/O without reserving one blocked thread per request.
3. **What about 1,000 sync orders?** The one-thread-per-order version creates many OS threads, increases stack/memory and context-switching cost, and can fail due to resource pressure. Async typically keeps a much smaller ThreadPool footprint while many requests wait on I/O.
4. **Why essential for DB/files/APIs?** Those operations spend much time waiting; async permits higher concurrency without tying up one worker thread for each wait.
5. **Fire-and-forget problem:** the caller cannot know the email completed and cannot handle its failure; the application may exit before it finishes. Awaiting it ensures the operation is completed and faults are observed.
6. **When is fire-and-forget acceptable?** Rarely, and only when a durable queue/background worker owns the work and records retries/errors independently. A bare discarded `Task` is not a reliable queue.
7. **Why is `async void` dangerous?** Callers cannot await completion or catch its exceptions. It is acceptable for event handlers because the event-handler signature is usually `void`; catch/report exceptions inside the handler.

### Phase 4

1. **Tool choices:** Task 10.1 uses `Channel<T>` for finish-order notifications without repeatedly scanning incomplete tasks. Task 10.2 uses `Task.WhenAll` for parallel loads and inspects all faulted tasks. Task 10.3 uses concurrent carrier tasks, linked cancellation and a 1.2-second budget to select the first successful quote. Task 10.4A uses `Parallel.ForEachAsync` limited to the CPU count for CPU-bound scoring; task 10.4B uses `Parallel.ForEachAsync` with degree 4 for bounded-concurrency I/O. Starting then awaiting one-by-one would serialise completions or wait for the first task before reporting faster later tasks.
2. **How get every dashboard error?** Await the combined `Task.WhenAll`, catch, then inspect each input task's `Exception.Flatten().InnerExceptions`. A plain `await` throws an exception to the caller (usually one of the failing exceptions), while `WhenAll.Exception` aggregates all failures.
3. **What if losing carriers are not cancelled?** Their network work continues after a winner/fallback has been selected, wasting resources and potentially producing unobserved failures. Cancelling the shared linked token releases them promptly.
4. **Why is fraud scoring faster with more cores, but email resend isn't CPU-core bound?** Scoring is CPU-bound and can execute independently on several cores. Email sends are I/O-bound; most elapsed time is waiting for the SMTP-delay simulation, so bounded overlapping waits—not more CPU arithmetic capacity—drive throughput.
5. **Parallel vs concurrent async work:** parallel work executes CPU instructions simultaneously across cores. Concurrent async I/O interleaves operations while they wait; it need not use multiple cores simultaneously.
6. **Cancelled run behaviour:** Ctrl+C or `--cancel-after` cancels pending delays. Dispatch code checks the run token before starting more work; in-progress orders stop at their next cancellable step, release reserved stock, refund captures, and persist `Pending` with `FailureReason = "Cancelled"`. Valid JSON is preserved by temp-file replacement and bounded compensation tokens. Completed orders remain completed. Use `dotnet run -- phase3 --cancel-after 4000` to observe a real cancellation on your machine.

## 7. Problems encountered and resolutions

- **Lost updates in collections:** separate read and write calls could overwrite another task's changes. Fixed with `UpdateAsync` locking the full read-modify-write sequence.
- **Half-written collection JSON:** fixed with same-directory temporary files and atomic replacement.
- **Last item oversell:** unsafe code deliberately demonstrates stale reads; safe reservation performs the availability check and subtraction under one per-file update.
- **Cancellation after a successful file mutation:** repository reservations/releases remember whether the mutation committed; compensation uses an independent bounded token to complete release/refund/save despite the cancelled run token.
- **Payment captured but order save fails:** a compensation path records a refund, releases stock and persists the failed reason.
- **Email failure should not undo payment:** retry failure goes to `email_retry.json` and leaves the order Completed.
- **Exception propagation from concurrent orders:** each task is awaited/handled independently so one failure does not prevent the others from being observed.
- **Completion order differs from start order:** a channel reports each terminal result immediately to a single output reader.
- **Multiple dashboard failures:** the code inspects all faulted load tasks after `Task.WhenAll`.

## Console evidence / screenshots

The repository includes console output paths and file snapshots, but genuine screenshots cannot be recorded until the application is compiled and run locally. After `dotnet build`, run each phase in a terminal and take screenshots of the generated outputs for: race demo, faulted Phase 2 task, timeout/refund/email retry, both fire-and-forget outcomes, all Phase 4 tasks, and `--cancel-after`. Do not submit a screenshot of this README as runtime evidence.
