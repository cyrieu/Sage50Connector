# Sage 50 Connector Runbook

The connector is a Windows desktop agent that reverse-polls Rutter for jobs and
services them against a local Sage 50 install through the Sage Peachtree SDK.
Rutter never connects inbound; the connector always dials out.

**The Rutter side is documented in rutter-backend**, at
`src/platformization/platforms/sage_50/CLAUDE.md` — how jobs are generated, the
end-to-end data-flow diagram, and the server-side gotchas. Read it alongside
this file; this one covers the Windows and Sage half.

## What runs where

- Develop and commit C# changes on macOS if preferred.
- Build and run the connector on the Windows VM: it requires Sage 50 and the Sage Peachtree SDK.
- The SDK is installed on the VM at `C:\Program Files (x86)\Sage\Peachtree\API`, including `Sage.Peachtree.API.dll`.
- Build the `Sage50Connector.sln` solution as **x86** and target .NET Framework 4.8. `Any CPU` is not appropriate for the Sage SDK.

## SDK reference on the VM, and how to read it off-machine

The SDK ships its own docs, installed separately from the runtime DLLs. The
runtime folder (`C:\Program Files (x86)\Sage\Peachtree\API`) holds only
`Sage.Peachtree.API.dll` and its resolver. The documentation is in
`C:\Program Files (x86)\Sage\Sage 50 2026.0 SDK`:

| File | What it is |
|---|---|
| `Sage.Peachtree.API.XML` | 1.1 MB of XML doc comments, ~3,500 members. **Machine-readable — start here.** |
| `SagePeachtreeAPIDocumentation.chm` | Full API reference, 2,744 topics / 352 classes |
| `Sage50DotNETSDK.chm` | Conceptual guide, release-by-release breaking changes, `IssuesLimitations.html` |
| `SDK poster.pdf` | Object-model poster |

Sample code (`Basic SDK App`, `Lists Example`, `Payments`, `Receipts`,
`WinFormExamples`, plus a COM set) is under the interactive user's Documents
folder on the lab VM, e.g.
`C:\Users\<WindowsUser>\Documents\Sage 50 2026.0 SDK\.NET Samples\Sample Code`.

SSH works and is the quickest way to get at all of it — the same variables
`release-via-ssh.sh` uses (`SAGE50_SSH_HOST`, `SAGE50_SSH_USER`,
`SAGE50_SSH_KEY`):

```bash
ssh -i "$SAGE50_SSH_KEY" "$SAGE50_SSH_USER@$SAGE50_SSH_HOST" 'powershell.exe -NoProfile -Command "..."'
scp -i "$SAGE50_SSH_KEY" "$SAGE50_SSH_USER@$SAGE50_SSH_HOST:C:/Program Files (x86)/Sage/Sage 50 2026.0 SDK/Sage.Peachtree.API.XML" .
```

Both CHMs extract on macOS with `7z x file.chm -oout`; the reference topics are
GUID-named HTML, so build a title index before searching. Do not commit any of it
— it is Sage's licensed documentation.

Reflecting on the DLL also works, and settles questions the docs leave vague, but
it must run under **32-bit** PowerShell: the assembly is x86, so 64-bit
`Assembly.LoadFrom` fails with "an attempt was made to load a program with an
incorrect format". Write the real script to disk and re-run it with
`$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`.

## Build and run on the VM

Build remotely from macOS with the Visual Studio Build Tools installed on the
VM. The VM builds `origin/rutter/productionize-v1`, so commit and push the code
you want to test first. Then run:

```bash
.claude/skills/sage50-iterate/scripts/vmrun.sh \
  .claude/skills/sage50-iterate/scripts/build.ps1
```

`build.ps1` stops any existing connector, fetches and resets the VM checkout to
`origin/rutter/productionize-v1`, restores NuGet packages, and rebuilds the
solution in `Release` using:

```text
C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe
```

The connector project sets `PlatformTarget` to `x86`; the executable is written
to `C:\src\Sage50Connector\bin\Release\Sage50Connector.exe`.

Do not run the executable directly through `az vm run-command`: that command
runs as `SYSTEM`, while Sage authorization belongs to the interactive Windows
user. Keep an RDP session open as the Windows user who will approve Sage, then
launch the connector from macOS with:

```bash
.claude/skills/sage50-iterate/scripts/vmrun.sh \
  .claude/skills/sage50-iterate/scripts/run.ps1
```

`run.ps1` stops stale connector processes, registers `RutterSageLive` as an
interactive-token scheduled task for the lab Windows user
(`SAGE50_WINDOWS_USER`, defaulting to the logged-on user), starts it, waits for
the connector to poll, and prints a redacted log excerpt. To stop it before
enqueuing another test batch:

```bash
.claude/skills/sage50-iterate/scripts/vmrun.sh \
  .claude/skills/sage50-iterate/scripts/stop.ps1
```

If you are already working inside the RDP desktop as the approved user, you can
instead run `C:\src\Sage50Connector\bin\Release\Sage50Connector.exe` directly
from PowerShell or File Explorer.

Development copies identify themselves as **Rutter Sage 50 Connector
(Development)** in the window title and tray menu, prefix tray status text with
`DEV:`, and log `RuntimeMode=Development` plus the executable path at startup.
The MSI records its chosen install directory and installed copies keep the plain
customer-facing name and log `RuntimeMode=Installed`. The process name remains
`Sage50Connector.exe`; inspect `ExecutablePath` when diagnosing from PowerShell.

Sage 50 does **not** need to be open — see "Sage 50 does not need to be running".

The tray executable polls for jobs and exits after
`MaxConsecutivePollFailures` failed polls. Installed copies are registered under
the machine-wide `Run` key and start in the logged-on user's interactive session.

### Azure CLI details

`az vm run-command` works for git, build, and file checks, but **not** for
running the connector — see "The connector must run as the user who approved
Sage". Write a CRLF PowerShell file and pass it with `--scripts @file`; quoting
inline is fragile.

```bash
# Prefer the wrapper, which supplies resource group / VM / subscription from env:
#   SAGE50_VM_RG, SAGE50_VM_NAME, SAGE50_VM_SUBSCRIPTION
.claude/skills/sage50-iterate/scripts/vmrun.sh /tmp/cmd.ps1

# Or call az directly with those same values:
az vm run-command invoke -g "$SAGE50_VM_RG" -n "$SAGE50_VM_NAME" \
  --subscription "$SAGE50_VM_SUBSCRIPTION" \
  --command-id RunPowerShellScript --scripts @/tmp/cmd.ps1 -o tsv --query "value[0].message"
```

Only one run-command executes at a time; a second returns `Conflict`, and a
timed-out extension can wedge the channel for several minutes.

### Signed release build

`build.ps1` produces unsigned development artifacts. Customer releases must use
the existing Rutter Azure Artifact Signing account to sign both the installed
executable and the MSI.

**Signing is opt-in, not part of normal development.** For ordinary coding,
building, testing, iteration, or VM deployment, stop after the unsigned
`build.ps1` artifacts and the requested verification. Do not run
`release-via-ssh.sh`, `release.ps1`, Jsign, or Azure Artifact Signing unless the
user explicitly asks for a **signed release** or customer distributable. A
request to rebuild, test, install, or generate an MSI is not by itself a request
to sign it.

The preferred path builds and packages on Windows but signs on macOS. It copies
the EXE and MSI over SSH, so Azure credentials never need to be stored on the
VM:

```bash
.claude/skills/sage50-iterate/scripts/release-via-ssh.sh
```

The script reads `SAGE50_SSH_HOST` / `SAGE50_SSH_USER` / `SAGE50_SSH_KEY` (and
optional `SAGE50_SIGNING_*` overrides), signs with the Mac's current Azure CLI
identity, writes immutable local artifacts under
`artifacts/sage50-release-<git-sha>/`, returns both signed files to the VM, and
verifies them with Windows Authenticode. It expects Jsign (`brew install jsign`)
and an active `az login` on the Mac. Do not commit hostnames, key paths, or
subscription names into the public tree — keep them in your shell env.

As an alternative, the complete release can run inside an interactive
PowerShell session on the VM. Install the release tools there once:

```powershell
Set-Location C:\src\Sage50Connector
.\.claude\skills\sage50-iterate\scripts\install-release-tools.ps1
```

Open a new PowerShell window, authenticate, and create the release:

```powershell
az login
.\.claude\skills\sage50-iterate\scripts\release.ps1
```

`release.ps1` takes the Azure Artifact Signing subscription, endpoint, account,
and certificate profile as parameters (or `SAGE50_SIGNING_*` env vars). Defaults
are empty so nothing internal is hard-coded in the public scripts.

Both paths use the same fixed order: rebuild, sign `Sage50Connector.exe`, package
the MSI without rebuilding project references, **sign the MSI**, verify both
Authenticode signatures (`Get-AuthenticodeSignature` status Valid), and print
SHA-256 checksums. Do not rebuild, re-sign, or modify the artifacts afterward.
Signing changes the executable's MD5, so perform Sage approval and final testing
against that exact signed EXE.

The Link zip (`Sage 50 Connector Installer.zip`) is that signed MSI, zipped.
A zip cannot carry Authenticode; SmartScreen looks at the MSI. A Valid
signature is required but does not by itself hide “Windows protected your PC”
until Microsoft has publisher reputation. Do not publish unsigned MSIs to S3
to dodge that dialog.

## Sage authorization — the thing that will waste your afternoon

On first access the Sage API returns `Authorization result = Pending`, which the
connector surfaces as
`Error: Authorization result = Pending. Company is disconnected.`

1. Run the connector once to register the access request.
2. In Sage 50: **File → Close Company**, then reopen the company as a Sage administrator.
3. Sage shows the third-party access request during company open.
4. Choose **Always Allow Access**.
5. Run the connector again.

The tray app probes Sage access for the configured company before it polls
Rutter. It shows **Approved for this version** only when Sage returns `Granted`
for the currently running executable. A changed build shows **Approval required
for this version**, registers the request without consuming queued Rutter jobs,
and changes **Sync now** to **Check access**. After approving in Sage, click
**Check access** to retry immediately; otherwise it retries every five minutes.
The probe releases its Sage session after every attempt.

The prompt appears **only when the company is opened**. If the company is
already open, nothing happens no matter how many times the connector asks — this
is the single most common way to lose an hour here.

The dialog shows `ADDIN_TITLE` from `Properties/Resources.resx`. It said "Sage
Peachtree SDK" (the SDK sample's name) until Aug 2026; it is now "Rutter Sage 50
Connector".

### What revokes the grant — Sage keys it to the executable's MD5

Settled 2026-08-02 by reading Sage's own record. The grant lives in the
**company** directory:

```
C:\Sage\Peachtree\Company\<company>\APIACCSS.DAT
```

Printable strings in that file show one entry per approved application —
`Sage50Connector.exe`, `GNCIApp`, `1.0.0.0`, `Rutter`, the `ADDIN_TITLE`, and a
base64 16-byte value that is **`MD5(Sage50Connector.exe)`**. Verified by
computing the MD5 of the approved binary and finding that exact string in the
file.

So the identity Sage authorizes is the executable's *content*:

| Action | Grant survives? |
|---|---|
| Restarting the connector process | Yes |
| Killing it with `Stop-Process -Force` | Yes |
| Closing/force-killing Sage 50 | Yes |
| **Rebuilding with no source change** | **Yes** — the build is deterministic, so the bytes and therefore the MD5 are identical |
| **Rebuilding after a source change** | **No** — different bytes, new identity, new approval |
| Reinstalling or rolling back to a previously approved build | Yes — entries are additive, so old hashes stay authorized |

This is Sage behaving as designed, not a defect. A deterministic rebuild with
no source change produces the same executable bytes and keeps the existing
approval; a source change produces a new identity.

**Product consequence, and it is a real one:** every genuine new version has a
new hash, so **every customer must re-approve on every upgrade**. Combined with
the approval being un-automatable on their machine, silent auto-update is
impossible. Ship updates with re-approval as a documented step, and expect a
support burden proportional to release frequency. Shipping fewer, larger updates
is cheaper here than shipping continuously.

For local iteration the cost is one approval per code change, which is
unavoidable — but `/sage50-iterate` automates the clicking.

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

**Scope this to development, deliberately.** The dialog is a consent decision
about handing an application persistent access to company financial data. On
this VM it is fine to automate: it is our own test machine, Sage's built-in
sample company, our own connector, and the approval is being re-requested only
because *we* rebuilt the binary. Automating the same click on a customer's
machine is not fine, and no tooling here should grow in that direction — the
customer's approval is the point of the dialog, not an obstacle to it.

So none of this changes the **product** story: a customer still clicks this
themselves. It is a hard install step and it blocks silent auto-update.

Expect an agent to pause and ask before clicking OK. That is the right instinct;
confirm it explicitly rather than pre-emptively instructing it not to ask.

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
$windowsUser = $env:SAGE50_WINDOWS_USER
if (-not $windowsUser) { $windowsUser = $env:USERNAME }
$action = New-ScheduledTaskAction -Execute 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe'
$principal = New-ScheduledTaskPrincipal -UserId $windowsUser -LogonType Interactive -RunLevel Highest
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

### Real (non-sample) companies — verified

**Settled 2026-08-02.** Until then every test had used `Bellwether Garden
Supply`, a Sage *sample* company — and since an empty `ApplicationIdentifier`
already grants sample-company access, none of it proved Rutter's licensed
identifier worked anywhere real.

It does. A non-sample company (`Rutter Test Co`, created through Sage's New
Company wizard) authorized normally and synced **53 accounts** with correct
`platform_id`s, paging across two pages. Customers and vendors returned 0,
which is right for a company with no data yet. No licensing, registration or
"unauthorized application" error at any point.

Practical notes from doing it:

- The grant is **per company**. A new company means a fresh `Pending` and a
  fresh approval, even though the binary is unchanged — each company has its own
  `APIACCSS.DAT`.
- One connector configuration serves one company. Select it through Rutter Link:
  the connector enumerates Sage's company list and stores both the exact SDK name
  and stable company GUID. Do not ask customers to type the company name.

## Runtime configuration and logs

Config path (preferred):

`%ProgramData%\Rutter\Sage50Connector\sage50Config.json`

Legacy fallback, still honored: `C:\Users\Default\Documents\sage50Config.json`

```json
{
  "CompanyGuid": "<stable Sage company GUID>",
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

New configurations resolve the company by `CompanyGuid`, so a later company
rename does not break the connector. `CompanyName` is retained for display and
as a fallback for legacy configurations; in that fallback it must match Sage
exactly. A mismatch yields `Error: There are no companies with that name`, which
is a different failure from `Pending`.

## Rutter authentication contract

- `ConnectionId`: the Rutter item ID.
- `AccessKey`: the item's credential **inbound access token** (`iat_…`).

Do **not** use the item's regular `access_token`, public token, organization
client ID, or client secret. A 401 `Invalid access token/connectionId pair` means
the pair is wrong, the credential has no `inbound_access_token`, or the exe is
reading a different config/build.

Customer provisioning does not expose either value. Rutter Link creates a
provisional item with `POST /sage-50/setup-session`, downloads the generic MSI,
and opens `rutter-sage50://setup?...`. The installed connector enumerates
`CompanyManager.Instance.Companies`, lets the customer select an exact SDK
company, then exchanges the one-time setup token at
`POST /sage-50/complete-setup`. The response is written to the config above and
the token cannot be replayed. Link polls `/sage-50/link-verify` until the
connector has finalized the item and its initial sync has completed.

`POST /sage-50/save-id` and the `--setup` command remain available as a legacy
development/recovery path; they are not the customer onboarding flow.

## The ingest protocol

How jobs get *generated* — the refresh scheduler, `selectNextJob`, and the
data-flow diagram — lives in rutter-backend at
`src/platformization/platforms/sage_50/CLAUDE.md`. What follows is the
connector's side of the same contract.

The connector polls `POST {ApiBaseUrl}/versioned/ingest` with
`X-Rutter-Version: 2024-04-30` and `Authorization: Bearer <iat_…>`, body
`{"connection":{"id":"<itemId>"}}`. Rutter replies with the next job:

- `LIST_FETCH` — Accounts, Vendors, Customers, Company Info, Journal Entries,
  Invoices, Bills, Expenses
- `ID_FETCH` — one Vendor by `parameters.platform_id`
- `CREATE` — Vendors
- `UPDATE` — one Vendor; fields to apply come from the job's `create_body`
- `DELETE` — one Vendor by `parameters.platform_id`
- `NOOP` — nothing to do; the connector sleeps 5 minutes

The connector reports a result by POSTing back to the same endpoint with
`job_id`, `type`, `platform_entity`, `parameters`, and either `data` or
`error_message`.

A job the connector cannot service **must still be reported** with an
`error_message`. Rutter re-serves an in-progress job on every poll, so a job
that is only logged and skipped is handed back forever.

Verified end to end against Bellwether on 2026-08-03, one vendor through its
whole lifecycle: `CREATE` wrote it, `ID_FETCH` read it back, `UPDATE` renamed
it, `DELETE` removed it, and a follow-up `ID_FETCH` found nothing — confirming
the delete reached Sage and not just Rutter's copy.

Only vendors are wired for the single-record jobs. Any other entity is reported
as unsupported rather than silently ignored.

`COMPANY_INFO` is a `LIST_FETCH` like the rest, answered with a single record and
therefore always one page. Sage has no factory and no list for the company — the
fields hang off the open `Company` object, so "fetching" it is opening the
company. Sage also records no timestamp against it, so the job's `updated_at` is
ignored and every sync re-sends the row for Rutter to upsert onto itself. `$.id`
is the Sage company **GUID**, not the name, because the name is what customers
rename. The record also carries the outer accounting-period range
(`fiscalYearStart`/`fiscalYearEnd`), which is the only way to know what date range
a transaction fetch can usefully ask for.

**How initial and incremental sync actually behave here — and the four ways the
current design is wrong about it — is in [docs/sync-model.md](docs/sync-model.md).**
Read it before scaling the transaction entities: the paging strategy that works
for 156 accounts is quadratic, and Rutter currently treats "job enqueued" as
"refresh complete".

### Transactions: journal entries, invoices, bills, expenses

Written 2026-08-03, **not yet built or run** — see "Known gaps". Every property
name and type below was verified by reflecting on `Sage.Peachtree.API` 2026.1 and
cross-checked against the SDK's own samples, but the first build is still the
real check.

| Rutter entity | Sage factory | Lines |
|---|---|---|
| `JOURNAL_ENTRIES` | `GeneralJournalEntryFactory` | `GeneralJournalEntryLines` |
| `INVOICES` | `SalesInvoiceFactory` (AR) | `ApplyToSalesLines` |
| `BILLS` | `PurchaseInvoiceFactory` (AP) | `ApplyToPurchasesLines` |
| `EXPENSES` | `PaymentFactory` | `ApplyToExpenseLines` **and** `ApplyToInvoiceLines` |

Four things about Sage's model that shape this code:

**A transaction has no ID, so `$.id` is its key GUID.** Accounts and vendors have
an `ID` string; transactions do not. The human-facing number is
`ReferenceNumber`, and it is *not* unique — two journal entries can share one — so
using it as the primary key would collapse rows the way the PascalCase bug did.
The GUID is reported as `id` and `referenceNumber` is sent alongside it.

**References are GUIDs; Rutter links on ID strings.** A line points at its account
through an `EntityReference`, which exposes only `.Guid` and `.Load(company)`.
Rutter's `platform_id` for an account is `10200-00`, not a GUID, so a reference
has to be resolved before a mapper can link it. `ReferenceIndex` reads the
account, customer and vendor lists once per fetch and indexes them by key — the
same join Sage's own sample does
(`join vendor in vendorList on payment.VendorReference equals vendor.Key`).
Resolving each reference individually would be one Sage round trip per line.
Inventory items and jobs are *not* resolved, because Sage 50 has no ITEMS entity
in Rutter to link to; they are reported as `inventoryItemGuid` / `jobGuid` so it
is obvious they are not platform ids.

**Line collections are padded with unused slots.** `TransactionLine.IsUsed` is
false on Sage's empty slots, exactly like the unpopulated accounting periods that
made `fiscalYearStart` read `0001-01-01`. An unused line is not a zero-amount
line; the reads drop them.

**`Payment` is both an expense and a bill payment.** Expense lines hit GL
accounts directly; invoice lines settle bills that already exist. Both
collections are reported so a mapper can distinguish them, and
`invoiceLines[].invoiceGuid` is the same GUID the BILLS read uses as its `id`, so
a future `BILL_PAYMENTS` entity links without extra work.

Two Sage quirks worth keeping in mind here: `PurchaseInvoice.WaitingForBill` is an
**int**, not a bool (reported as Sage stores it rather than coerced), and dates are
sent as `yyyy-MM-dd` rather than timestamps because Sage stores no timezone and a
conversion downstream could move a transaction to the previous day.

`UPDATE` applies only the fields present in the body: a null means "not
supplied", not "clear this", so a partial update cannot blank a vendor's email.
`DELETE` takes its id from the job's own `parameters`, never from the response
body — the connector is reporting on work Rutter asked for, and trusting it to
name a different row would let a buggy client delete arbitrary data.

### Transactions: General Ledger (COM exporter)

`TRANSACTIONS` uses the Sage COM General Ledger Rows exporter (object 16),
not the .NET SDK. The COM path is late-bound (`Type.GetTypeFromProgID` /
`Activator.CreateInstance`) — no compile-time interop reference is added.
Every COM object is released with `Marshal.FinalReleaseComObject` in a
finally block.

The repeatable credential, approval, verification, and recovery procedure is
in [docs/com-general-ledger.md](docs/com-general-ledger.md). Read it before
recreating the lab credential or responding to a COM authorization failure.

The COM exporter always dumps the whole ledger to CSV. The connector does
**not** call `SetDateFilterValue` at all — the spike proved
GeneralLedgerRows rejects it with `0x800436FD`, and a COM failure invoked
through `Type.InvokeMember` may be wrapped in `TargetInvocationException`
rather than surfacing as a raw `COMException`. Instead the connector parses
the full CSV, drops `IncludeInGL=false` rows, applies a half-open date window
(`start_date <= posting date < end_date`) locally, and groups rows by
`JournalPostOrder` into one transaction with id `gl:{JournalPostOrder}`.
Lines are ordered by `JournalRowIndex`.

CSV parsing uses a whole-stream RFC 4180 parser (`Rfc4180CsvParser`) that
handles quoted fields containing embedded newlines and doubled quotes —
`File.ReadAllLines` + per-line splitting would corrupt multi-line quoted
descriptions.

Operating constraints (from the 2026-08-20 spike against Bellwether):

- Sage 50 must be open in the interactive user session.
- COM uses separate Sage-issued partner credentials (DPAPI-encrypted at
  `%ProgramData%\Rutter\Sage50Connector\diagnostics\sage-com-credential.xml`).
  The credential file is created by `diagnostics\Set-GeneralLedgerComCredential.ps1`,
  which uses PowerShell `Export-Clixml` — the SecureString password is stored
  as a DPAPI-encrypted hex string (not Base64) in the CLIXML `<SS>` element.
  If missing, the connector fails the TRANSACTIONS job with a precise error;
  other .NET SDK entities are unaffected.
  **The credential script is lab/dev-only.** It is not included in the MSI, not
  part of customer onboarding, and requires Sage-issued COM partner credentials
  that customers do not possess. The TRANSACTIONS implementation is therefore
  **not customer-ready** and is not zero-touch: a customer cannot set up the
  COM credential themselves, and the MSI does not ship the script.
- One posting order can include multiple journal codes (e.g., sales + COGS).
  These stay in one balanced transaction. `headerConsistent` is true only when
  all lines share one normalized date and one normalized reference; all-blank
  references is consistent (zero references on any line); mixed blank/nonblank
  references or dates is NOT consistent. Multiple journal type codes are
  expected and do NOT make a group inconsistent.
- Fail-closed on malformed GL data: the connector throws an actionable error
  (with row number) if JournalPostOrder, JournalRowIndex, GL GUID, GLAccountId,
  Date, TransactionAmount, or IncludeInGL is missing, blank, or unparseable. Unknown
  journal codes preserve `journalTypeCode` but leave numeric `journalType`
  null rather than defaulting to 0 (General).
- GL rows have no `LastSavedAt`. The backend passes temporal bounds as
  `start_date`/`end_date` (transaction-date window) for historical initial
  batches, not `updated_at`/`updated_before` (LastSavedAt window). Recurring
  SIDE_REFRESH exports the full ledger (no `start_date`/`end_date`) because
  a transaction edited today but dated before the last sync would be missed
  by a transaction-date lower bound. The server upsert dedupes unchanged rows.
- No completed-snapshot reconciliation: stale GL rows from deleted/voided
  transactions are not removed by sync.

Not yet compiled or run on the Windows VM. Platform types are hand-authored
and will need regeneration after the first real sync. The connector has no
C# test project — parsing, grouping, and date filtering methods in
`GeneralLedgerExporter` are marked `internal` so a future test harness can
exercise them, but no test project exists in the solution today.

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
2. `ngrok http 4007` (or a reserved hostname you control) so the connector can
   reach the local backend across restarts without rewriting config each time.
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

- **Every upgrade requires every customer to re-approve** in Sage, because the
  grant is keyed to the executable's MD5. Rules out silent auto-update.
- The setup-session and completion routes must deploy before the Link flow is
  usable. Production's `SAGE_50_INSTALLER_URL` must point to a signed MSI that
  contains the `rutter-sage50` protocol registration.
- Ordinary development builds are unsigned. Customer artifacts must be produced
  with `release.ps1`, which signs both the EXE and MSI through Azure Artifact
  Signing before verification.
- **No silent auto-update.** Assisted upgrade exists (tray → Check for updates →
  MSI → Sage re-approval). See `docs/updates.md`.
- **The transaction reads are written but never executed.** Journal entries,
  invoices, bills and expenses were added 2026-08-03 and have not been compiled,
  run, or seen against real data. Nothing on the Rutter side normalizes them yet:
  they have no `platform_types`, no mappers, and are not in the endpoints'
  platform allowlists, so `GET /invoices` and friends will not serve them until
  the first sync produces payloads to generate types from. The sequence is in
  rutter-backend `platforms/sage_50/CLAUDE.md` under "Adding an entity".
- **Entity coverage.** Read: accounts, customers, vendors, company info (all
  verified end to end), plus the four transaction entities above (unverified).
  TRANSACTIONS (General Ledger) uses the COM exporter — see "Accounting
  Transactions (General Ledger)" in the backend CLAUDE.md. Not yet compiled or
  run on the VM. Vendors also support ID_FETCH, CREATE, UPDATE and DELETE. No
  payments received, credit memos, inventory items, employees or jobs —
  QuickBooks Desktop covers roughly 40 entities for comparison.
- **TRANSACTIONS require Sage COM partner credentials.** The COM General Ledger
  Rows exporter uses separate Sage-issued partner credentials, stored
  DPAPI-encrypted at `%ProgramData%\Rutter\Sage50Connector\diagnostics\
  sage-com-credential.xml`. Run `Set-GeneralLedgerComCredential.ps1` as the
  interactive Sage user to create them. If missing, the connector fails the
  TRANSACTIONS job with a precise error; other .NET SDK entities are unaffected.
  **The credential script is lab/dev-only.** It is not included in the MSI, not
  part of customer onboarding, and requires Sage-issued COM partner credentials
  that customers do not possess. The TRANSACTIONS implementation is therefore
  **not customer-ready** and is not zero-touch: a customer cannot set up the
  COM credential themselves, and the MSI does not ship the script.
- **TRANSACTIONS require Sage to be open.** The COM exporter attaches to the
  company already opened by the interactive Sage user. This does not change the
  headless behavior of the existing .NET SDK endpoints.
- **No completed-snapshot reconciliation for TRANSACTIONS.** Stale GL rows from
  deleted/voided transactions are not removed by sync. See the backend CLAUDE.md
  for details.
- **One connector configuration serves one company** — its stable Sage company
  GUID and exact SDK name are selected by the customer in the connector.
- Inbound access token is stored plaintext in `%ProgramData%` and logged
  plaintext by the backend's ingest middleware (deliberately deferred).

## Installing the MSI

```
msiexec /i RutterSage50ConnectorSetup.msi /qn /l*v install.log
```

The MSI is generic: it contains no company name, connection ID, or credential.
It registers both the tray connector under the machine-wide `Run` key and the
`rutter-sage50` URL protocol. After installation, return to Rutter Link and click
**Choose company in connector**. The deep link opens the selector and provisions
the chosen company. The customer must then approve that exact executable in Sage
as the same Windows user.

**A successful build proves nothing about the installer.** Test with an actual
install, verify the custom protocol opens the installed executable, and read
`/l*v` output; a bare 1603 hides the real error further up the log.

## Connector-side pitfalls already fixed — do not regress

- WiX `Product.wxs`: package shared DLLs from `$(var.Sage50Connector.TargetDir)`.
- A failed poll must not end the process outright — retry with backoff.
- Harmless warning: `CS0252` in `Sage50Repository.cs`.

## Unexplained: the connector logs success on HTTP 500

**Reproduced 2026-08-03 with independent evidence. Cause still unknown. Do not
trust "Successfully posted to Rutter." as proof a report was accepted.**

First seen as 330 error reports logged as success while the backend returned 500
to every one. It happened again on the first `COMPANY_INFO` run, and this time
ngrok's inspector recorded what the connector actually received:

```
20 x 500  /versioned/ingest   (reports — type=LIST_FETCH, COMPANY_INFO)
20 x 200  /versioned/ingest   (polls — no type field)
```

The connector log for the same window: **39 "Successfully posted to Rutter.",
zero "Failed to post"**. Reports and polls were told apart by request body
(`type` present or absent) and by the response, so the mapping is not a guess:
every 500 was a report, and every report was logged as a success.

Ruled out:

- **Not stale code.** The exe was built from the running HEAD, and its metadata
  contains the `Failed to post to Rutter` literal — the branch exists in the
  binary.
- **Not a misread of which call fails.** Polls returned 200 with a job body
  throughout; the connector kept receiving jobs and never logged a poll failure.
- **Not the source.** `PostToRutterAsync` checks `IsSuccessStatusCode`, and
  `HttpClient.SendAsync` does not throw on 5xx, so a 500 should take the failure
  branch.

`PostToRutterAsync` now logs the numeric status on **both** branches, so the next
occurrence says whether the connector is mis-reporting or genuinely being handed
a 2xx. Keep that.

Why it matters beyond the log line: a rejected report leaves the job
non-terminal, and Rutter re-serves an in-progress job on every poll. The observed
result was a job re-fetched every ~2 seconds indefinitely, with nothing
persisting and nothing in the connector log suggesting a problem. Verify a sync
in the database, never from the connector log.
