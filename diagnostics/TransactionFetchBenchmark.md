# Transaction fetch experiments (2026-10-05)

Read-only experiments on the owned Windows lab, using Sage 50 2026.1 and
Bellwether Garden Supply. These establish which SDK access paths preserve data;
they do not reproduce FatPipe's 46-hour bill read or prove a customer speedup.
The normal connector entry point, project and customer configuration are unchanged.

## Reproduce

Copy these diagnostic files into the Windows checkout, then run
`Build-TransactionFetchBenchmark.ps1` with a fresh `-Destination`. It builds a
separate x86 .NET Framework development executable. Run that executable as the
interactive Windows user with:

```powershell
Sage50Connector.exe --benchmark-fetch C:\path\results.json
```

Renew the development build's Bellwether approval using the normal close/reopen
company procedure if it returns exit code 2. No Rutter credentials are loaded,
no ingest calls are made, and no Sage transactions are written. Exit code 0 means
the investigation completed; consult `failures` and each observation, because
unsupported or incorrect candidate paths are deliberately recorded as failures.

The harness compares record GUID sets, duplicate counts and fingerprints of
selected financial header fields and used line collections against an unfiltered
SDK read. This is not complete DTO-schema equivalence. For bills and invoices it
also exercises the existing connector mapper and compares its complete ID set.
Timings include diagnostic reflection/fingerprinting and use repeated reads in
one process, so warm caches and run order affect them. Managed-memory deltas do
not measure native SDK memory or establish a bounded-memory implementation.

## Findings

| Candidate | Result |
|---|---|
| Retain a loaded list and enumerate records incrementally | Same IDs and selected fingerprints across all five factories; first 50 records can be processed before the full list is walked |
| Capture public `Keys`, sort GUIDs, read public key indexer | Same contents across all five; inexpensive key capture on this sample, but no consistent total-read speedup |
| SDK sort by `Key.Guid` | Rejected: property paths accept `Class.Property`, not three levels |
| SDK sort by `Date` | Contents preserved across all five |
| Month-sized half-open `Date` filters | Combined results preserve contents across all five; empty and single-day boundaries pass |
| Exact intraday `LastSavedAt` filter | Incorrect subsets on bills, invoices and receipts; timestamp precision cannot be assumed |
| Widen `LastSavedAt` bounds to whole days, then exact local post-filter | Correct tested timestamped subsets; cannot by itself preserve missing-timestamp rows |
| `LastSavedAt == null` / OR null with minimum date | `NullReferenceException` inside SDK `PersistentList.Load(LoadModifiers)` |
| `LastSavedAt == DateTime.MinValue` | Returns zero despite sample rows exposing minimum timestamps |

Use fully qualified SDK property paths, e.g. `PurchaseInvoice.Date` and
`SalesInvoice.LastSavedAt`. Bare `Date`/`LastSavedAt` paths are rejected as
unknown classes. This matches Sage's Lists Example sample's path construction.

Baseline sample counts: 59 bills, 107 invoices, 13 journal entries, 146 payments
and 40 receipts (365 total). Missing/minimum `LastSavedAt`: respectively 55, 98,
13, 146 and 37. All sampled document dates were populated. Some invoices and
payments are dated 2021 while the current fiscal year is 2026: using current
fiscal bounds to discover all transaction history would lose records.

The final run completed with 163 observations and 35 nonpassing candidate
checks (the rejected sorts and unsafe saved-time/missing-value filters above).
Retained enumeration, sorted-key access, date sorting, monthly date batches,
empty date windows and single-day windows each passed for all five factories.
Widened-day saved-time queries with exact local post-filtering passed for all
three factories that had populated timestamps; the other two had none to test.

In the qualified-path run, bill/invoice `Load()` returned in about 67/17 ms;
walking headers and lines took about 2.3/4.6 seconds. Existing full DTO reads
took about 2.8/6.8 seconds. Thus this sample puts most work after `Load()`, but
FatPipe timing logs are still required to locate its bottleneck. Inventory
reference-index construction cost roughly 0.62–0.67 seconds versus 0.15–0.20
without inventory items. Removing that lookup requires preserving item IDs
where the production payload currently resolves them; dropping it is not a
verified equivalent mapping.

## Implementation direction

1. First implement page-sized materialization over a retained public SDK list
   or captured public key sequence, rather than constructing every transaction
   DTO before the first upload. Keep a stable cursor sequence and exact replay
   semantics. Test session release after upload failure: retained SDK objects
   may become unusable when the company/session closes. Do not rerun full
   `Load().Skip().Take()` for each page.
2. Push existing explicit document-date job bounds into qualified SDK filters,
   with the existing local filter retained for correctness. For larger jobs,
   split known complete history into bounded date windows and emit pages per
   window. Benchmark real first-page latency and peak memory; monthly windows
   are not guaranteed to be faster or to contain at most one page.
3. Consider widened-day `LastSavedAt` filtering only where the job explicitly
   excludes untimestamped rows. Preserve the exact local timestamp predicate.
   Jobs that include untimestamped rows need a proven missing-value query or a
   complete fallback read. Never replace modification-time filtering with
   document-date filtering to infer which old transactions were edited.

These experiments do not implement an ingest streaming cursor, retry recovery
for an SDK-backed snapshot, or customer rollout. Raw local reports are kept in
ignored `artifacts/sage50-fetch-benchmark/`; Sage's licensed SDK documentation
is not committed.
