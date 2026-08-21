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
# Ask the running instance to quit so it hands its Sage session back. Killing it
# leaks the connection seat, and a few of those exhaust the licence.
$running = Get-Process Sage50Connector -ErrorAction SilentlyContinue
if ($running) {
  try {
    $quit = [System.Threading.EventWaitHandle]::OpenExisting('Global\RutterSage50ConnectorQuit')
    $quit.Set() | Out-Null
    if (-not $running.WaitForExit(20000)) {
      Write-Output 'graceful quit timed out; killing (this leaks a Sage seat)'
      $running | Stop-Process -Force
    } else {
      Write-Output 'previous instance exited cleanly'
    }
  } catch {
    # Older build with no quit listener.
    Write-Output 'no quit signal available; killing (this leaks a Sage seat)'
    $running | Stop-Process -Force
  }
}
Start-Sleep -Seconds 3

# Belt and braces: reclaim any seats leaked by earlier hard kills or crashes.
$connectSvc = Get-Service | Where-Object {
  $_.DisplayName -match 'Sage 50 Connect|Peachtree' -and $_.Status -eq 'Running'
}
foreach ($s in $connectSvc) {
  Restart-Service -Name $s.Name -Force -ErrorAction Continue
}
if ($connectSvc) { Start-Sleep -Seconds 8 }

$log = Join-Path $env:ProgramData 'Rutter\Sage50Connector\log.txt'
if (Test-Path $log) { Remove-Item $log -Force }

Unregister-ScheduledTask -TaskName RutterSageLive -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction `
  -Execute 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe' `
  -WorkingDirectory 'C:\src\Sage50Connector\bin\Release'
$windowsUser = if ($env:SAGE50_WINDOWS_USER) {
  $env:SAGE50_WINDOWS_USER
} else {
  $interactiveUser = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
  if ($interactiveUser) {
    $interactiveUser
  } else {
    $activeSession = query user 2>$null | Where-Object { $_ -match '\sActive\s' } | Select-Object -First 1
    if ($activeSession) { ($activeSession.Trim() -split '\s+')[0].TrimStart('>') } else { $env:USERNAME }
  }
}
$principal = New-ScheduledTaskPrincipal -UserId $windowsUser -LogonType Interactive -RunLevel Highest
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
