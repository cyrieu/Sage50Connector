param(
    [string]$Destination = 'C:\src\InvoiceWindowDiagnosticFinalV2',
    [string]$ConfigPath = 'C:\src\InvoiceWindowDiagnosticFinalV2\e2e-secrets\invoice-ingest-e2e.json',
    [string]$TaskName = 'Sage50InvoiceIngestRestart'
)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ($config.CompanyName -cne 'Rutter Test Co') { throw 'This diagnostic is restricted to Rutter Test Co.' }
$exe = Join-Path $Destination 'bin\Release\Sage50Connector.exe'
$resultDirectory = Join-Path $Destination 'results'
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$result = Join-Path $resultDirectory ('invoice-ingest-restart-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '.json')
$arguments = '--e2e-invoice-fetch "' + $ConfigPath + '" "' + $result + '"'
$action = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $Destination
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

$deadline = [DateTime]::UtcNow.AddSeconds(30)
$process = $null
do {
    Start-Sleep -Milliseconds 250
    $process = Get-CimInstance Win32_Process -Filter "name='Sage50Connector.exe'" |
        Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine.Contains($result) } |
        Select-Object -First 1
} while (-not $process -and [DateTime]::UtcNow -lt $deadline)
if (-not $process) { throw 'The isolated restart-test process did not start.' }
Write-Output ('pid=' + $process.ProcessId)
Write-Output ('result=' + $result)
