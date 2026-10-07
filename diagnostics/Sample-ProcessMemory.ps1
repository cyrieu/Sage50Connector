param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$CsvPath,
    [int]$StartTimeoutSeconds = 120,
    [int]$IntervalMilliseconds = 250
)
$ErrorActionPreference = 'Stop'
$fullPath = [IO.Path]::GetFullPath($ExecutablePath)
$csvFullPath = [IO.Path]::GetFullPath($CsvPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $csvFullPath) -Force | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds($StartTimeoutSeconds)
$target = $null
while ([DateTime]::UtcNow -lt $deadline -and $null -eq $target) {
    $target = Get-CimInstance Win32_Process -Filter "Name='Sage50Connector.exe'" |
        Where-Object { $_.ExecutablePath -eq $fullPath } |
        Select-Object -First 1
    if ($null -eq $target) { Start-Sleep -Milliseconds 100 }
}
if ($null -eq $target) { throw "No process started from $fullPath before timeout." }
$processId = [int]$target.ProcessId
$startUtc = [DateTime]::UtcNow
@('utc,elapsed_ms,process_id,private_bytes,working_set_bytes') | Set-Content -LiteralPath $csvFullPath -Encoding ASCII
while ($true) {
    try { $process = Get-Process -Id $processId -ErrorAction Stop }
    catch { break }
    $now = [DateTime]::UtcNow
    '{0:o},{1},{2},{3},{4}' -f $now, [int]($now - $startUtc).TotalMilliseconds, $processId, $process.PrivateMemorySize64, $process.WorkingSet64 |
        Add-Content -LiteralPath $csvFullPath -Encoding ASCII
    Start-Sleep -Milliseconds $IntervalMilliseconds
}
Write-Output "Sampled process $processId to $csvFullPath"
