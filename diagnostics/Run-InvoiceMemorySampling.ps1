param(
    [Parameter(Mandatory = $true)][ValidateSet('full-dto', 'retained-keys', 'date-windows', 'production-windows')][string]$Mode,
    [string]$Destination = 'C:\src\Sage50InvoicePagingDiagnosticV5',
    [int]$StartTimeoutSeconds = 120,
    [int]$IntervalMilliseconds = 250
)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $Destination 'bin\Release\Sage50Connector.exe'
$result = Join-Path (Join-Path $Destination 'results') ('external-' + $Mode + '.csv')
$sampler = Join-Path $PSScriptRoot 'Sample-ProcessMemory.ps1'
& $sampler -ExecutablePath $executable -CsvPath $result -StartTimeoutSeconds $StartTimeoutSeconds -IntervalMilliseconds $IntervalMilliseconds
