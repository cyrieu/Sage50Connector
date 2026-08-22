# Initial and incremental sync on a reverse-polled desktop platform

How a Sage 50 sync is scheduled, how completion is observed, and the
connector-side optimizations that keep multi-page fetches from going quadratic.
Read `CLAUDE.md` first for the ingest protocol, and the Rutter-side platform doc
(`rutter-backend: src/platformization/platforms/sage_50/CLAUDE.md`) for how jobs
are generated.

## What happens today (post sync-OS work)

```
complete-setup / link-verify recovery
  └─ enqueueSage50HistoricalSyncJobs (QBD-style multi-batch)
       Batch 0 (ready): foundation full + temporal LastSavedAt ≥ now−12m
       Batch 1 (historical): temporal now−12m > LastSavedAt ≥ epoch
         (updated_before bound; untimestamped rows only in deepest batch)
  ⋮
refresh scheduler (side or full)
  └─ sage_50 sync/config.ts → buildSage50ListFetchStrategy.fetcher()
       • after / before from fetchMetadata
       • INSERT desktop_platform_jobs (ENQUEUED, priority by entity stage,
         parameters={updated_at, updated_before?, limit})
       • does NOT open/complete RefreshEntityRun (asyncDesktopCompletion)
       • return { data: [], nextCursor: null }   ← enqueue succeeded only
  ⋮  minutes later
connector poll → selectNextJob (priority ASC, stage-gated) → job served
  └─ JobFetchCache: Load Sage once per job_id, page from memory
  └─ handleIngestListFetchJob
       • first page → insert RefreshEntityRun (SIDE_ or FULL_REFRESH)
       • data pages → syncPrefetchedPlatformEntities(dispatchWebhooks: true)
       • next_cursor present → save cursor, stay IN_PROGRESS
       • absent → completeRun + job COMPLETED + lifecycle
       • error_message → job FAILED, run left incomplete (cutoff does not advance)
```

**Ready vs historical (QBD-like):** batch 0 complete → `isReady` + side refresh +
INITIAL_UPDATE. All batches complete → `isHistoricalReady`.

The incremental cutoff comes from `getEntityLastCompleted`
(`src/genericWorker/utils/lastupdated/getEntityLastCompleted.ts`): the
`startedAt` of the **last completed `SIDE_REFRESH` `RefreshEntityRun`** for that
entity. For Sage 50 that run is now opened on the first ingest page and completed
only on the final page — same meaning as cloud platforms.

## Entity ordering (priority + stage gate)

| Priority | Entities |
|---|---|
| 0 | CREATE / UPDATE / DELETE / ID_FETCH |
| 10 | COMPANY_INFO |
| 20 | ACCOUNTS |
| 30 | CUSTOMERS, VENDORS |
| 40 | JOURNAL_ENTRIES, INVOICES, BILLS, EXPENSES, TRANSACTIONS (historical batch 0) |
| 50 | same temporal entities, historical batch 1 (deeper) |

`selectNextJob` orders by `(priority ASC, updatedAt ASC)` and will not start an
enqueued job while any unfinished job has a **lower** priority. Multi-page jobs
still round-robin *within* a stage (cursor saves bump `updatedAt`).

## Connector paging / caches

Under `%ProgramData%\Rutter\Sage50Connector\cache\`:

| Cache | Purpose |
|---|---|
| `JobFetchCache` (in-process) | Full filtered list for an open `job_id` — one Sage `Load()` per job, not per page |
| `{jobId}/{entity}.ids` | Disk id list for restart resilience / progress |

The connector always reports every row in the page. Unchanged-row dedupe is left
to the server upsert (content hash / `platform_id` match). No client-side hash
cache.

Optional `start_date` / `end_date` (yyyy-MM-dd) filter transaction bodies by
document `Date` after load (fiscal / outer range windowing). **TRANSACTIONS
(GL) do not use `start_date`/`end_date` on recurring SIDE_REFRESH** — the COM
exporter always dumps the full ledger and the server upsert dedupes unchanged
rows. Historical initial batches may partition by transaction date.

**Delete monitoring is not supported.** The Sage Peachtree SDK has no deleted-
entity query (verified against 2026.1 SDK docs on the lab VM). Hard deletes would
only be detectable by full-inventory id set-diff across syncs; we do not
implement that. Explicit vendor `DELETE` write jobs still work.

## Initial-sync observability

- Link `POST /sage-50/link-verify` returns `initial_sync: { completed_entities, pending_entities, total, completed }`.
- When every initial entity job is COMPLETED: `isReady`, `isHistoricalReady`, side-refresh enqueue, and **`INITIAL_UPDATE` webhook** (`createInitialUpdateWebhooksJob`).
- Tray `SyncStatus` shows records done/total from the in-memory job list.

## Still imperfect / follow-ups

- Sage `LastSavedAt` still often null → connector includes untimestamped rows (safe over-fetch); server upsert dedupes unchanged payloads.
- Accounts still ignore `updated_at` on the Sage side.
- Per-accounting-period multi-job historical batches (N windows per entity) not yet enqueued from Rutter — outer fiscal range params are supported when set.
- Scorecard still scores enqueue-only fetchers poorly unless exempted.

## How to verify

```sql
select entity, type, started_at, completed_at
from refresh_entity_runs where item_id = '<itemId>'
order by started_at desc limit 20;

select platform_entity, status, priority, parameters, updated_at
from desktop_platform_jobs where item_id = '<itemId>'
order by priority, updated_at;

select platform_entity, count(*), count(platform_id)
from platform_entities where item_id = '<itemId>' group by 1;
```
