# Release leaked Sage SDK sessions.
#
# The connector never closes its PeachtreeSession, so every force-killed
# instance leaks a connection. Enough of them and Sage starts answering:
#
#   License is currently unavailable. You have reached the maximum number of
#   connections, please try again later
#
# Restarting the Sage network connector service drops the orphaned sessions.
Get-Process Sage50Connector -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# The connection broker is "Sage 50 Connect Service <year>" (the process is
# Sage.Peachtree.Network.Connector<year>). Match both namings across versions.
$svc = Get-Service | Where-Object {
  $_.DisplayName -match 'Sage 50 Connect|Peachtree' -and $_.Status -eq 'Running'
}
if (-not $svc) {
  Write-Output 'no running Sage connect service found; list follows'
  Get-Service | Where-Object { $_.DisplayName -match 'Sage|Peachtree' } |
    Select-Object Name, DisplayName, Status | Format-Table -AutoSize
  exit 1
}

foreach ($s in $svc) {
  Write-Output ('restarting: ' + $s.Name + ' (' + $s.DisplayName + ') was ' + $s.Status)
  Restart-Service -Name $s.Name -Force -ErrorAction Continue
}
Start-Sleep -Seconds 10

Get-Service | Where-Object { $_.DisplayName -match 'Sage|Peachtree' } |
  Select-Object Name, Status | Format-Table -AutoSize
Write-Output 'sessions released; Sage 50 may need the company reopened before the next run'
