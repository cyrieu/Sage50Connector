$ErrorActionPreference = 'Stop'
$logPath = Join-Path $env:ProgramData 'Rutter\Sage50Connector\log.txt'
if (-not (Test-Path -LiteralPath $logPath)) { Write-Output 'no-log-file'; exit 0 }
$matches = @(Get-Content -LiteralPath $logPath |
    Where-Object { $_ -match 'Lost the in-memory snapshot|Invoice window page size|Error handling LIST_FETCH job for INVOICES|Failed to report|Object reference not set|company is disconnected' } |
    Select-Object -Last 10)
$matches
