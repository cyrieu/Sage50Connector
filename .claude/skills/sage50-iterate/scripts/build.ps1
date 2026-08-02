# Pull rutter/productionize-v1 on the VM and rebuild Release.
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
& $git reset --hard origin/rutter/productionize-v1 2>&1 | Out-Null
Write-Output ('HEAD: ' + (& $git rev-parse --short HEAD) + '  ' + (& $git log -1 --pretty=%s))

& $nuget restore Sage50Connector.sln -NonInteractive 2>&1 | Out-Null
$build = & $msb Sage50Connector.sln /t:Rebuild /p:Configuration=Release /m /v:m /nologo 2>&1
$errors = $build | Select-String 'error CS|error MSB'
if ($errors) {
  Write-Output 'BUILD FAILED'
  $errors | Select-Object -First 10 | ForEach-Object { $_.Line.Trim() }
  exit 1
}

Write-Output 'BUILD OK'
Get-Item 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe' |
  ForEach-Object { Write-Output ('  exe   ' + $_.LastWriteTime) }
Get-Item 'C:\src\Sage50Connector\Sage50ConnectorSetup\bin\Release\RutterSage50ConnectorSetup.msi' -ErrorAction SilentlyContinue |
  ForEach-Object { Write-Output ('  msi   ' + $_.LastWriteTime) }
