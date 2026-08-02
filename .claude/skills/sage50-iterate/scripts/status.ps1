# What is actually running on the VM right now.
Write-Output '=== processes ==='
Get-Process Peachw, Sage50Connector, Sage.Peachtree.Network.Connector2026 -ErrorAction SilentlyContinue |
  Select-Object ProcessName, Id, SessionId, StartTime | Format-Table -AutoSize

Write-Output '=== interactive sessions (need one for Sage + the connector) ==='
query user 2>&1 | Out-String

Write-Output '=== repo HEAD ==='
$git = 'C:\Program Files\Git\cmd\git.exe'
Set-Location 'C:\src\Sage50Connector'
Write-Output ((& $git rev-parse --short HEAD) + '  ' + (& $git log -1 --pretty=%s))

Write-Output '=== build outputs ==='
Get-Item 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe' -ErrorAction SilentlyContinue |
  ForEach-Object { Write-Output ('  exe ' + $_.LastWriteTime) }

Write-Output '=== config (token redacted) ==='
$cfg = Join-Path $env:ProgramData 'Rutter\Sage50Connector\sage50Config.json'
if (Test-Path $cfg) { (Get-Content $cfg -Raw) -replace 'iat_[A-Za-z0-9]+', 'iat_<redacted>' }
else { Write-Output 'no config at ' + $cfg }

Write-Output '=== service ==='
$svc = Get-CimInstance Win32_Service -Filter "Name='Sage50ConnectorService'" -ErrorAction SilentlyContinue
if ($svc) { Write-Output ('state=' + $svc.State + ' start=' + $svc.StartMode + ' account=' + $svc.StartName) }
else { Write-Output 'not installed' }
