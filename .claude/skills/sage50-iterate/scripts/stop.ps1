# Stop the connector. Run this BEFORE enqueuing jobs, not just before running.
#
# A live instance polls every 5 minutes, so anything enqueued while one is still
# up gets picked up by it — and if it started before the current Sage approval,
# it holds a cached failed PeachtreeSession and fails every job it touches. The
# jobs are gone before your intended run sees them, which reads as "the run only
# got NOOP".
Get-Process Sage50Connector -ErrorAction SilentlyContinue | ForEach-Object {
  Write-Output ('stopping Sage50Connector pid ' + $_.Id)
  $_.Kill()
}
Start-Sleep -Seconds 3
Unregister-ScheduledTask -TaskName RutterSageLive -Confirm:$false -ErrorAction SilentlyContinue

$left = Get-Process Sage50Connector -ErrorAction SilentlyContinue
if ($left) { Write-Output 'WARNING: still running'; $left | Select-Object Id | Format-Table -AutoSize }
else { Write-Output 'no connector running' }
