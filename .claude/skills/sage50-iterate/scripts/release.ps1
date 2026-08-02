param(
  [string]$Subscription = 'Azure Signing Certificate',
  [string]$Endpoint = 'https://eus.codesigning.azure.net',
  [string]$SigningAccount = 'RutterSigning',
  [string]$CertificateProfile = 'DynamicsCertificate'
)

# Produce immutable, signed customer release artifacts.
# Run this from an interactive PowerShell session on the Windows VM after
# authenticating Azure CLI as an identity with the Artifact Signing Certificate
# Profile Signer role. Never rebuild or otherwise modify the outputs afterward.
$ErrorActionPreference = 'Stop'

$repo = 'C:\src\Sage50Connector'
$solution = Join-Path $repo 'Sage50Connector.sln'
$wixProject = Join-Path $repo 'Sage50ConnectorSetup\Sage50ConnectorSetup.wixproj'
$exe = Join-Path $repo 'bin\Release\Sage50Connector.exe'
$msi = Join-Path $repo 'Sage50ConnectorSetup\bin\Release\RutterSage50ConnectorSetup.msi'
$msbuild = 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$nuget = 'C:\BuildTools\nuget.exe'

foreach ($required in @($solution, $wixProject, $msbuild, $nuget)) {
  if (-not (Test-Path $required)) { throw "Required path not found: $required" }
}
foreach ($command in @('az', 'jsign')) {
  if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
    throw "$command is not installed or is not on PATH. Install it, open a new PowerShell window, and retry."
  }
}

$running = Get-Process Sage50Connector -ErrorAction SilentlyContinue
if ($running) {
  throw 'Sage50Connector is running. Exit it from the tray before releasing so the EXE can be rebuilt without force-killing and leaking a Sage session.'
}

Set-Location $repo
& $nuget restore $solution -NonInteractive
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed with exit code $LASTEXITCODE" }

# Build through the solution so its Release|x86 mappings keep the x86 WiX
# project while mapping the C# projects to their Release|AnyCPU configurations.
& $msbuild $solution /t:Rebuild /p:Configuration=Release /p:Platform=x86 /m:1 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "Release EXE was not created: $exe" }

az account show --output none 2>$null
if ($LASTEXITCODE -ne 0) {
  throw "Azure CLI is not authenticated. Run 'az login', then retry."
}
az account set --subscription $Subscription
if ($LASTEXITCODE -ne 0) { throw "Could not select Azure subscription '$Subscription'." }

$token = az account get-access-token `
  --resource 'https://codesigning.azure.net' `
  --query accessToken `
  --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
  throw 'Could not obtain an Azure Artifact Signing access token.'
}

$alias = "$SigningAccount/$CertificateProfile"
try {
  # Signing changes the EXE bytes and therefore its Sage authorization MD5.
  # Package only after this step so the MSI embeds the signed executable.
  & jsign --storetype TRUSTEDSIGNING `
    --keystore $Endpoint `
    --storepass $token `
    --alias $alias `
    --name 'Rutter Sage 50 Connector' `
    $exe
  if ($LASTEXITCODE -ne 0) { throw "EXE signing failed with exit code $LASTEXITCODE" }

  # Do not rebuild project references: that would overwrite the signed EXE.
  & $msbuild $wixProject `
    /t:Rebuild `
    /p:Configuration=Release `
    /p:Platform=x86 `
    /p:BuildProjectReferences=false `
    /m:1 `
    /v:minimal `
    /nologo
  if ($LASTEXITCODE -ne 0) { throw "MSI packaging failed with exit code $LASTEXITCODE" }
  if (-not (Test-Path $msi)) { throw "Release MSI was not created: $msi" }

  & jsign --storetype TRUSTEDSIGNING `
    --keystore $Endpoint `
    --storepass $token `
    --alias $alias `
    --name 'Rutter Sage 50 Connector' `
    $msi
  if ($LASTEXITCODE -ne 0) { throw "MSI signing failed with exit code $LASTEXITCODE" }
}
finally {
  $token = $null
}

foreach ($artifact in @($exe, $msi)) {
  & jsign show --verbose $artifact
  if ($LASTEXITCODE -ne 0) { throw "Jsign verification failed: $artifact" }

  $signature = Get-AuthenticodeSignature $artifact
  if ($signature.Status -ne 'Valid') {
    throw "Authenticode verification failed for $artifact`: $($signature.Status) - $($signature.StatusMessage)"
  }

  $file = Get-Item $artifact
  $sha256 = (Get-FileHash $artifact -Algorithm SHA256).Hash
  Write-Output ('RELEASE ARTIFACT: ' + $file.FullName)
  Write-Output ('  bytes:  ' + $file.Length)
  Write-Output ('  sha256: ' + $sha256)
  Write-Output ('  signer: ' + $signature.SignerCertificate.Subject)
}

Write-Output 'SIGNED RELEASE OK'
Write-Output 'Do not rebuild, re-sign, or modify these artifacts. Approve and test this exact signed EXE in Sage.'
