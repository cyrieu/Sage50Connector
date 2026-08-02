# Start the connector as the interactive desktop user and show what it did.
#
# It MUST run as the user who approved access in Sage: Sage records the grant
# per Windows user, and az vm run-command itself is SYSTEM in session 0, which
# gets "Authorization result = Pending" forever. A scheduled task with an
# Interactive principal runs in the logged-on user's session without a password.
#
# Any already-running instance is killed first. One that started before an
# approval caches its failed PeachtreeSession, and because it wakes every five
# minutes on NOOP it will grab freshly enqueued jobs and fail them before this
# run sees them.
Get-Process Sage50Connector -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$log = Join-Path $env:ProgramData 'Rutter\Sage50Connector\log.txt'
if (Test-Path $log) { Remove-Item $log -Force }

Unregister-ScheduledTask -TaskName RutterSageLive -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction `
  -Execute 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe' `
  -WorkingDirectory 'C:\src\Sage50Connector\bin\Release'
$principal = New-ScheduledTaskPrincipal -UserId 'rutteradmin' -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName RutterSageLive -Action $action -Principal $principal | Out-Null
Start-ScheduledTask -TaskName RutterSageLive

# Long enough to drain a few jobs and reach NOOP. The process is deliberately
# left running afterwards: it polls every 5 minutes, and killing it mid-session
# is what the stale-session note above is about.
Start-Sleep -Seconds 60

Write-Output '=== connector log ==='
if (Test-Path $log) {
  (Get-Content $log |
    Select-String 'Fetched|Sage returned|Pending|Error|Failed|NOOP|Successfully posted|Poll failed|Could not reach|Releasing Sage session|Loaded configuration') -replace
    'iat_[A-Za-z0-9]+', 'iat_<redacted>'
} else {
  Write-Output 'no log written - did the task start?'
}

Write-Output '=== process ==='
Get-Process Sage50Connector -ErrorAction SilentlyContinue |
  Select-Object Id, SessionId | Format-Table -AutoSize
