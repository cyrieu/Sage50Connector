# Invoice paging and memory validation (2026-10-06 Eastern; 2026-10-07 UTC)

This report validates candidate invoice-reading strategies against the owned
Windows Sage 50 lab and the dedicated `Rutter Test Co` company. It changes no
production connector path and makes no Rutter ingest requests. The executable is
an unsigned, isolated diagnostic build. The synthetic customer and invoices were
created in the test company only.

## Environment and dataset

- Windows 11 Pro, x64; Sage 50 Premium Accounting 2026.1.00.0207.
- Diagnostic executable: x86, .NET Framework 4.8; MSBuild 17.14.51.32402.
- `Rutter Test Co`: 650 synthetic invoices dated within August 2026, 8,114 used
  direct sales lines, no used sales-order, proposal, or retainage lines.
- Each comparison ran in a fresh process. Date windows covered
  `[2026-08-01, 2026-09-01)` as 31 daily half-open SDK `Date` loads.
- Each mode emitted 13 diagnostic pages of 50 records, counted serialized JSON
  bytes, and fingerprinted the complete mapped invoice payload. The reference
  index and company open time are included in first-page and total wall-clock
  measurements.

The dataset does not exercise ancient invoices, missing `LastSavedAt`, missing
document dates, fiscal-year discovery, or line collections other than direct
sales lines. The seed manifest covers 647 of the 650 invoices across 31 dates,
with at most 24 manifested rows on any one date; even if all three initial smoke
invoices fell on that same date, no daily window exceeded 27 rows. Thus this run
did not test a window larger than the 50-record page size. Separate read-only sample
company work found historical invoices and many missing `LastSavedAt` values;
the existing transaction-fetch report records that evidence. These synthetic
invoice runs do not establish coverage of those cases.

## Results

| Mode | First page | Total | Peak private bytes | Private delta | Peak working set | Working-set delta | Peak managed heap |
|---|---:|---:|---:|---:|---:|---:|---:|
| Full DTO materialization | 56.30 s | 56.51 s | 90.6 MiB | 79.2 MiB | 123.8 MiB | 106.7 MiB | 41.5 MiB |
| Retained SDK list + sorted public keys | 15.73 s | 56.40 s | 96.4 MiB | 84.9 MiB | 128.8 MiB | 111.7 MiB | 47.6 MiB |
| Daily SDK date windows + page-sized mapping | 8.34 s | 55.15 s | 56.9 MiB | 45.5 MiB | 90.2 MiB | 73.0 MiB | 11.9 MiB |

Peaks are absolute process values; deltas subtract each process's initial value.
MiB means bytes divided by 1,048,576. In-process sampling ran every 100 ms;
an independent process sampler recorded at 250 ms. These are observed peaks for
this 650-row run, not a proof of a fixed memory ceiling at larger scales. Page
serialization occurred inside the sampling interval and page bytes were
counted, but a short-lived allocation between samples could exceed the recorded
peak.

All modes reported 650 source/matched invoices, 8,114 used lines, 13 pages,
2,961,830 serialized bytes, and no missing `LastSavedAt`. The GUID-ordered
aggregate SHA-256 fingerprint of each complete mapped payload was identical in
all three modes:

```text
313e26a5d58da77abe51cf9da378b722ca46338f160e9e1ec2da325a167cdffd
```

The fingerprint comparison includes the mapper's complete DTO and mapped line
collection contents; it is an aggregate equality check, not a separately stored
row-by-row manifest comparison. The synthetic records populated only the direct
sales-line collection, so equality does not validate the other invoice line
families.

Retaining the SDK list and paging DTO construction substantially improved
first-page time, but it did not reduce memory: its private and managed peaks
were slightly higher than full materialization. At this scale, loading the
company's date-qualified daily windows reduced the absolute private-memory
peak by about 37% (90.6 to 56.9 MiB) and the private-memory delta by about 42%
(79.2 to 45.5 MiB); the managed peak fell by about 71% versus the full DTO
baseline, while total time stayed about the same. That result supports testing
date-windowed reads as a memory candidate; it does not prove that Sage releases native allocations or
that the peak remains bounded for a 60K-invoice company. The date-window harness
also retains all payload fingerprints to compare runs, an O(N) diagnostic cost
that a production implementation should avoid or replace with a bounded
correctness strategy.

The row-creation run reached 650 invoices at roughly one invoice per second.
The fixture writer performs one invoice `Create`, builds used lines, calls
`Validate`, and calls `Save` for each invoice. The SDK sample follows this
per-invoice pattern. The installed XML reference did not document a bulk sales
invoice import API; a generic `PersistentList.Save()` exists, but using it as a
batch write was not validated. The observed rate does not identify whether
validation, persistence, or another SDK operation dominates, and is not evidence
of an unavoidable SDK limit. At that rate, 10K/60K fixture creation was not a
practical extension of this run.

The writer stopped when Sage's validation rejected the next synthetic invoice
because of the test customer's configured credit ceiling. The customer edit
screen showed that the numeric ceiling was disabled while credit status was set
to “No credit limit.” No further invoice writes were attempted after that
validation failure. This is why the evidence is reported at 650 rows rather
than 1K.

## Reproduce

Copy the repository into a fresh lab diagnostic destination and run
`Build-InvoicePagingBenchmark.ps1` there. It produces an unsigned x86 diagnostic
copy; it does not replace the connector checkout. Run each mode in its own
interactive-user process against the authorized test company:

```powershell
.\Build-InvoicePagingBenchmark.ps1 -Destination C:\src\Sage50InvoicePagingDiagnostic
.\Run-InvoicePagingBenchmark.ps1 -Mode full-dto -Destination C:\src\Sage50InvoicePagingDiagnostic
.\Run-InvoicePagingBenchmark.ps1 -Mode retained-keys -Destination C:\src\Sage50InvoicePagingDiagnostic
.\Run-InvoicePagingBenchmark.ps1 -Mode date-windows -StartDate 2026-08-01 -EndDate 2026-09-01 -Destination C:\src\Sage50InvoicePagingDiagnostic
```

The harness result JSON contains counts, timings, byte totals, fingerprint,
private bytes, working set, managed heap, and sample count. For independent
250 ms process sampling, run `Run-InvoiceMemorySampling.ps1 -Mode <mode>` in
parallel with the benchmark, against the exact executable path. Do not include
Sage SDK XML, raw financial records, seed manifests, or raw result files in the
repository; the summary JSON, memory CSVs, and loopback transcript used for this
report are retained under the ignored `diagnostics/artifacts/invoice-paging-20261006/`
directory. No invoice payloads or seed manifest rows are copied there.

## Delivery and recovery evidence

The local ingest-delivery loopback diagnostic passed its retry and acknowledgment
checks: transient 502/503/504 retries, terminal 400 behavior, identical replay
bodies, page retention until acknowledgment, final-page acknowledgment,
error-report replay, and cleanup after acknowledgment or company switch. This
test exercises the existing delivery code with a local mock listener; it does
not exercise a Sage-backed paging cursor through a production server or a real
process restart.

The SDK list's native-memory release behavior after dropping each window and
the safety of closing/reopening Sage sessions between windows remain unproven.
The benchmark runs one company session for the full process. Production upload
failure recovery, exact cursor replay across date windows, and process
restart-from-beginning behavior still require integration validation.

## Recommendation and remaining checks

Use a bounded date-window reader with page-sized DTO mapping as the leading
memory candidate, while keeping the existing local job-window predicate and
complete-history semantics. The current invoice job has no document-date
bounds, and current fiscal-year bounds cannot establish complete history: the
separate sample-company read found older invoices. Production needs a discovery
strategy that covers old and missing dates without first retaining an
unbounded invoice list. Keep the retained-key approach only as a first-page
latency option when memory measurements show it is acceptable; this run did
not show a memory benefit.

A multi-window cursor must encode both the date-window identity and position/key
inside the window. A bare GUID cursor cannot represent chronological windows
because GUID ordering is unrelated to invoice dates. Backend source inspection
shows the cursor is treated as an opaque string and echoed/stored, so this
encoding appears compatible without a schema change; full connector/backend
runtime behavior has not been tested.

Before production rollout, verify full payload behavior for missing timestamps
and dates, ancient dates, exact boundaries, all four invoice line families,
windows larger than a page, native-memory behavior at substantially larger
datasets, safe SDK/session release, upload failure replay, and process restart.
The present evidence supports a candidate architecture at 650 synthetic rows;
it does not validate the production cursor integration or prove 60K-scale
bounded memory.
