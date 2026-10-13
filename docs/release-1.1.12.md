# Sage 50 Connector 1.1.12

Accept General Ledger rows that Sage exports with posting order 0. FatPipe's
real company file has such rows, and 1.1.11 failed the whole TRANSACTIONS
export on them ("JournalPostOrder '0' is not a positive integer"). Each
posting-order-0 row is now sent as its own single-line transaction
(`gl:0:{GL GUID}`), so unrelated rows are never merged into one bogus entry.
Every export that contains them logs a summary without ledger text: row count,
how many post to the GL, journal codes and date range. Negative posting orders
still fail closed.

Verification: `diagnostics/Test-GlZeroPostingOrder.ps1` on the lab VM against a
native Sage GL export with posting order zeroed on rows from four entries;
`diagnostics/CsvParserTests.cs` still passes.

Customers must reopen their company as a Sage administrator and approve
Always Allow Access for this version after upgrading, and approve the
transaction (COM) prompt again.
