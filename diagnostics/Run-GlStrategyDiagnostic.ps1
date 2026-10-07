param(
    [ValidateSet('capture','baseline','stream-dictionary','stream-groups','disk-spool')][string]$Mode,
    [string]$Destination='C:\src\GlStrategyDiagnosticV1',
    [string]$Company='Rutter Test Co',
    [Parameter(Mandatory=$true)][string]$CsvPath,
    [string]$StartDate,
    [string]$EndDate,
    [int]$TimeoutMinutes=60
)
$ErrorActionPreference='Stop'
$results=Join-Path $Destination 'results'
New-Item -ItemType Directory -Force $results | Out-Null
$result=Join-Path $results ($Mode+'-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'.json')
$exe=Join-Path $Destination 'bin\Release\Sage50Connector.exe'
if ($Mode -eq 'capture') {
    $arguments='--capture-gl-csv "'+$Company+'" "'+$CsvPath+'" "'+$result+'"'
} else {
    $arguments='--benchmark-gl-csv "'+$CsvPath+'" '+$Mode+' "'+$result+'"'
    if ($StartDate -or $EndDate) { $arguments+=' "'+$StartDate+'" "'+$EndDate+'"' }
}
$taskName='Sage50GlStrategyDiagnostic'
if ((Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue).State -eq 'Running') { throw 'Another GL diagnostic is active.' }
$action=New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $Destination
$principal=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
$deadline=[DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
do {
    Start-Sleep -Seconds 2
    $task=Get-ScheduledTask -TaskName $taskName
} while (($task.State -ne 'Ready' -or -not (Test-Path $result)) -and [DateTime]::UtcNow -lt $deadline)
if ($task.State -ne 'Ready') { throw 'GL diagnostic remains running at timeout; leave it to release COM cleanly.' }
if (-not (Test-Path $result)) { throw 'GL diagnostic did not write a result.' }
Get-Content $result -Raw
$manifest=Get-Content $result -Raw | ConvertFrom-Json
if ($manifest.status -ne 'completed' -or (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult -ne 0) { throw ('GL diagnostic failed: '+$result) }
