---
name: sage50-iterate
description: Run one end-to-end Sage 50 connector iteration - push, rebuild on the Windows VM, re-approve Sage access via a computer-use agent, run the connector, and verify the sync landed in the local DB. Also covers hand-inserting ID_FETCH/CREATE/UPDATE/DELETE desktop jobs, which nothing enqueues automatically. Use when iterating on Sage50Connector changes, when testing the vendor write path, when the connector reports "Authorization result = Pending" after a rebuild, or when asked to rebuild/redeploy/retest the Sage 50 connector.
---

# Sage 50 connector: one iteration

Every rebuild revokes the connector's Sage authorization, so a code change is
not testable until someone clicks **Always Allow Access** again. That click can
be automated — by a computer-use agent driving the Mac's Remote Desktop app, not
from inside the VM. This skill is that loop.

Read `CLAUDE.md` at the repo root for the underlying facts. This is the
procedure; that is the reference.

## Artifact policy

Default to unsigned development artifacts. For normal coding, rebuilding,
testing, iteration, and VM deployment, run `build.ps1` and the relevant test
scripts, then stop. Do not invoke `release-via-ssh.sh`, `release.ps1`, Jsign, or
Azure Artifact Signing unless the user explicitly asks for a **signed** release
or customer distributable. Producing or testing the development MSI does not by
itself authorize signing.

## Preconditions

- An RDP session to `<SAGE50_VM_HOST>` open on the Mac, logged in as the lab Windows user.
  Everything below depends on it: Sage's grant is per Windows user, and the
  approval agent drives that window.
- rutter-backend running from the branch that has the Sage 50 routes
  (`paperclip/RUT-29-re-setup-the-sage-50-integration`, `PORT=4007`), with
  `ngrok http --hostname=<your-ngrok-hostname> 4007`.
- `az` logged in.

Check the VM end with:

```bash
.claude/skills/sage50-iterate/scripts/vmrun.sh .claude/skills/sage50-iterate/scripts/status.ps1
```

## The loop

### 1. Push the change

The VM builds from `origin/rutter/productionize-v1`; it does not see your
working tree.

```bash
git add -A && git commit -m "..." && git push origin rutter/productionize-v1
```

### 2. Rebuild on the VM

```bash
.claude/skills/sage50-iterate/scripts/vmrun.sh .claude/skills/sage50-iterate/scripts/build.ps1
```

Prints `BUILD OK` plus the new HEAD, or `BUILD FAILED` with the first errors.

### 3. Stop the connector, then enqueue jobs

**Stop first.** A live instance polls every 5 minutes, so it will pick up
anything you enqueue — and if it started before the current approval it holds a
cached failed session and fails every job it touches, before your intended run
ever sees them. The symptom is jobs sitting at `failed` a few seconds before
your run, and your run reporting only NOOP. This is the single easiest way to
waste a cycle here.

```bash
.claude/skills/sage50-iterate/scripts/vmrun.sh .claude/skills/sage50-iterate/scripts/stop.ps1
```

Then enqueue, from the backend worktree. `--after` matters: without it the
cursor defaults to *now* and vendors/customers legitimately return nothing,
which reads as a bug.

```bash
cd <rutter-backend>/.paperclip/worktrees/paperclip/RUT-29-re-setup-the-sage-50-integration
DB=$(grep -E '^DATABASE_URL' .env | cut -d= -f2-)
ITEM=<REDACTED_CONNECTION_ID>
psql "$DB" -c "delete from platform_entities where item_id='$ITEM';"
psql "$DB" -c "delete from desktop_platform_jobs where item_id='$ITEM' and status in ('enqueued','in_progress');"
for E in ACCOUNTS VENDORS CUSTOMERS; do
  yarn rutter refresh $ITEM -e $E -t side -f --after 1900-01-01 >/dev/null 2>&1
done
```

Clearing `platform_entities` first is what makes the counts in step 6 mean
something.

### 4. Run the connector

```bash
.claude/skills/sage50-iterate/scripts/vmrun.sh .claude/skills/sage50-iterate/scripts/run.ps1
```

After a rebuild this reports `Authorization result = Pending. Company is
disconnected.` for every entity. That is expected, and the run is what registers
the access request Sage will present in step 5. Do not skip it.

### 5. Approve, without a human

Only if step 4 said `Pending`. **A rebuild with no source change does not need
this** — the build is deterministic and Sage keys the grant to `MD5(exe)`, so
identical bytes stay authorized. Only a real code change mints a new identity.

To check what Sage currently has granted, without guessing:

```powershell
# one entry per approved binary; the base64 value is MD5(Sage50Connector.exe)
C:\Sage\Peachtree\Company\<company>\APIACCSS.DAT
```

`scripts/grant-status.ps1` prints whether the current exe's hash is recorded —
though note that Sage writes an entry when access is *requested*, not only when
it is granted, so a recorded hash is not proof of a grant. Only a run that
fetches data proves it.

When approval is needed, delegate the GUI to a computer-use agent —
`codex:codex-rescue` via the `Agent` tool, or any agent that can drive the Mac's
Remote Desktop app:

> Use Computer Use to control the Remote Desktop app on my Mac. Work only inside
> the existing Windows remote-desktop session.
>
> 1. In Sage 50, use File → Close Company (the real menu command; do not
>    terminate Peachw.exe).
> 2. From the welcome screen, reopen the company "Bellwether Garden Supply".
> 3. When the Third Party Application Access dialog appears for
>    `Sage50Connector.exe`, ensure "Always allow access" is selected and click OK.
> 4. Confirm the dialog closed and the company is open, and report the final
>    on-screen state.
>
> Leave Sage 50 running with Bellwether Garden Supply open.

The agent will usually stop and ask before clicking OK, because it is granting
persistent access to company data. Confirm and it finishes.

Then re-enqueue (step 3 — the failed run consumed the jobs) and re-run (step 4).

Two traps here:

- **Kill stale connector processes.** `run.ps1` does this, but if you start one
  another way: an instance from before the approval caches its failed
  `PeachtreeSession` and wakes every five minutes on NOOP, so it will grab the
  jobs you just enqueued and fail them before the new instance sees them.
  Symptom: jobs go `failed` seconds before your run, and your run only sees NOOP.
- **Do not close the company afterwards.** Reopening it is what triggers the
  prompt, so leave it open or you will need another approval.

### 6. Verify

```bash
psql "$DB" -c "select platform_entity, count(*), count(platform_id),
  count(*) filter (where _normalized_at is not null) from platform_entities
  where item_id='$ITEM' group by 1 order by 1;"
psql "$DB" -c "select platform_entity, status, parameters->>'cursor'
  from desktop_platform_jobs where item_id='$ITEM' order by updated_at desc limit 3;"
```

Against Bellwether Garden Supply a healthy sync is exactly:

| Entity | Rows |
|---|---|
| ACCOUNTS | 156 |
| CUSTOMERS | 35 |
| VENDORS | 29 |

with all three jobs `completed`, and `count(*) = count(platform_id)` for each.

Read the failures, not just the totals:

- `count(platform_id)` below `count(*)`, or one row where you expected many —
  the payload went out in Sage's PascalCase and Rutter's `$.id` matched nothing.
- Vendors 0, customers 1 — the `LastSavedAt` filter is dropping records Sage
  never timestamped.
- Rows land but the job stays `in_progress` with a `cursor` set — a page was
  accepted but the next one never arrived.
- Job `failed` with nothing persisted — check the backend log for a `ZodError`.
  `parameters.cursor` and `next_cursor` must be *omitted*, never `null`.

## Testing the write jobs (ID_FETCH / CREATE / UPDATE / DELETE)

Only `LIST_FETCH` is ever enqueued for you — the refresh strategy is the sole
producer. Everything else has to be inserted by hand, so this is the recipe.

All four are **vendors only**; any other entity is reported back as unsupported.

```bash
cd <rutter-backend>/.paperclip/worktrees/paperclip/RUT-29-re-setup-the-sage-50-integration
DB=$(grep -E '^DATABASE_URL' .env | cut -d= -f2-)
ITEM=<REDACTED_CONNECTION_ID>

# clear anything in flight first, or a stale job wins the race
psql "$DB" -c "delete from desktop_platform_jobs
               where item_id='$ITEM' and status in ('enqueued','in_progress');"
```

**The body column is not shaped the same for CREATE and UPDATE.** Both live in
`create_body`, but the connector unwraps them differently — `CREATE` reads
`create_body.data`, while `UPDATE` receives `create_body` verbatim as
`update_body`. Getting this wrong fails with a null-reference, not a clear error.

```sql
-- CREATE: fields nested under "data"
insert into desktop_platform_jobs (item_id, status, platform_entity, type, create_body)
values ('<item>', 'enqueued', 'VENDORS', 'CREATE',
        '{"data":{"ID":"RUTTERTEST","Name":"Rutter Test Vendor","Email":"test@rutterapi.com"}}'::jsonb);

-- ID_FETCH: id in parameters
insert into desktop_platform_jobs (item_id, status, platform_entity, type, parameters)
values ('<item>', 'enqueued', 'VENDORS', 'ID_FETCH', '{"platform_id":"RUTTERTEST"}'::jsonb);

-- UPDATE: id in parameters, fields NOT nested
insert into desktop_platform_jobs (item_id, status, platform_entity, type, parameters, create_body)
values ('<item>', 'enqueued', 'VENDORS', 'UPDATE', '{"platform_id":"RUTTERTEST"}'::jsonb,
        '{"Name":"Rutter Test Vendor RENAMED","Email":"renamed@rutterapi.com"}'::jsonb);

-- DELETE: id in parameters
insert into desktop_platform_jobs (item_id, status, platform_entity, type, parameters)
values ('<item>', 'enqueued', 'VENDORS', 'DELETE', '{"platform_id":"RUTTERTEST"}'::jsonb);
```

Run one job at a time with `run.ps1`, then check both the job row and the data:

```sql
select type, status from desktop_platform_jobs where item_id='<item>'
  order by updated_at desc limit 3;
select platform_id, platform_data->>'name' from platform_entities
  where item_id='<item>' and platform_id='RUTTERTEST';
```

Run them in the order above as a **lifecycle test**: it exercises all four and
cleans up after itself, so nothing is left behind in the customer's books. Do
this on a vendor you created — do not update or delete a real Bellwether vendor.

Finish by re-running `ID_FETCH` after the `DELETE`. It should find nothing; that
is what proves the delete reached Sage rather than only Rutter's copy. A
`DELETE` that removes the `platform_entities` row while the vendor still exists
in Sage looks identical in the job table.

`UPDATE` only writes the fields you supply — a null means "not supplied", not
"clear this" — so to test clearing behaviour you have to change the connector,
not the payload.

## Troubleshooting

**`License is currently unavailable. You have reached the maximum number of
connections`** — leaked Sage sessions. The connector never closes its
`PeachtreeSession`, so every force-killed instance holds a seat until something
drops it. Enough iterations and Sage refuses new connections:

```bash
.claude/skills/sage50-iterate/scripts/reset-sage-sessions.ps1   # via vmrun.sh
```

That restarts `Sage 50 Connect Service <year>`, which drops the orphans. Then
re-enqueue and re-run.

**`There are no companies with that name`** — `CompanyName` in
`sage50Config.json` does not match Sage exactly. Different failure from
`Pending`.

**Everything returns NOOP and jobs are already `failed`** — a stale connector
beat you to them. See the note in step 5.

## Notes

- `vmrun.sh` converts scripts to CRLF and waits out the run-command lock. Use it
  rather than calling `az` directly.
- Never print the inbound access token. The scripts redact `iat_...`; keep that
  if you add more.
- Approving is a real grant of access to company data. It is fine here — a test
  VM, a Sage sample company, Rutter's own connector — and should not be
  automated against a customer machine.
