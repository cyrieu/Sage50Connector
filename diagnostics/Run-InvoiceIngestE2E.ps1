param(
    [string]$Destination = 'C:\src\InvoiceWindowDiagnosticFinalV2',
    [string]$ConfigPath = 'C:\src\InvoiceWindowDiagnosticFinalV2\e2e-secrets\invoice-ingest-e2e.json',
    [string]$TaskName = 'Sage50InvoiceIngestE2E',
    [int]$TimeoutMinutes = 30
)
$ErrorActionPreference = 'Stop'

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ($config.CompanyName -cne 'Rutter Test Co') { throw 'This diagnostic is restricted to Rutter Test Co.' }
$exe = Join-Path $Destination 'bin\Release\Sage50Connector.exe'
$resultDirectory = Join-Path $Destination 'results'
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$result = Join-Path $resultDirectory ('invoice-ingest-e2e-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '.json')
$arguments = '--e2e-invoice-fetch "' + $ConfigPath + '" "' + $result + '"'
$action = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $Destination
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
do {
    Start-Sleep -Seconds 2
    $task = Get-ScheduledTask -TaskName $TaskName
    $exists = Test-Path -LiteralPath $result
} while ((-not $exists -or $task.State -ne 'Ready') -and [DateTime]::UtcNow -lt $deadline)

if (-not $exists -or $task.State -ne 'Ready') { throw 'Invoice diagnostic did not finish before timeout.' }
$taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName
$manifest = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
if ($taskInfo.LastTaskResult -ne 0 -or $manifest.status -ne 'completed') {
    throw ('Invoice diagnostic failed. Exit=' + $taskInfo.LastTaskResult + '; status=' + $manifest.status + '; result=' + $result)
}
Get-Content -LiteralPath $result -Raw
