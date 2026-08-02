---
name: sage50-iterate
description: Run one end-to-end Sage 50 connector iteration - push, rebuild on the Windows VM, re-approve Sage access via a computer-use agent, run the connector, and verify the sync landed in the local DB. Use when iterating on Sage50Connector changes, when the connector reports "Authorization result = Pending" after a rebuild, or when asked to rebuild/redeploy/retest the Sage 50 connector.
---

# Sage 50 connector: one iteration

Every rebuild revokes the connector's Sage authorization, so a code change is
not testable until someone clicks **Always Allow Access** again. That click can
be automated — by a computer-use agent driving the Mac's Remote Desktop app, not
from inside the VM. This skill is that loop.

Read `CLAUDE.md` at the repo root for the underlying facts. This is the
procedure; that is the reference.

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

`scripts/grant-status.ps1` prints whether the current exe's hash is recorded. Delegate the GUI to a computer-use agent —
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
