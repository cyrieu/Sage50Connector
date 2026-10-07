$ErrorActionPreference = 'Stop'
$activeDiagnostics = @(Get-CimInstance Win32_Process -Filter "name='Sage50Connector.exe'" |
    Where-Object { $_.ExecutablePath -like 'C:\src\InvoiceWindowDiagnosticFinalV2\*' })
if ($activeDiagnostics.Count -gt 0) {
    throw 'Refusing to restart Sage Connect Service while this isolated diagnostic executable is running.'
}
$service = Get-Service -Name 'Sage 50 Connect Service 2026'
if (-not $service) { throw 'Expected Sage 50 Connect Service 2026 was not found.' }
Restart-Service -Name $service.Name -Force
$service = Get-Service -Name $service.Name
$service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))
Write-Output 'Sage 50 Connect Service 2026 restarted and is running; no isolated diagnostic process was active.'
