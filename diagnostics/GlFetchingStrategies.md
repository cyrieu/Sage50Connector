# G/L fetching strategy experiment

The owner cancelled invoice scale testing and requested investigation of optimal
G/L accounting transaction reads. This is a diagnostic experiment on the owned
MicrosoftGreatPlains lab VM, using Rutter Test Co and Sage sample data only.
It does not change production G/L behavior or publish a customer build.

The current G/L path uses COM General Ledger Rows exporter (object16), not the
.NET invoice factory. It exports all rows to CSV; date filtering is local because
Sage rejects SetDateFilterValue for this exporter (0x800436FD). Posting dates do
not identify old-dated edits, so incremental changed-record detection cannot be
replaced with a recent posting-date filter. Server content hashes deduplicate
unchanged transactions.

The production RFC4180 parser already reads records from a TextReader. The
memory growth occurs afterward: arrays for all CSV records, all row DTOs,
filtered copies, grouped transactions, and the full job cache coexist.

Compare fresh x86 processes on the exact same captured COM CSV:

- Current full materialization and paging.
- Streaming CSV parsing into a group dictionary (still retains all mapped rows).
- Streaming contiguous JournalPostOrder groups into 50-transaction pages.
- Streaming groups to a JSONL disk snapshot, then paging the snapshot.

Verify identical per-ID canonical transaction hashes, row/transaction counts,
line order, header consistency, amounts, IncludeInGL behavior, and half-open date
filters. Verify native exported JournalPostOrder ordering before trusting group
flush. Shuffled/noncontiguous input must fail clearly, not lose or split postings.

Measure native export time separately from CSV processing, first page ready,
process private/managed peaks, and snapshot disk size. Scale copied real CSV
records on disk (unique posting-order and line IDs), without creating more Sage
invoices. Diagnostic hash indexes grow with transaction count; report that
measurement overhead separately from the intended bounded reader.

The disk snapshot candidate must account for complete postings and page replay,
release COM before HTTP waits, and define cursor/restart/file cleanup behavior
before any production implementation. Large single postings bound minimum memory;
a count cap alone does not bound page payload bytes. An export is still required
before the first page; these candidates optimize connector parsing/memory, not
Sage's native export work.

## Lab measurements (2026-10-07)

Unsigned isolated x86 diagnostic on the owned lab VM. Native COM capture of
Rutter Test Co completed in **20.522 seconds**, producing 6,142,444 CSV bytes,
35,801 posting rows and 2,652 transactions. The export was already monotonic
by JournalPostOrder, contiguous by posting, and sorted by row index. The live
capture released COM before the offline benchmark began.

Customer-scale fixtures clone those real exported rows with unique posting and
line identities. They measure managed parsing/paging, not native Sage export
performance at that scale. Source data consists of synthetic sales-invoice
postings; mixed journal types and customer-specific ledger quirks remain a
separate validation requirement.

At 286,408 rows / 21,216 transactions, each mode produced the same canonical
ID-to-transaction SHA256 map. Peak process private memory and total harness time:

| Strategy | Peak private MiB | Total seconds |
| --- | ---: | ---: |
| Current materialization | 338.6 | 15.18 |
| Streaming into full dictionary | 279.4 | 15.28 |
| Streaming complete postings | 28.2 | 14.93 |
| Disk snapshot and page replay | 27.8 | 25.50 |

Timing includes canonical serialization, SHA256 verification and every page;
it excludes native export and HTTP. Baseline/dictionary first-page timing
includes hashing the whole result first; contiguous modes hash during paging.
Do not treat first-page latency as an apples-to-apples product benchmark.
Baseline/dictionary use ordinal string-ID order, while contiguous modes use
numeric posting encounter order. Payload parity is verified independently of
page ordering; a production cursor design must explicitly support that change.
All modes retain a diagnostic-only per-ID hash catalog, which grows with
transaction count. Memory is sampled, rather than an allocation proof.

At 1,145,632 rows / 84,864 transactions, all four modes produced identical transaction hashes:

| Strategy | Peak private MiB | Total seconds |
| --- | ---: | ---: |
| baseline | 1262.0 | 61.82 |
| stream-dictionary | 1042.3 | 61.92 |
| stream-groups | 48.6 | 60.04 |
| disk-spool | 47.0 | 101.80 |

The disk snapshot was 452,809,278 bytes (431.8 MiB). Exact snapshot write/read
SHA256 matched at all sizes, and temporary snapshot cleanup succeeded on both
completed and rejected runs. Native export time must be added separately; the
20.522-second observation applies only to the small live company.

All **36 benchmark cases** were checked from their JSON manifests:

- Native, 8x and 32x fixtures: identical per-ID transaction hashes and counts.
- Reversed posting groups and noncontiguous groups: baseline/dictionary preserve
  original hashes; both contiguous candidates reject ordering violations.
- Reversed line order: all modes reproduce the original payload hashes.
- Date window: inclusive lower-bound clone present, exclusive upper-bound clone
  and IncludeInGL=false clone absent, verified by exact IDs in all four sidecars.
- Comma/quote/embedded-CRLF description: identical hashes in all modes, with one
  added posting/line; no physical-line splitting.
- Empty future date window: zero transactions/lines in all modes.

## Recommendation and production work remaining

Use a validated, job-scoped disk snapshot for G/L. Stream the RFC4180 CSV by
complete JournalPostOrder groups into JSONL, then serve bounded pages from the
completed snapshot. This retains about 47 MiB in the million-row diagnostic,
compared with 1,262 MiB for materialization; it trades extra disk I/O for a
complete validation barrier before any page is published. The dictionary
candidate still scales linearly in ledger payload and is not the preferred fix.

Direct group streaming is faster, but can discover a late ordering violation
only after earlier pages have been assembled. The diagnostic does not send
those pages to Rutter. Production must validate the whole export first, or
use the completed snapshot barrier; fail clearly on unexpected order, or use a
bounded external-sort fallback. Do not silently switch to unbounded grouping.

The snapshot must become a job resource, with cursor/read-offset and replay
rules, cleanup on success/failure/startup, available-disk checks, and complete
posting/byte-aware page sizing. The current experiment verifies write-to-read
replay within one process only. It does not implement crash persistence or HTTP
retry recovery; retain existing restart-from-beginning semantics unless durable
job identity and cursor validation are explicitly implemented. A single unusually
large posting sets a minimum memory requirement and must never be split.

Keep full-ledger reads for changed-record sync so old-dated edits are included;
posting-date filtering is appropriate for historical ranges, not modification
detection. Release COM before parsing/network waits. Snapshot files contain
financial payloads and need appropriately restricted permissions and cleanup.

These are isolated diagnostics, not a production G/L code change or backend
end-to-end ingest test. Production integration still requires queue/cursor/retry,
restart and normalization tests, plus a representative mixed-journal ledger.
Raw captured CSV remains private on the lab VM/local temporary directory; only
redacted metrics are retained under ignored diagnostics/artifacts.
