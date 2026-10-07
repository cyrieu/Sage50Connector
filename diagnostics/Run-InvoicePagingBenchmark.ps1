param(
    [ValidateSet('full-dto', 'retained-keys', 'date-windows', 'production-windows')][string]$Mode,
    [string]$Company = 'Rutter Test Co',
    [string]$Destination = 'C:\src\Sage50InvoicePagingDiagnosticV3',
    [string]$TaskName = 'Sage50InvoicePagingDiagnostic',
    [string]$StartDate,
    [string]$EndDate,
    [switch]$DumpPayloads,
    [int]$TimeoutMinutes = 120
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Destination 'bin\Release\Sage50Connector.exe'
$resultDirectory = Join-Path $Destination 'results'
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$result = Join-Path $resultDirectory ($Mode + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '.json')
$arguments = '--benchmark-invoices "' + $Company + '" ' + $Mode
if ($Mode -eq 'date-windows') {
    if (-not $StartDate -or -not $EndDate) { throw 'date-windows requires StartDate (inclusive) and EndDate (exclusive).' }
    $arguments += ' ' + $StartDate + ' ' + $EndDate
}
$arguments += ' "' + $result + '"'
if ($DumpPayloads) { $arguments += ' --dump-payloads' }
$action = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $Destination
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
do {
    Start-Sleep -Seconds 2
    $task = Get-ScheduledTask -TaskName $TaskName
    $exists = Test-Path $result
} while ((-not $exists -or $task.State -ne 'Ready') -and [DateTime]::UtcNow -lt $deadline)
if ($exists -and $task.State -eq 'Ready') {
    $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName
    $manifest = Get-Content $result -Raw | ConvertFrom-Json
    if ($taskInfo.LastTaskResult -ne 0 -or $manifest.status -ne 'completed') { throw ('Diagnostic finished abnormally. Exit=' + $taskInfo.LastTaskResult + '; status=' + $manifest.status + '; result=' + $result) }
    Get-Content $result -Raw
} else { throw 'Diagnostic did not finish and write its result before timeout.' }
