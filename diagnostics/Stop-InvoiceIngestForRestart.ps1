param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [string]$Destination = 'C:\src\InvoiceWindowDiagnosticFinalV2'
)
$ErrorActionPreference = 'Stop'
$expectedExe = Join-Path $Destination 'bin\Release\Sage50Connector.exe'
$process = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ProcessId)
if (-not $process -or $process.ExecutablePath -ne $expectedExe -or -not $process.CommandLine.Contains($ResultPath)) {
    throw 'Refusing to stop a process that is not this exact isolated restart diagnostic.'
}
Stop-Process -Id $ProcessId -Force
Write-Output 'isolated restart diagnostic stopped after backend commit'
