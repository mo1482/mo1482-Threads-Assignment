# Assignment 03 — Locking Research

## 1. What is `System.Threading.Monitor` used for?

`System.Threading.Monitor` is a .NET synchronization mechanism used to protect shared data when multiple threads may access it at the same time. A thread calls `Monitor.Enter` to acquire an object's lock and `Monitor.Exit` to release it. While one thread owns that lock, other threads trying to acquire the same lock must wait.

`Monitor` also provides coordination methods such as `Wait`, `Pulse`, and `PulseAll`. These allow threads to wait for a condition and notify other waiting threads when shared state changes.

Use a dedicated reference-type object for synchronization. Do not lock on value types, strings, or publicly accessible objects, because that can lead to incorrect synchronization or unexpected lock contention.

## 2. What does the C# `lock` statement compile to?

For a normal reference-type lock target, the C# compiler translates `lock` into a pattern that calls `Monitor.Enter` and `Monitor.Exit`. The `Enter` call is protected by a `try`/`finally` structure so that the lock is released even if the code inside the critical section throws an exception. The compiler also evaluates the lock expression only once.

Conceptually, this:

```csharp
lock (_gate)
{
    count++;
}
```

is equivalent to a pattern like this:

```csharp
bool lockTaken = false;

try
{
    System.Threading.Monitor.Enter(_gate, ref lockTaken);
    count++;
}
finally
{
    if (lockTaken)
    {
        System.Threading.Monitor.Exit(_gate);
    }
}
```

Important version note: in modern C# (C# 13 and later), when the expression has the special `System.Threading.Lock` type, the compiler uses that type's dedicated locking mechanism rather than the traditional `Monitor` pattern. The explanation above applies to the usual `object` lock target.

## 3. At least two differences between `lock` and direct `Monitor` calls

1. **Simplicity and safety:** `lock` automatically releases the lock when the block exits, including when an exception occurs. With direct `Monitor.Enter` / `Monitor.Exit`, the developer must use `try`/`finally` correctly to avoid accidentally leaving the lock held.
2. **Timeout support:** a normal `lock` waits until it can acquire the lock. `Monitor.TryEnter` can attempt to acquire it immediately or wait for a specified timeout, allowing the program to take another action if the lock is unavailable.
3. **Thread coordination:** `lock` by itself only protects a critical section. `Monitor.Wait`, `Monitor.Pulse`, and `Monitor.PulseAll` support coordination between threads that share the same monitor.
4. **Control versus readability:** direct `Monitor` APIs provide more control, but are more verbose and easier to misuse. For ordinary mutual exclusion, `lock` is generally the clearer choice.

## 4. When might a developer use `Monitor.TryEnter`, `Monitor.Pulse`, or `Monitor.Wait`?

- **`Monitor.TryEnter`:** use it when a thread should not wait forever for a lock. For example, a program can wait up to 100 milliseconds, then log the contention or skip a nonessential operation if it cannot acquire the lock.
- **`Monitor.Wait`:** use it when a thread holds a monitor but must wait until a condition becomes true. `Wait` temporarily releases that monitor and later reacquires it before returning.
- **`Monitor.Pulse`:** use it when a thread changes shared state and wants to wake one thread waiting on that same monitor. `Monitor.PulseAll` wakes all waiting threads. A woken thread must reacquire the lock and should check the condition again.

`Wait`, `Pulse`, and `PulseAll` must be called by a thread that currently owns the monitor. Waiting code should check its condition in a `while` loop because the condition may still be false when the thread reacquires the lock.

## 5. The same critical section using `lock` and `Monitor.Enter` / `Monitor.Exit`

Both examples protect the increment of a shared counter with the same lock object.

### Version A — `lock`

```csharp
using System;

public class CounterWithLock
{
    private readonly object _gate = new();
    private int _count;

    public void Increment()
    {
        lock (_gate)
        {
            _count++;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }
}
```

### Version B — direct `Monitor.Enter` / `Monitor.Exit`

```csharp
using System;
using System.Threading;

public class CounterWithMonitor
{
    private readonly object _gate = new();
    private int _count;

    public void Increment()
    {
        bool lockTaken = false;

        try
        {
            Monitor.Enter(_gate, ref lockTaken);
            _count++;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_gate);
            }
        }
    }

    public int Count
    {
        get
        {
            bool lockTaken = false;

            try
            {
                Monitor.Enter(_gate, ref lockTaken);
                return _count;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_gate);
                }
            }
        }
    }
}
```

The two versions provide the same mutual-exclusion behavior for this example. The direct version uses `lockTaken` and `finally` to make sure `Exit` is called only when the monitor was successfully acquired. This is why `lock` is usually preferred unless a feature of the `Monitor` API is needed.

## Sources

1. Microsoft Learn — [Monitor Class](https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor)
2. Microsoft Learn — [The `lock` statement (C# reference)](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock)
