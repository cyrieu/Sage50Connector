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
No machine PATH, registry configuration, Sage installation files, grants, or
company data were changed to produce the failure.
