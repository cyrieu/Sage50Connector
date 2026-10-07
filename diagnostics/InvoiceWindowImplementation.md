# Bounded invoice reads — implementation validation

The production `INVOICES` LIST_FETCH path now uses `InvoiceWindowReader` and the
existing invoice mapper. It inventories original GUIDs, discovers historical
dates with a sorted SDK header/key load, then reads SDK date-window keys and
loads invoices individually for page-sized mapping. Windows start at no more
than 31 days and split when they contain over 250 keys. A dense single day
retains keys rather than a hydrated SDK list. Missing or moved dates are covered
by an original-GUID fallback. No financial payloads are persisted locally.

## Live SDK comparison

On the owned Windows lab, an unsigned x86 .NET Framework 4.8 diagnostic build
of the changed connector read `Rutter Test Co` through the production reader.
The October 7, 2026 UTC run matched the earlier full-DTO baseline:

- 650 invoices, 8,114 used lines, 13 pages, 2,961,830 serialized bytes.
- Complete DTO fingerprint, ordered by invoice GUID:
  `313e26a5d58da77abe51cf9da378b722ca46338f160e9e1ec2da325a167cdffd`.
- First page: 8.55 seconds; total: 57.26 seconds.
- Observed peak private memory: 59,396,096 bytes (56.6 MiB), versus
  94,978,048 bytes (90.6 MiB) for the earlier full-DTO run: approximately 37%
  lower. Observed peak managed heap: 11,976,504 bytes (11.4 MiB).

This measurement includes historical extent discovery and individual factory
loads, not just the earlier explicit daily-window prototype. Sampling can miss
short-lived peaks. This fixture does not establish a memory ceiling for 60K
invoices or an exceptionally large single invoice. Reference indexes and key
inventories still scale with company size.

## Paging and transport checks

`--test-invoice-reader` passes synthetic cases for a dense 600-record day,
adaptive window splitting, missing/moved dates, original membership and new-key
exclusion, page limits, identical pending-page reuse, foreign session cursors,
late acknowledgement after disposal, and fully filtered jobs.

`Test-InvoiceIngestDelivery.ps1` uses the actual production invoice handler and
HTTP transport with a synthetic reader. It verifies a 502 retry, rejected final
page retention, cleanup only after successful acknowledgement, byte-identical
report replay, and actual Sage shutdown followed by late acknowledgement and an
explicit `restart_from_beginning` request. All seven expected requests were observed.

`Test-InvoiceFilterPredicates.ps1` checks null/default timestamp policy and the
inclusive `updated_at` / exclusive `updated_before` boundaries without opening
Sage. The original local predicate is reused; SDK timestamp filtering is not
introduced.

## Real Rutter ingest

The changed connector ran against a newly created, migrated local PostgreSQL
Rutter database through the real `/versioned/ingest` API and a reverse SSH tunnel.
The diagnostic used the approved interactive-user executable and its production
poll/report and LIST_FETCH handlers.

- Full read: 13 accepted pages, then NOOP. The desktop job is `completed`.
- Persisted records: 650 platform rows / 650 distinct invoice IDs; all 650 have
  normalization timestamps, with no pending normalization flags.
- Normalized `n_invoices`: 650 rows / 650 distinct platform IDs.
- Stored invoice lines: 8,114, all with nonempty IDs.
- Associated `refresh_entity_runs`: `SUCCESS`, completed timestamp present,
  650 new rows and 0 updated rows.
- A second job with a future LastSavedAt cutoff emitted one empty final page.
  Its job completed and its refresh entity run succeeded with 0 new / 0 updated;
  existing invoice and line totals remained unchanged.

A forced-process-restart test exercised the backend cursor-reset path but did
not complete a full replay. In three isolated attempts, the first 50-row page
was accepted, the diagnostic process was force-stopped, and the backend then
reset the same job to page one (`restart_count = 1`, cursor cleared). On each
attempt the new SDK reader failed with `License is currently unavailable. You
have reached the maximum number of connections, please try again later` before
it could replay the first page. Restarting only Sage 50 Connect Service after
the process death and closing the Sage company did not clear this condition.
The backend reset behavior is confirmed; recovery of the real SDK reader after
an ungraceful process death is **not a pass** and needs an SDK/service recovery
strategy before relying on automatic crash replay. The separate synthetic HTTP
test still passes the connector's restart-from-beginning protocol.

The first wire-capture prototype hashed JavaScript-reserialized records, which
can change decimal token formatting relative to Json.NET. Its aggregate hash
mismatch is therefore inconclusive and is not evidence of different payload
values. Full DTO parity is established by the independent live SDK benchmark's
GUID-sorted fingerprint; the real backend ingest is independently confirmed by
persisted row and line counts. A corrected raw-JSON capture was used for the
restart attempts, but those captures contain only the initial 50-row page and
are not a full-run fingerprint comparison.

An additional Bellwether baseline/filter comparison registered a separate
company-specific SDK request and stopped at `Pending`; it has not passed. The
approved Rutter Test Co fixture has no missing LastSavedAt values and only used
direct sales lines. All four line families share the unchanged mapper, but
live null-timestamp/other-line-family coverage remains a separate check. The
fixture contains 650 invoices, so it does not establish a 60K memory ceiling.

## Reproduction

Build an isolated diagnostic copy with `Build-InvoicePagingBenchmark.ps1`.
After Sage approves that exact executable for the test company, run
`Run-InvoicePagingBenchmark.ps1 -Mode production-windows`.
The diagnostic also provides `--test-invoice-reader`,
`--compare-invoice-filters <company> <result.json>`, and
`--e2e-invoice-fetch <protected-config.json> <result.json>`.
The last command accepts only Rutter Test Co and a localhost HTTP endpoint,
including a reverse SSH tunnel to the isolated backend. Keep credentials and
raw results outside version control. Summary measurements are retained under
ignored `diagnostics/artifacts/invoice-window-implementation-20261007/`.

## Subsequent completed E2E verification

A later isolated-backend run completed the full read of 652 invoices / 8,149
lines with raw-JSON payload fingerprint parity. All 652 invoices normalized,
no lines had empty IDs, the desktop job completed and its refresh run succeeded.
Ten newly seeded invoices then produced exactly ten incremental rows; totals
were 662 invoices / 8,256 lines. A future cutoff returned zero changes. Peak
private memory for the full run was 60,874,752 bytes (58.1 MiB).

The requested customer-scale overnight test was subsequently cancelled by the
owner. Its last 1,000-invoice seed batch completed normally, leaving 2,652 test
invoices. No customer-scale invoice fetch/ingest success is claimed. Earlier
forced-kill SDK license recovery limitations remain as described above.
