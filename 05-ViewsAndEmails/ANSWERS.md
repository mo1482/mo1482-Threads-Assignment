# Assignment 04 — Answers

## Why Expected and Actual disagree

`Views++` is not an atomic operation. It involves reading the current value, adding one, and writing the result back. When multiple threads do this at the same time, their operations can overlap and some increments are lost. Therefore, `Actual` can be lower than `Expected` (400,000).

## What you changed

I replaced `Views++` with `Interlocked.Increment(ref Views)`. `Interlocked.Increment` increments the integer atomically, so concurrent calls do not overwrite one another's updates. For this single-counter operation, it is simpler and more appropriate than taking a lock.

## When would you protect the update differently

I would use `lock` (or another synchronization mechanism) when the update must coordinate multiple fields or steps as one indivisible operation, or when I need to maintain a larger invariant. `Interlocked` is ideal for simple atomic operations on a single value; it does not make a sequence of several operations atomic.
