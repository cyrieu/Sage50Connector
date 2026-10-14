# Sage 50 Connector 1.1.13

General Ledger (TRANSACTIONS) sync no longer fails because of a few bad rows.
A row with a missing or unparseable posting order, row index, GL GUID, account,
date, amount or IncludeInGL flag is skipped and the rest of the ledger is sent.
Each export logs how many rows were skipped, grouped by reason, with the
offending values masked and the first row numbers listed. Every sync re-exports
the full ledger, so a row corrected in Sage arrives on the next sync.

The export still fails on a structural CSV error (columns after it cannot be
trusted) or when more than half the rows are invalid (a format change, not bad
data), so a broken export is never reported as a complete one.

Verification on the lab VM: `diagnostics/Test-GlSkipInvalidRows.ps1`,
`diagnostics/Test-GlZeroPostingOrder.ps1` and `diagnostics/CsvParserTests.cs`.

Customers must reopen their company as a Sage administrator and approve
Always Allow Access for this version after upgrading, and approve the
transaction (COM) prompt again.
