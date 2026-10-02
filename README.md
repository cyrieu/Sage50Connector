# Rutter Sage 50 Connector

The Sage 50 (US / Peachtree) desktop connector for Rutter. Sage 50 has no
cloud API, so this Windows application runs on the machine where Sage 50 is
installed and relays data between the local Sage 50 SDK
(`Sage.Peachtree.API`, x86) and the Rutter backend over HTTPS.

It is a reverse-poll client: the tray application polls
`POST {ApiBaseUrl}/versioned/ingest` (default
`https://production.rutterapi.com`), picks up queued jobs (LIST_FETCH /
CREATE), runs them against the open Sage 50 company, and posts results back.

## Projects

| Project | Output | Purpose |
|---|---|---|
| `Sage50Connector` | `Sage50Connector.exe` | Interactive tray connector and provisioning tool (`--setup`). |
| `Sage50ConnectorSetupCustomActions` | DLL | MSI custom action that writes `sage50Config.json` at install time. |
| `Sage50ConnectorSetup` (WiX) | `RutterSage50ConnectorSetup.msi` | Installer. |

## Prerequisites (build machine)

- Windows with .NET Framework 4.8 developer pack + Visual Studio 2019/2022 (Build Tools is enough).
- [WiX Toolset v3.11](https://github.com/wixtoolset/wix3/releases/) build tools + WiX Visual Studio extension (to load `Sage50ConnectorSetup.wixproj`).
- The Sage 50 SDK assemblies at `C:\Program Files (x86)\Sage\Peachtree\API\` (a Sage 50 install puts them there). The build references them via `HintPath`.

Build the solution in **Release**: `msbuild Sage50Connector.sln /p:Configuration=Release`.
The MSI lands at `Sage50ConnectorSetup\bin\Release\RutterSage50ConnectorSetup.msi`.

## Installing on a customer machine

Machine requirements: Windows, Sage 50 (US Edition) installed and licensed,
the target company file openable in the Sage 50 UI, .NET Framework 4.8.

1. In Rutter Link, start Sage 50 setup and download the MSI.
2. Run `RutterSage50ConnectorSetup.msi` (elevated). The installer contains no
   company-name or credential fields.
3. Return to Rutter Link and click **Choose company in connector**. Windows
   opens the installed connector through the `rutter-sage50:` setup link.
4. The connector reads Sage's own company list. Select the company from the
   dropdown; when only one is available it is selected automatically. Rutter
   stores the company's stable Sage GUID and the exact SDK-provided name.
5. Leave the connector running. It first registers the normal Sage .NET SDK
   request and shows **Approval required**; it does not consume sync jobs yet.
6. In Sage 50, sign in as an administrator, use **File → Close Company**, and
   reopen the selected company. Choose **Always Allow Access** for
   `Rutter Sage 50 Connector`.
7. Before starting the initial sync, the connector immediately checks Sage's
   separate transaction/COM access. Keep the company open; in the **Peachtree
   Software** prompt, check **Remember this setting** and click **Yes**.
8. The connector starts syncing after that first COM attempt. If the COM prompt
   is dismissed, denied, or unavailable, accounts, customers, vendors, invoices,
   and the other SDK entities continue normally; only General Ledger
   `TRANSACTIONS` are reported unavailable. Follow the displayed instructions
   and click **Check access** to retry. A successful check immediately polls
   Rutter for sync work. Keep Sage 50 open whenever General Ledger transaction
   exports run; other SDK entities can sync while it is closed.

The MSI and setup link never ask the customer for Rutter's Sage partner
credential. Rutter sends it encrypted to a one-use key created by the connector,
and Windows stores it with current-user DPAPI. Existing installs missing the
local encrypted credential recover it from Rutter using their inbound connection
token before performing the same authorization checks. A non-secret per-company
approval marker prevents ordinary restarts from requiring Sage to be open; new
connector-version approvals and transaction exports still perform live COM checks.

### Re-provisioning without the MSI prompts

From an elevated command prompt in the install directory:

```
Sage50Connector.exe --setup "<CompanyName>" <OrgId> [ApiBaseUrl]
```

- `CompanyName` — the Sage 50 company name.
- `OrgId` — the Rutter organization the connection belongs to.
- `ApiBaseUrl` — optional; defaults to `https://production.rutterapi.com`.

This calls `POST {ApiBaseUrl}/sage-50/save-id`, which creates/reuses the
Rutter connection and returns the access key + connection id. The tool writes
`sage50Config.json` itself — no hand-edited JSON.

The command-line setup path is retained for development and recovery. Normal
customer setup never asks anyone to type a Sage company name or copy an inbound
access token.

## Multiple company files

One install still syncs one Sage 50 company at a time. Every company that has
finished Rutter Link setup (or `--setup`) is remembered in
`%ProgramData%\Rutter\Sage50Connector\connections.json`. The access key in that
file is encrypted to the Windows user who set it up. `sage50Config.json` is
only the company that is syncing right now.

Open the tray window and use the **Company file** list to switch. The connector
checks two things before it changes anything:

- **Connected** — Rutter still accepts this connection's token. The check does
  not take a sync job off the queue.
- **Company file present** — Sage 50 still has that company on this computer.
  The file is not opened for the check.

If Rutter says the connection was removed or its access was revoked, the list
shows **Disconnected — reconnect via Rutter Link** and sync stays on the
current company. That row can be removed from this computer's list; nothing is
deleted in Sage or in Rutter. If Rutter cannot be reached, nothing changes and
the switch can be tried again. If Sage cannot find the company file, nothing
changes until the file is restored or re-imported.

Companies that are not selected do not sync, so their Rutter jobs wait until
you switch back. Adding a company that has never been set up is still done in
Rutter Link; the list only shows companies that already completed setup. The
first time an upgraded connector starts, the company already in
`sage50Config.json` is added to the list automatically.

Switching company does not by itself grant Sage access. A company that has
never been given **Always Allow Access** for this connector version still has
to be approved the usual way (File → Close Company, reopen, Always Allow
Access). Transaction access is remembered separately for each company.

## Configuration

The connector syncs the company in exactly one active config file:
`%ProgramData%\Rutter\Sage50Connector\sage50Config.json`

```json
{
  "CompanyName": "<Sage 50 company name>",
  "CompanyGuid": "<stable Sage company GUID>",
  "AccessKey": "<inbound access token, iat_...>",
  "ConnectionId": "<Rutter connection/item id>",
  "ApiBaseUrl": "https://production.rutterapi.com"  // optional
}
```

The legacy location `C:\Users\Default\Documents\sage50Config.json` is still
read as a fallback so existing installs keep working.

## Logs

`%ProgramData%\Rutter\Sage50Connector\log.txt` — each poll logs the fetched
job, the company it opened, and the post result. Check here first when the
connection isn't syncing.

## Uninstall

Standard MSI uninstall removes the connector and its login-start registration.
Exit the tray application before uninstalling. `sage50Config.json`,
`connections.json`, and `log.txt` are left behind in
`%ProgramData%\Rutter\Sage50Connector\` so a reinstall picks the connection up
again; delete that directory for a completely clean removal.

## Known limitations / follow-ups

- Ordinary development builds are unsigned. Produce a signed release on demand
  before customer distribution.
- `--setup` creates a **new** connection per (org, company) pair. Re-running
  it for an existing company is rejected by the backend with a "duplicate"
  response; point at an existing connection by installing with the MSI
  prompts or by writing `sage50Config.json` directly.
- The build references the Sage SDK from a machine-local path; keep the
  Sage 50 SDK installed on the build machine.
