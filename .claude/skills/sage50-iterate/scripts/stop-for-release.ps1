# Stop the interactive tray app without leaking its Sage SDK session, and
# disable the development-only launch tasks while release artifacts are built.
$ErrorActionPreference = 'Stop'

Unregister-ScheduledTask -TaskName 'RutterSageLive' -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'RutterSageTray' -Confirm:$false -ErrorAction SilentlyContinue

$process = Get-Process Sage50Connector -ErrorAction SilentlyContinue
if (-not $process) {
  Write-Output 'connector was not running'
  exit 0
}

try {
  $quit = [System.Threading.EventWaitHandle]::OpenExisting('Global\RutterSage50ConnectorQuit')
  $quit.Set() | Out-Null
} catch {
  throw 'The running connector does not expose the global clean-shutdown signal. Exit it from the tray and retry.'
}

if (-not $process.WaitForExit(60000)) {
  throw 'Connector did not exit cleanly within 60 seconds. Exit it from the tray and retry; do not force-kill a release build.'
}
Write-Output 'connector exited cleanly'
