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
7. When Rutter requests accounting transactions, the connector checks the
   separate Sage transaction/COM access. Approve that company-data request too.
   If Sage does not display it immediately, close and reopen the company once
   more, then click **Check access** in the connector.
8. Keep Sage 50 open while General Ledger transaction exports run. Other SDK
   entities can sync while Sage is closed; if transaction access is not yet
   approved, that transaction job is reported with an actionable error and can
   be retried after approval.

The MSI and setup link never ask the customer for Rutter's Sage partner
credential. Rutter sends it encrypted to a one-use key created by the connector,
and Windows stores it with current-user DPAPI. Existing installs missing the
local encrypted credential recover it from Rutter using their inbound connection
token before performing the same authorization checks.

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

## Configuration

The connector reads exactly one config file:
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
Exit the tray application before uninstalling. `sage50Config.json` and
`log.txt` are left behind in `%ProgramData%\Rutter\Sage50Connector\` so a
reinstall picks the connection up again; delete that directory for a completely
clean removal.

## Known limitations / follow-ups

- Ordinary development builds are unsigned. Produce a signed release on demand
  before customer distribution.
- `--setup` creates a **new** connection per (org, company) pair. Re-running
  it for an existing company is rejected by the backend with a "duplicate"
  response; point at an existing connection by installing with the MSI
  prompts or by writing `sage50Config.json` directly.
- The build references the Sage SDK from a machine-local path; keep the
  Sage 50 SDK installed on the build machine.
