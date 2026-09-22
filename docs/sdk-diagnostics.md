# Diagnose setup from inside the connector

The setup picker now records its actual company-list results and SDK failures in
`%ProgramData%\Rutter\Sage50Connector\diagnostics\sdk-<UTC>-<PID>.json`.
If creating that directory is denied, it uses the corresponding directory under
`%LocalAppData%`. The ordinary connector log records the exact report path.
An empty company list or enumeration/folder-lookup failure exposes **Open
diagnostic report** in the setup window. This opens the JSON report in Notepad.

The same executable can run discovery without starting setup or polling Rutter:

```powershell
$p = Start-Process 'C:\Program Files (x86)\Rutter Sage 50 Connector\Sage50Connector.exe' -ArgumentList '--diagnose-sdk' -PassThru -Wait
$p.ExitCode
```

Exit 0 means both attempts succeeded. Exit 1 means at least one failed. Exit the
tray connector before testing. No configuration or setup token is needed; this
mode uses the same `CompanyManager` and Rutter-licensed session as the picker.
It attempts discovery twice in one process, then releases the session. It never
requests company authorization, opens a company, or sends Rutter requests.

Reports include process bitness, executable version/location, Windows identity,
working directory, inherited PATH directories, actual managed/native DLL
locations and versions, expected SDK/Actian file presence, company names and
paths, and exception chains/stacks. They do not record process arguments,
application identifiers, config contents, setup tokens, or accounting records.
One report file is maintained per process with the latest 16 snapshots.

The standalone `Compare-SageSdk.ps1` remains useful for comparing PowerShell's
environment, but it uses an empty SDK identity. Its success is not proof of
success in the connector. Compare PATH and actual DLL locations between reports.

## Lab reproduction (2026-09-22)

On the owned Azure Sage 50 lab, the x86 Release executable built successfully
with .NET Framework 4.8. Normal environment: both discovery attempts found six
companies. Removing only Actian/Pervasive/PSQL/PVSW entries from a child process's
PATH reproduced `Unable to load DLL 'w3dbav90.dll'` / `0x8007007E` during
`PeachtreeSession.Begin`. The DLL remained installed on disk. Both retries
preserved the original error; no uninitialized-session error appeared because
failed sessions are now disposed and never cached.

The standalone PowerShell diagnostic also failed under that same restricted
environment. This establishes a reproducer, not proof of the customer's root
cause. Their connector may inherit a different/stale PATH from Explorer or its
launcher; an actual connector report is needed to confirm that hypothesis.

The real setup form was exercised via a 32-bit STA reflection harness:
restricted PATH produced zero choices, the original DLL error, a visible report
link, and a saved report. Normal PATH populated the companies. These are control
assertions, not a substitute for visual inspection in the interactive desktop.
The interactive desktop was then checked through Mac Windows App: the actual
connector setup showed the reproduced DLL error and its report link; clicking
the link opened the report in Notepad. Relaunching with the normal environment
displayed all six companies in the picker. Screenshots are kept in ignored local
`artifacts/sdk-diagnostics-validation/`, not committed. Setup used a dummy token
and loopback API address; Connect company was never clicked.
No machine PATH, registry configuration, Sage installation files, grants, or
company data were changed to produce the failure.

## Automatic recovery for the installed Actian runtime

On a `DllNotFoundException` naming `w3dbav90.dll`, session initialization now
looks in the standard Program Files Actian Zen/PSQL and Pervasive PSQL runtime
folders. It loads the installed DLL by absolute path with
`LOAD_WITH_ALTERED_SEARCH_PATH`, prepends that folder to **this process's** PATH
(for Actian's dynamically loaded components), and retries `Begin` once using a
fresh session. It retains the native module for the process lifetime. It does
not copy DLLs, install software, change machine/user PATH, or bypass Sage access
approval. Other errors do not trigger this recovery. Failed recovery remains a
visible error with diagnostic evidence.

Loading just the primary DLL was insufficient on the lab: `PvStart` then failed
with DTI error 8020. Including the vendor directory in the connector process's
PATH resolved that dependency-discovery failure.

Verified the recovery build on 2026-09-22 with
`diagnostics/Test-ConnectorSdk.ps1`: baseline, restricted PATH, and restored PATH
all exited 0 and found six companies on both discovery attempts. The restricted
case asserts the original DLL exception, successful native preload and session
recovery, and no poisoned/uninitialized-session error. The real interactive
setup also populated its picker when launched with the restricted PATH. Local
report evidence is under `artifacts/sdk-diagnostics-validation/recovery/` and
`setup-recovered.png`.
