# Replace the unsigned build output with the uploaded signed EXE, verify it,
# then package the MSI without rebuilding project references.
$ErrorActionPreference = 'Stop'

$repo = 'C:\src\Sage50Connector'
$signed = Join-Path $repo 'bin\Release\Sage50Connector.exe.signed'
$exe = Join-Path $repo 'bin\Release\Sage50Connector.exe'
$wix = Join-Path $repo 'Sage50ConnectorSetup\Sage50ConnectorSetup.wixproj'
$msi = Join-Path $repo 'Sage50ConnectorSetup\bin\Release\RutterSage50ConnectorSetup.msi'
$msbuild = 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe'

if (Get-Process Sage50Connector -ErrorAction SilentlyContinue) {
  throw 'Sage50Connector is running. Stop it cleanly before packaging.'
}
if (-not (Test-Path $signed)) { throw "Signed EXE upload not found: $signed" }

Copy-Item $signed $exe -Force
$signature = Get-AuthenticodeSignature $exe
if ($signature.Status -ne 'Valid') {
  throw "Signed EXE failed Authenticode verification: $($signature.Status) - $($signature.StatusMessage)"
}
Write-Output ('EXE SIGNATURE: ' + $signature.Status)
Write-Output ('EXE SHA256: ' + (Get-FileHash $exe -Algorithm SHA256).Hash)

# AnyCPU selects the real C# output paths (bin\Release). Product.wxs still marks
# the MSI package itself x86. BuildProjectReferences=false is critical: rebuilding
# the connector here would overwrite the signed EXE before WiX embeds it.
# Read MSI version from Version.props so Product.wxs $(var.ProductVersion) is set
# even when this script builds the WiX project alone (Platform=AnyCPU).
$msiVersion = '1.1.0'
$versionProps = Join-Path $repo 'Version.props'
if (Test-Path $versionProps) {
  $m = [regex]::Match((Get-Content $versionProps -Raw), '<Sage50ConnectorMsiVersion>([^<]+)</Sage50ConnectorMsiVersion>')
  if ($m.Success) { $msiVersion = $m.Groups[1].Value.Trim() }
}
Write-Output ("MSI ProductVersion: " + $msiVersion)

& $msbuild $wix `
  /t:Rebuild `
  /p:Configuration=Release `
  /p:Platform=AnyCPU `
  /p:OutputPath=bin\Release\ `
  /p:BuildProjectReferences=false `
  /p:Sage50ConnectorMsiVersion=$msiVersion `
  /p:DefineConstants="ProductVersion=$msiVersion" `
  /m:1 `
  /v:minimal `
  /nologo
if ($LASTEXITCODE -ne 0) { throw "MSI packaging failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $msi)) { throw "MSI was not created: $msi" }

Write-Output ('MSI CREATED: ' + $msi)
Write-Output ('MSI SHA256: ' + (Get-FileHash $msi -Algorithm SHA256).Hash)
