# Assignment 01 — Invoice export

**SIMULATION — Software House & Academy**

SwiftBite runs a nightly invoice export. For each pending order the system loads the order from a database, reads the customer profile from a file, and writes an invoice file to disk.

## Run

```bash
dotnet run --project InvoiceExport
```

## What you should do

1. Run the project and observe how long it takes and how the work is ordered.
2. In `ANSWERS.md`, describe the problems with this design when many orders must be exported.
3. Change the export pipeline so waiting on the database and files does not block the way it does now. Keep the same business flow: load order → read customer → write invoice.
4. Write what you changed in `ANSWERS.md`.

## Submit

- Updated project code
- `ANSWERS.md`
