# Initial and incremental sync on a reverse-polled desktop platform

How a Sage 50 sync is scheduled today, the four ways the current design is wrong
about it, and the model to move to. Read `CLAUDE.md` first for the ingest
protocol, and the Rutter-side platform doc
(`rutter-backend: src/platformization/platforms/sage_50/CLAUDE.md`) for how jobs
are generated.

Everything under "What happens today" was read out of the code, not assumed;
file references are given so the next person can check rather than trust.

## What happens today

```
refresh scheduler (side or full)
  └─ sage_50 sync/config.ts → buildSage50ListFetchStrategy.fetcher()
       • after = fetchMetadata.after.value
       • INSERT desktop_platform_jobs (ENQUEUED, parameters={updated_at, limit:50})
       • return { data: [], nextCursor: null }        ← the refresh "succeeded"
                                                         here, with no data
  ⋮  minutes later
connector poll → selectNextJob → job served → Sage read → report
  └─ handleIngestListFetchJob
       • persists the page
       • next_cursor present → save cursor, stay IN_PROGRESS
       • absent            → job COMPLETED
```

The incremental cutoff comes from `getEntityLastCompleted`
(`src/genericWorker/utils/lastupdated/getEntityLastCompleted.ts`): the
`startedAt` of the **last completed `SIDE_REFRESH` `RefreshEntityRun`** for that
entity. For every cloud platform that is the run that actually fetched the data.
For Sage 50 it is the run that inserted a row into a queue.

## Four things that follow from that, all of them real

**1. "Refresh complete" means "job enqueued".** The run completes when the
fetcher returns, which is before the connector has opened Sage. So Rutter
believes an item is refreshed while the migration is still running — and there is
no state anywhere that means "the initial pull has actually finished". That is
precisely the signal DualEntry asked for (initial-sync-complete /
incremental-sync webhooks). `handleIngestListFetchJob` already carries the TODOs
for it: `refreshEntityRunRepo.insertNewRun`, "Complete the job in
refreshEntityRunRepo", "Record in platform_entity_cursors".

**2. A failed job silently advances the incremental cutoff.** An
`error_message` marks the *job* `FAILED`, but the `RefreshEntityRun` completed
back at enqueue time. The next side refresh therefore asks for changes since that
run started, and anything that changed inside the failed window is never
requested again. It self-heals only for records Sage never timestamps, because
those are always included. Records Sage *does* timestamp are lost quietly, which
is the worst shape a data bug can have.

**3. Round-robin defeats entity ordering.** `selectNextJob` orders by
`updatedAt ASC`, and saving a cursor bumps `updatedAt`, so a multi-page job goes
to the back of the queue after every page. Entities interleave page by page.
Nothing can express "finish accounts first", even though account mapping gates
every transaction a migration imports.

**4. Every page rescans the entity from Sage.** `GetVendors`/`GetCustomers` call
`list.Load()` — which materialises the whole list — then `TakePage` sorts by id
in memory and takes 50. An entity of n rows costs n/50 full loads: O(n²/50) work
and n/50 × full memory churn. The connector is 32-bit by necessity (the Sage SDK
is x86), so ~2 GB of address space is the ceiling a large `Load()` runs into
first. Bellwether hides all of this: 156 accounts is four loads of a small list.

The transaction reads make this the first thing to fix rather than a
someday item, for two compounding reasons:

- Transactions are the high-cardinality entities. A company with 40k invoices
  pages 800 times, and each page loads all 40k invoices *and* walks their lines.
- Each page also rebuilds `ReferenceIndex`, which loads the account, customer and
  vendor lists again. So a page costs one transaction load plus up to three more.

Neither is a reason to hold the reads back — correctness first, and Bellwether
will not notice — but do not point this at a real ledger before the id-list cache
below exists.

And the part already documented in `CLAUDE.md`: incremental filtering rests on
`LastSavedAt`, which Sage leaves unset on records untouched since the company was
created (29 of 29 vendors, 34 of 35 customers at Bellwether), while accounts
ignore `updated_at` entirely. So today "incremental" means "full re-send, deduped
on the primary key".

## The model to move to

Two phases with explicit, observable state — not a cadence.

### Initial sync: one ordered pass, full history

Order matters because the importer's mapping depends on it:

```
COMPANY_INFO → ACCOUNTS → CUSTOMERS, VENDORS → transactions (per fiscal period)
```

Cheapest way to get it: a `priority` int on `desktop_platform_jobs`, and
`selectNextJob` ordering by `(priority ASC, updatedAt ASC)`. Same round-robin
inside a stage, no round-robin across stages. The alternative — don't enqueue
stage N+1 until stage N is `COMPLETED` — needs a scheduler that remembers where
it was, which is more machinery for the same result.

Completion becomes observable and cheap to define: the initial sync is done when
every job for the item has reached `COMPLETED`. That is what to hang the webhook
on, and what the tray UI should show as progress instead of a per-entity count.

### Incremental: cutoff from the report, not the enqueue

The fix is one change in `handleIngestListFetchJob`, and it removes defects 1 and
2 together:

- first page of a job arrives → open the `RefreshEntityRun`
- page arrives without `next_cursor` → complete it
- `error_message` arrives → mark it failed, so the cutoff does not advance

`getEntityLastCompleted` then means the same thing for Sage 50 as for every cloud
platform, and a failed job is retried over the window it actually missed.

Keep the connector's "no timestamp ⇒ include" rule. It over-fetches, which is the
safe direction, and Sage gives no alternative.

Volume control, when transaction entities land: have the connector keep a
per-entity `id → hash` cache under `%ProgramData%` and omit rows whose hash is
unchanged. That turns a 100k-row re-send into a diff. It is purely a
bandwidth/CPU saving on the customer's machine — Rutter's continuous-backfill
hashing already suppresses the no-op updates server-side — so it is worth doing
only once the row counts justify it, not now.

### Paging that does not rescan

Cache the ordered id list per `(job_id, entity)` for the life of the job, and
load only the ids on the requested page. Keyed on `job_id` because a job
re-served after a connector restart should just rebuild the list. This is what
turns O(n²/50) back into O(n), and it needs no protocol change: Rutter is already
sending `cursor` and `limit`.

### Windowing for transactions

Sage exposes `Company.Defaults.GeneralLedger.AccountingPeriods` as `(From, To)`
pairs, and `COMPANY_INFO` now reports the outer range as
`fiscalYearStart`/`fiscalYearEnd`. Fetch transactions one fiscal period at a time
rather than as one unbounded list: bounded memory in a 32-bit process, resumable
after a crash, and it maps onto Rutter's existing `windowedList` strategy shape
instead of needing a new one.

### Two decisions that are not the connector's to make

**Refresh cadence.** `SAGE_50` is commented out of `ON_PREM_PLATFORMS` in
`src/types.ts`, so it is side-refreshed on Rutter's schedule like a cloud
platform. One machine, one Sage licence seat, one job at a time: the scheduler
can enqueue faster than the customer's machine drains the queue. The per-entity
`ENQUEUED` dedupe in `existingListFetchJobEnqueued` bounds duplicates per entity
and nothing bounds the queue overall. Either move Sage 50 into
`ON_PREM_PLATFORMS` (syncs only when something asks — but DualEntry bought a
"continual connection", so that needs an explicit trigger), or keep the schedule
and skip enqueueing while the item has any unfinished job.

**Scorecard.** `scorecards/SAGE_50.txt` scores 6/18 with "Fetch response is not
empty: 0 / 3" for all three entities, because an enqueue-only fetcher returns no
data by construction. The harness assumes inline fetch. Desktop strategies need
either an exemption or to be scored on the delivered job, otherwise the scorecard
stays permanently red and stops meaning anything.

## How to verify any of this

Per entity, after a run:

```sql
select platform_entity, count(*), count(platform_id)
from platform_entities where item_id = '<itemId>' group by 1;

select platform_entity, type, status, parameters, updated_at
from desktop_platform_jobs where item_id = '<itemId>' order by updated_at;
```

Counts that disagree mean the payload casing regressed (see `CLAUDE.md`). A job
stuck `IN_PROGRESS` with an unchanging `updated_at` means the connector is not
reporting — which Rutter will re-serve forever.
