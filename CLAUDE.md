# Sage 50 Connector Runbook

The connector is a Windows desktop agent that reverse-polls Rutter for jobs and
services them against a local Sage 50 install through the Sage Peachtree SDK.
Rutter never connects inbound; the connector always dials out.

## What runs where

- Develop and commit C# changes on macOS if preferred.
- Build and run the connector on the Windows VM: it requires Sage 50 and the Sage Peachtree SDK.
- The SDK is installed on the VM at `C:\Program Files (x86)\Sage\Peachtree\API`, including `Sage.Peachtree.API.dll`.
- Build the `Sage50Connector.sln` solution as **x86** and target .NET Framework 4.8. `Any CPU` is not appropriate for the Sage SDK.

## Build and run on the VM

1. Pull the desired Git commit on the VM.
2. Open `Sage50Connector.sln` in Visual Studio 2022 (not merely the repository folder).
3. Select `Debug` and `x86` in the toolbar.
4. Use **Build -> Rebuild Solution** after source changes. Changing only the external config does not require a rebuild.
5. Run the `Sage50Connector` startup project.

Sage 50 does **not** need to be open — see "Sage 50 does not need to be running".

The one-shot executable polls for jobs and exits after
`MaxConsecutivePollFailures` failed polls. `Sage50ConnectorService` is the
long-running Windows Service wrapper; it calls the connector at startup and then
once per minute.

### Scripted build/run from macOS

`az vm run-command` works for git, build, and file checks, but **not** for
running the connector — see "The connector must run as the user who approved
Sage". Write a CRLF PowerShell file and pass it with `--scripts @file`; quoting
inline is fragile.

```bash
az vm run-command invoke -g MICROSOFTGREATPLAINS_GROUP -n microsoftgreatplains \
  --command-id RunPowerShellScript --scripts @/tmp/cmd.ps1 -o tsv --query "value[0].message"
```

Only one run-command executes at a time; a second returns `Conflict`, and a
timed-out extension can wedge the channel for several minutes.

## Sage authorization — the thing that will waste your afternoon

On first access the Sage API returns `Authorization result = Pending`, which the
connector surfaces as
`Error: Authorization result = Pending. Company is disconnected.`

1. Run the connector once to register the access request.
2. In Sage 50: **File → Close Company**, then reopen the company as a Sage administrator.
3. Sage shows the third-party access request during company open.
4. Choose **Always Allow Access**.
5. Run the connector again.

The prompt appears **only when the company is opened**. If the company is
already open, nothing happens no matter how many times the connector asks — this
is the single most common way to lose an hour here.

The dialog shows `ADDIN_TITLE` from `Properties/Resources.resx`. It said "Sage
Peachtree SDK" (the SDK sample's name) until Aug 2026; it is now "Rutter Sage 50
Connector".

### What revokes the grant

Confirmed by repeated observation on the VM:

| Action | Grant survives? |
|---|---|
| Restarting the connector process (same binary) | Yes |
| Killing the connector with `Stop-Process -Force` | Yes |
| Closing/force-killing Sage 50 itself | Yes |
| **Rebuilding the connector** | **No — re-approval required** |

Every rebuild produced `Pending` again and needed another **Always Allow
Access**. `AssemblyVersion` is pinned to `1.0.0.0`, so it is not version
auto-increment. Cause not fully isolated; suspect Sage keys the grant to the
executable's identity or hash.

**This is an unresolved product risk**: if it holds for customers, every
connector upgrade re-prompts every user. Investigate before shipping updates.
When iterating locally, avoid needless rebuilds — restarting the existing binary
is free.

### Automating the approval — what works

For **development**, the approval can be clicked without a human, but only from
*outside* the VM. What fails, and what works:

| Approach | Result |
|---|---|
| UI Automation (`System.Windows.Automation`) from PowerShell in the interactive session | **No.** Sage exposes only static text panes (`WindowsForms10.STATIC.app...`); no invokable buttons for `InvokePattern`. |
| Screenshot + coordinate clicking from inside the VM | **No.** Windows Defender blocks screen-capture-plus-base64 as `ScriptContainedMaliciousContent`. Do not work around the AV. |
| Remote agent talking to the Azure API | **No.** Sandboxed agents have no outbound access to `management.azure.com`. |
| **Computer use driving the Mac's Remote Desktop app** | **Yes.** This is the one that works. |

The working recipe (used repeatedly on 2026-08-02) is to keep an RDP session
open on the Mac and hand the GUI steps to an agent with computer use — e.g.
`/codex:rescue`, told to operate *only* inside the existing remote-desktop
session:

1. File → Close Company (the real menu command, not killing `Peachw.exe`).
2. Reopen "Bellwether Garden Supply" from the welcome screen.
3. When the Third Party Application Access dialog appears for
   `Sage50Connector.exe`, confirm **Always allow access** and click **OK**.

The agent will typically stop and ask before clicking OK, since it is granting
persistent data access; answer it and it finishes.

None of this changes the **product** story: a customer still has to click this
themselves. It is a hard install step and it blocks silent auto-update.

Note that killing `Peachw.exe` and relaunching it brings Sage back to the
**welcome screen with no company open**, which is *not* equivalent to
File → Close Company followed by reopening. No company open means no access
prompt, no matter how many requests are pending.

### The connector must run as the user who approved Sage

Sage records the grant per Windows user.

| Launch context | Result |
|---|---|
| `az vm run-command` (SYSTEM, session 0) | `Pending` forever |
| Interactive user who approved | Works |

This is why the MSI no longer installs the service as `LocalSystem`. To run the
connector remotely as the interactive user, register a scheduled task with an
interactive-token principal — no password needed:

```powershell
$action = New-ScheduledTaskAction -Execute 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe'
$principal = New-ScheduledTaskPrincipal -UserId 'rutteradmin' -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName RutterSageLive -Action $action -Principal $principal
Start-ScheduledTask -TaskName RutterSageLive
```

`schtasks /create /IT /NP` does **not** work — those switches are mutually
exclusive.

### Sage 50 does not need to be running

Verified 2026-08-02: with `Peachw.exe` fully closed, a full sync succeeded (156
accounts, 35 customers, 29 vendors). The SDK opens the company itself via the
`Sage.Peachtree.Network.Connector` Windows service, which autostarts.

This matters: the background-service deployment model is viable, and customers
do not have to leave Sage 50 open.

### Sample vs real companies — untested risk

**Every test to date has used `Bellwether Garden Supply`, which is a Sage
*sample* company.** Per the SDK, an *empty* `ApplicationIdentifier` already
grants sample-company access, so none of this proves Rutter's licensed
identifier works against a real customer company. Test against a non-sample
company before any customer deployment. If the identifier is wrong, nothing else
matters.

## Runtime configuration and logs

Config path (preferred):

`%ProgramData%\Rutter\Sage50Connector\sage50Config.json`

Legacy fallback, still honored: `C:\Users\Default\Documents\sage50Config.json`

```json
{
  "CompanyName": "Bellwether Garden Supply",
  "AccessKey": "<credential inbound access token, iat_...>",
  "ConnectionId": "<Rutter item ID>",
  "ApiBaseUrl": "https://production.rutterapi.com"
}
```

`ApiBaseUrl` is optional and defaults to production; point it at an ngrok URL for
local end-to-end work.

Never commit this file or paste its credential values into tickets, chat, or logs.

`ConnectorConfig.Load` parses this with `JObject.Parse`. Do not revert to
hand-written string slicing: slicing includes the quotes and causes auth failures.

Logs: `%ProgramData%\Rutter\Sage50Connector\log.txt` (legacy: Documents\log.txt).
The connector logs config path, company name, connection ID and access-token
*length* — never the token itself.

`CompanyName` must match the Sage company exactly. A mismatch yields
`Error: There are no companies with that name`, which is a different failure from
`Pending`.

## Rutter authentication contract

- `ConnectionId`: the Rutter item ID.
- `AccessKey`: the item's credential **inbound access token** (`iat_…`).

Do **not** use the item's regular `access_token`, public token, organization
client ID, or client secret. A 401 `Invalid access token/connectionId pair` means
the pair is wrong, the credential has no `inbound_access_token`, or the exe is
reading a different config/build.

Provisioning without hand-editing JSON:

```
Sage50Connector.exe --setup "<CompanyName>" <OrgId> [ApiBaseUrl]
```

This calls `POST {ApiBaseUrl}/sage-50/save-id`, which mints the item, credential
and inbound token and returns the config to write. **That route currently exists
only on the `paperclip/RUT-29-re-setup-the-sage-50-integration` branch of
rutter-backend** — it must ship to production before `--setup` works for
customers.

## The ingest protocol

The connector polls `POST {ApiBaseUrl}/versioned/ingest` with
`X-Rutter-Version: 2024-04-30` and `Authorization: Bearer <iat_…>`, body
`{"connection":{"id":"<itemId>"}}`. Rutter replies with the next job:

- `LIST_FETCH` — Accounts, Vendors, Customers
- `CREATE` — Vendors
- `NOOP` — nothing to do; the connector sleeps 5 minutes

The connector reports a result by POSTing back to the same endpoint with
`job_id`, `type`, `platform_entity`, `parameters`, and either `data` or
`error_message`.

### Paging

`parameters.limit` and `parameters.cursor` are set by Rutter. The connector
returns at most `limit` records and sets `next_cursor` when more remain; Rutter
marks the job `completed` only when a response arrives **without** `next_cursor`,
and re-serves the same job with `parameters.cursor` advanced otherwise.

`next_cursor` must be *omitted*, not null, on the final page — Rutter types it as
an optional string, which rejects an explicit `null`. **The same applies to
`parameters.cursor`**, which the connector echoes back on every report; it
carries `[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]` for
exactly this reason. Send `"cursor": null` on the first page and every job dies
with a 500 (`path: ["parameters","cursor"]`), nothing persists, and the cursor
never advances.

Paging keys on the record id, not an offset: Sage is a live database and an
offset silently skips records when something is inserted earlier in the order
between pages.

Verified 2026-08-02: a 156-account fetch with `limit=50` pages correctly, the
cursor advances, the final page arrives without `next_cursor`, the job moves to
`completed`, and all 156 rows persist with populated `platform_id`s.

### Leaked Sage sessions exhaust the license

The connector now releases its session on every exit path it can observe —
normal return, Ctrl+C, unhandled exception, and the service's `OnStop`. A **hard
kill** (`Stop-Process -Force`, TerminateProcess, power loss) runs no handler and
still leaks a seat; nothing in-process can fix that. After enough leaked
sessions Sage answers:

```
License is currently unavailable. You have reached the maximum number of
connections, please try again later
```

Restarting `Sage 50 Connect Service <year>` releases the orphans
(`.claude/skills/sage50-iterate/scripts/reset-sage-sessions.ps1`).

Note that `Dispose()` and `CloseCompany()` used to reach the session through the
`PeachtreeSession` *property*, whose getter creates **and begins** a session on
demand — so releasing through it opened the very connection it was meant to give
back. Both read `m_peachtreeSession` directly now; keep it that way.

### Kill stale connector processes before re-testing

The connector caches its `PeachtreeSession`. An instance that started before an
approval keeps failing after it, and — because it wakes every 5 minutes on
`NOOP` — it will grab freshly enqueued jobs and fail them before a newly started
instance can. Symptom: jobs go `failed` seconds before your run starts, and your
run only sees `NOOP`. Always `Stop-Process` every `Sage50Connector` first.

### Job lifecycle gotcha

Rutter's `selectNextJob` falls back to re-serving `inProgressJobs[0]` when
nothing is enqueued. A job that never reaches a terminal state is therefore
handed back on **every** poll. Combined with a connector that retried instantly,
this produced 330 poll/report cycles in one minute. Two defenses now exist:
`PollDelay` between cycles in the connector, and Rutter marking the job `failed`
when `error_message` arrives.

## Serialization: the rule that silently ate 155 of 156 records

Build the payload as an anonymous object and serialize it **directly** with
`CamelCasePropertyNamesContractResolver`.

```csharp
// Right
JsonConvert.SerializeObject(responseObject, new JsonSerializerSettings {
    ContractResolver = new CamelCasePropertyNamesContractResolver()
});

// Wrong — the resolver is silently ignored
var jsonObject = JObject.FromObject(responseObject);
JsonConvert.SerializeObject(jsonObject, settingsWithResolver);
```

`JObject.FromObject` materialises property names immediately, and a
`ContractResolver` has no effect when serializing an already-built `JObject`.
Records then go out with Sage's casing (`ID`, `Name`, `IsInactive`). Rutter
extracts each record's primary key from `$.id`, which matches nothing, so every
record persists with a **null `platform_id`** and the whole page upserts onto one
row. A 156-account fetch landed as 1 row, with no error anywhere.

**Verification:** after a sync, check that `platform_id` is populated:

```sql
select platform_entity, count(*), count(platform_id)
from platform_entities where item_id = '<itemId>' group by 1;
```

Counts that disagree mean the casing is wrong again.

## Incremental sync and Sage's LastSavedAt

Sage leaves `LastSavedAt` unset on records untouched since the company was
created. Against Bellwether: **29 of 29 vendors and 34 of 35 customers had no
`LastSavedAt`.**

`LastSavedAt` is a `DateTime?`, and in C# a null nullable compares `false`
against any bound — so `LastSavedAt >= cutoff` dropped every untimestamped
record, permanently, at any cutoff. Vendors synced 0 rows and looked "correct".

`Sage50Repository.ChangedSince` now treats an absent timestamp as "include" and
`LogFilterOutcome` logs `returned / no-timestamp / passed` counts every fetch, so
this is visible rather than silent.

Consequence: those records are re-sent on every sync, since Sage never gives them
a timestamp. Fine at Bellwether's size; a real cursor strategy is needed at scale.

`GetAccounts` ignores `updated_at` entirely and always returns everything — which
is why accounts were the only entity that looked healthy.

## Local end-to-end testing

1. Run rutter-backend from the branch with the Sage 50 routes
   (`paperclip/RUT-29-re-setup-the-sage-50-integration`, `PORT=4007`), not main.
2. `ngrok http --hostname=eyrutter.ngrok.io 4007` — the reserved hostname keeps
   the connector config stable across restarts.
3. Point `ApiBaseUrl` at the ngrok URL.
4. Enqueue jobs. The cursor matters:
   ```
   yarn rutter refresh <itemId> -e ACCOUNTS -t side -f --after 1900-01-01
   ```
   Without `--after`, the cursor defaults to *now* and vendors/customers
   legitimately return nothing, which is easy to misread as a bug.
5. Run the connector as the interactive user (see above) and watch both logs.

Verify in the DB — never print credential values:

```sql
select platform_entity, status from desktop_platform_jobs where item_id = '<itemId>';
select platform_entity, count(*), count(platform_id) from platform_entities
  where item_id = '<itemId>' group by 1;
```

Bouncing the backend kills the connector's poll; with the retry/backoff it now
survives a short restart, but a long outage still exits the process by design.

## Known gaps before customer deployment

- **Untested against a real (non-sample) Sage company.** Highest risk; see above.
- **Rebuild revokes Sage authorization.** Cause unknown; blocks a clean upgrade story.
- **`/sage-50/save-id` is not in production**, so `--setup` and the MSI's
  `WriteSageConfigJson` custom action cannot provision a customer yet.
- **MSI is unsigned** — SmartScreen will block it.
- **Service account must be supplied at install** (`SERVICEACCOUNT`,
  `SERVICEPASSWORD`); the service installs `Start="demand"` and will not sync
  under the `LocalSystem` default. Installing with a real service account has
  not been exercised — only the LocalSystem default path was tested.
- **No auto-update mechanism.**
- **Entity coverage is accounts/vendors/customers read plus CREATE vendor.** No
  invoices, bills, payments, or journal entries.
- **One install serves one company** — `CompanyName` is a single value.
- Inbound access token is stored plaintext in `%ProgramData%` and logged
  plaintext by the backend's ingest middleware (deliberately deferred).

## Installing the MSI

```
msiexec /i RutterSage50ConnectorSetup.msi /qn ^
  COMPANYNAME="Bellwether Garden Supply" ACCESSKEY=iat_... CONNECTIONID=<itemId> ^
  SERVICEACCOUNT="MACHINE\SageUser" SERVICEPASSWORD="..." ^
  /l*v install.log
```

`COMPANYNAME`/`ACCESSKEY`/`CONNECTIONID` are written to `sage50Config.json` by
the `WriteSageConfigJson` custom action; omit them and provision later with
`--setup`. Omit `SERVICEACCOUNT` and the service installs as `LocalSystem`,
which cannot authorize against Sage.

The service is **not** started by the installer. Grant Sage access as the
service account first, then `sc start Sage50ConnectorService`.

**A successful build proves nothing about the installer.** The custom action
failed to load for a while and every build was green — only `msiexec` surfaced
it. Test with an actual install, and read `/l*v` output; a bare 1603 hides the
real error further up the log.

## Connector-side pitfalls already fixed — do not regress

- `Service1.cs`: use `System.Timers.Timer` (ambiguous with `System.Threading.Timer`).
- WiX `Product.wxs`: package shared DLLs from `$(var.Sage50Connector.TargetDir)`.
- Custom actions are packed by a post-build `MakeSfxCA` step into `*.CA.dll`.
- A failed poll must not end the process outright — retry with backoff.
- Harmless warnings: `CS0252` in `Sage50Repository.cs`, NuGet `NU1903` for
  `System.Text.Json` 7.0.3.

## Unexplained

With the pre-fix build, 330 error reports were logged as "Successfully posted to
Rutter" while the backend returned **500** to every one (confirmed by replaying
the exact payload). `PostToRutterAsync` checks `IsSuccessStatusCode` correctly and
the binary matched HEAD. The fix made those posts genuinely succeed, so it is
masked now — but connector logs may be capable of reporting success on failure.
Worth chasing.
