# Customer-scale invoice validation

The run uses only the owned lab's **Rutter Test Co**, an unsigned isolated
`C:\src\InvoiceWindowOvernightV3` build, and the fresh local database
`sage50_invoice_scale_20261007`. It never uses the installed connector's config.
No signed release or customer deployment is part of this test.

Target: **60,000 invoices and approximately 750,000 used lines**. The existing
fixture has 650 invoices / 8,114 lines; references are MEM00000001 and
MEM00000003 through MEM00000651 (MEM00000002 was never saved). The V3 seed smoke
adds references 652 and 653. The pipeline checkpoints at 652, 10,000, 30,000,
and 60,000 persisted invoices. The first checkpoint also inserts ten more
invoices and verifies a nonempty incremental sync reports exactly ten rows.
Later seed batches skip those existing references without duplicating them.

At the previously measured roughly one invoice/second, 60K seeding alone may
take around 17 hours. Benchmark and ingest checks add time. Earlier checkpoints
provide evidence before the final target is reached; elapsed time is measured
rather than assumed. All dates remain within the verified August 2026 period.
The synthetic RUTMEMTEST customer receives a $100M credit limit while its other
payment terms are preserved. Each synthetic invoice has 5–20 sales lines.

## Pipeline

`Run-InvoiceScalePipeline.ps1` runs as an interactive-token scheduled task under
the approved Windows user. It seeds in 1,000-invoice processes, with progress
manifests every 100 new invoices. A resumed batch skips existing MEM references
and appends its row manifest; it never overwrites an invoice. SDK sessions close
normally between processes. It benchmarks the actual production reader at each
checkpoint, then waits for an E2E acknowledgement before continuing.

`InvoiceScaleController.py` runs on the Mac as a detached process. It keeps the
Mac awake while alive, maintains a reverse-SSH tunnel to the local capture proxy,
and watches the Windows pipeline. At each checkpoint it queues an invoice job
in the isolated DB, invokes the real production poll/report handler on Windows,
and independently verifies:

- Job status COMPLETED and refresh entity status SUCCESS.
- Expected raw and normalized invoice counts, unique IDs, used line counts,
  nonempty line IDs, and no pending normalization.
- Wire payload fingerprints match the live SDK benchmark fingerprint.
- Future-cutoff incremental fetch completes without changing persisted counts.
- At the first checkpoint, a real ten-invoice insert is recovered by an
  incremental fetch that reports exactly ten new invoices.

A failed check writes a failed acknowledgement and stops further fixture growth.
The controller never force-kills a live SDK process. Interrupted SDK processes
previously leaked licensed connections; the hard-crash recovery test remains a
separate unresolved issue, not an implied pass from this scale test.

## Inspect / operate

Protected local state lives in `/tmp/sage-invoice-overnight/`. Read
`controller-state.json`, `vm-state.json`, and `checkpoint-*.json` for sanitized
results. **Do not print `config.json` or `env.json`**: they contain credentials.
The backend runs on local port 4008; the proxy on 4007; the VM reverse tunnel on
14007. The private state directory contains owned process PID files.

Windows progress lives in `C:\src\InvoiceWindowOvernightV3\results`:
`scale-pipeline.json`, `seed-*-attempt-1.json`, `production-windows-*.json`,
and `e2e-ack-*.json`. The pipeline task is `Sage50InvoiceScalePipeline`.

An Orca automation checks every ten minutes in the existing Sage50Connector
workspace. Its ID is recorded in the private state's `automation-id.txt`.
It reports stage/count changes and failures, then disables itself on completion
or failure. It does not send Slack/email messages.

Launch prerequisites: approved V3 binary, healthy SDK access, successful two-row
seed smoke, healthy isolated backend and local ingest token pair. Copy the
protected config to the diagnostic's `e2e-secrets` directory only after those
checks. Start the controller and Windows pipeline, then verify the first real
E2E checkpoint before leaving the larger seed run unattended. Final cleanup
must gracefully close diagnostics, stop only the recorded local backend/tunnel,
remove protected configs, and drop only this explicitly named scratch database
once results are retained. Keep the synthetic fixture for subsequent lab tests.

## Preparation status (2026-10-07 03:03 UTC)

V3 compiled successfully as x86 .NET Framework 4.8. The pipeline PowerShell
parser and local Python/Node syntax checks passed. The fresh local database
migrations completed, an isolated development token/item pair was created,
and a read-only ingest poll returned HTTP 200 / NOOP. The private config is
provisioned with restricted ACLs. The ten-minute monitor is enabled.

**The larger seed/E2E pipeline has not started.** SDK seat exhaustion also
blocked opening the company in Sage itself. After the owned VM reboot, services
are running but no Windows user is logged on. Windows App requires the owner to
reconnect RDP; the prior instruction prohibits retrieving/entering the account
password. SDK access, V3 approval, and the two-invoice seed smoke still need to
pass. Existing 650-row E2E evidence is in InvoiceWindowImplementation.md; no
customer-scale success is claimed yet. The private local launch helper is
`/tmp/sage-invoice-overnight/launch.py`, to be called only after those prerequisites.

## Started and first E2E checkpoint verified (2026-10-07 13:20 UTC)

The owner reconnected RDP. V3 was approved in Rutter Test Co, and seed attempt 3
saved refs 652–653 (35 lines). The Windows pipeline and detached local controller
are now running; the 10-minute monitor is enabled. The first checkpoint passed:

| Check | Result |
| --- | --- |
| Full-range invoice fetch | 652 invoices / 8,149 lines; job completed; refresh SUCCESS |
| Normalization | 652 normalized; no pending rows; no empty line IDs |
| Wire parity | Fingerprint matched live SDK benchmark exactly |
| Actual ingest peak private memory | 60,874,752 bytes (~58.1 MiB) |
| Nonempty incremental | Exactly ten new invoice rows; zero updated rows |
| After incremental | 662 invoices / 8,256 lines persisted and normalized |
| Future-cutoff incremental | Empty final page; zero new or updated rows |

Sanitized evidence is retained under
`diagnostics/artifacts/invoice-scale-20261007/`. The live seed phase is now
building the 10K fixture in resumable batches, followed by 30K and 60K. Larger
scale results are **pending**; the small checkpoint is not proof of 60K success.
The historical full-range LIST_FETCH is inserted directly in the isolated job
queue; this tests production ingest/normalization, not the entire refresh
scheduler or Link onboarding lifecycle. All backend refresh entity runs and job
completion statuses are checked independently. Hard-crash recovery remains a
separate unresolved test as documented above.

## Cancelled by owner (2026-10-07)

The owner cancelled the invoice scale test and redirected testing to G/L fetching
strategies. Further batches and the ten-minute automation are disabled. The
local controller, its reverse-SSH tunnel, and awake guard were stopped. The
independent active 1K SDK seed process is allowed to finish its current batch
normally so it closes Sage without leaking a licensed session; no further
invoice batches or scale checkpoints will be launched. Retain the already
verified small E2E evidence and existing test fixture. Larger-scale invoice
validation remains incomplete and is not claimed as passed.

The final active batch subsequently completed cleanly at
2026-10-07T14:07:17.749Z: 1,000/1,000 created, next reference number 2654.
The test company now contains 2,652 seeded invoices. No invoice seed task
remains active, and the pipeline scheduled task is disabled.
The isolated local backend used only for this invoice scale run was also stopped;
its database and captured evidence remain available for later inspection.
