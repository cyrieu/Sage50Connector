# Pull master on the VM and rebuild Release.
#
# Stops the connector first: a running instance holds a lock on
# bin\Release\Sage50Connector.exe and the build fails with MSB3027.
Get-Process Sage50Connector -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 4

$git = 'C:\Program Files\Git\cmd\git.exe'
$msb = 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$nuget = 'C:\BuildTools\nuget.exe'
Set-Location 'C:\src\Sage50Connector'

& $git fetch origin 2>&1 | Out-Null
& $git reset --hard origin/master 2>&1 | Out-Null
Write-Output ('HEAD: ' + (& $git rev-parse --short HEAD) + '  ' + (& $git log -1 --pretty=%s))

& $nuget restore Sage50Connector.sln -NonInteractive 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
  Write-Output 'NUGET RESTORE FAILED'
  exit $LASTEXITCODE
}

$build = & $msb Sage50Connector.sln /t:Rebuild /p:Configuration=Release /p:Platform=x86 /m /v:m /nologo 2>&1
$buildExit = $LASTEXITCODE
$errors = $build | Select-String ': error '
if ($buildExit -ne 0) {
  Write-Output 'BUILD FAILED'
  if ($errors) {
    $errors | Select-Object -First 20 | ForEach-Object { $_.Line.Trim() }
  } else {
    $build | Select-Object -Last 40
  }
  exit $buildExit
}

$exe = 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe'
$msi = 'C:\src\Sage50Connector\Sage50ConnectorSetup\bin\Release\RutterSage50ConnectorSetup.msi'
if (-not (Test-Path $exe) -or -not (Test-Path $msi)) {
  Write-Output 'BUILD FAILED: required release artifact is missing'
  Write-Output ('  exe exists: ' + (Test-Path $exe))
  Write-Output ('  msi exists: ' + (Test-Path $msi))
  exit 1
}

Write-Output 'BUILD OK (unsigned development artifacts)'
Get-Item 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe' |
  ForEach-Object { Write-Output ('  exe   ' + $_.LastWriteTime) }
Get-Item 'C:\src\Sage50Connector\Sage50ConnectorSetup\bin\Release\RutterSage50ConnectorSetup.msi' |
  ForEach-Object { Write-Output ('  msi   ' + $_.LastWriteTime) }
