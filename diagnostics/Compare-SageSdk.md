# Compare bundled and installed Sage SDKs

## Double-click launch

Send `SageSdkDiagnostic.zip`. In Windows, right-click the ZIP and choose
**Extract All**, then open the extracted folder and double-click
`Run-SageSdkDiagnostic.cmd`. Keep the launcher and `Compare-SageSdk.ps1` together.
Exit the Rutter connector when prompted, then press a key. The window stays open
to show results or errors. Send Rutter the report folder created on the Desktop.
Do not launch directly from inside the ZIP. The launcher respects Windows script
execution policy; if blocked, have IT approve the diagnostic.

The connector folder is detected from the MSI's InstallPath registry entry,
falling back to `C:\Program Files (x86)\Rutter Sage 50 Connector`.
For a custom installation, use the PowerShell command below with the actual path.

## PowerShell launch and interpretation

Copy `Compare-SageSdk.ps1` to the customer's Windows computer. Run it as the
same interactive Windows user who normally runs Sage. Exit the Rutter connector
cleanly from its tray menu first to avoid using an extra Sage session. Sage can
remain open. No rebuild, installation, or company approval is needed.

From Windows PowerShell, supply the directory containing the customer's
`Sage50Connector.exe` and bundled Sage DLLs (use the shortcut's Open file location):

```powershell
.\Compare-SageSdk.ps1 -ConnectorDirectory 'C:\actual\connector\installation'
```

The installed SDK defaults to
`C:\Program Files (x86)\Sage\Peachtree\API`. If Sage is installed elsewhere:

```powershell
.\Compare-SageSdk.ps1 -ConnectorDirectory 'C:\actual\connector\installation' -InstalledApiDirectory 'D:\actual\Sage\Peachtree\API'
```

Follow the organization's normal script execution policy if Windows blocks the
script. No administrator privileges are normally needed. The script writes a
timestamped report folder on the Desktop with a summary and stdout/stderr for
each probe, plus `inventory.txt`. Share those six text files; do not send
`sage50Config.json`. Version 2 includes company names, GUIDs, database/server
names, and company folder paths. It omits accounting records and credentials.

`inventory.txt` records connector executable/API versions and hashes, Sage DLL
paths, known Actian runtime locations, Sage INI file paths, relevant PATH entries,
running Sage/connector executable paths, and Sage/Actian service status. Connector
configuration, logs, credential files, and company DAT/UDL/approval files are
reported by file metadata only; their contents are never collected. Missing or
inaccessible files are labeled, not treated as proof of a damaged installation.
The file search is limited to known directories, not the whole disk.

Each probe starts a fresh 32-bit Windows PowerShell process, loads the selected
resolver, initializes it, loads the selected API, begins a sample-only session,
calls `CompanyList()`, and ends/disposes the session. It records file hashes,
versions, actual loaded assembly/module paths, and the exception chain with the
failing stage. It does not change PATH, copy DLLs, read connector configuration,
request Sage access, open a company, or contact Rutter. A 90-second timeout stops
only the diagnostic child; forced termination may leave a Sage session seat
until Sage reclaims it. Avoid repeated retries after a timeout.

| Bundled | Installed | Interpretation / next step |
|---|---|---|
| FAIL | PASS | Strong evidence to test loading the installed SDK in our connector; compare paths, versions, and failure stage. |
| FAIL | FAIL | Compare errors. The same native-DLL failure points toward shared native discovery/dependencies; capture the actual connector with Process Monitor. Missing API files or licensing errors are different failures. |
| PASS | PASS | This probe did not reproduce the connector problem. Capture the actual connector's loading context with Process Monitor. |
| PASS | FAIL | Installed API directory/version may be wrong or incomplete; do not switch the connector to it. |

This isolates SDK discovery, not full connector behavior. PowerShell's executable
directory differs from the connector's; the two probes deliberately use the same
working directory and inherited PATH. The empty application identifier only
permits sample-company access and is not evidence of real-company licensing or
authorization. A successful probe is a reason to test a connector change, not
proof that sync or company access will succeed. No production connector behavior
is changed by this diagnostic.

If the report says **source comparison INCONCLUSIVE**, .NET redirected the
requested API to a shared assembly (for example, the Windows GAC). PASS still
means SDK initialization and enumeration worked, but does not prove that the
two different DLL sources were exercised. Inspect the actual loaded paths.

Validated over SSH on the Azure Sage 50 lab VM on 2026-09-17: both source probes
passed enumeration using Sage API 2026.1.0.207; both were redirected to the GAC.
The test also caught and fixed differing 32/64-bit execution policies: workers
now inherit the caller's effective policy without changing machine settings or
overriding Group Policy.

Version 2 validated on the same Windows lab on 2026-09-22: both probes passed,
listed all six companies with names/GUIDs/database names/server names/paths, and
recorded the expected company files and installed runtime metadata. This still
does not test the actual connector's licensed session or company-open path.
