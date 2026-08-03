# Install the uploaded signed MSI as the canonical build output and verify both
# immutable release artifacts with Windows Authenticode.
$ErrorActionPreference = 'Stop'

$repo = 'C:\src\Sage50Connector'
$exe = Join-Path $repo 'bin\Release\Sage50Connector.exe'
$msi = Join-Path $repo 'Sage50ConnectorSetup\bin\Release\RutterSage50ConnectorSetup.msi'
$signedMsi = $msi + '.signed'

if (-not (Test-Path $signedMsi)) { throw "Signed MSI upload not found: $signedMsi" }
Copy-Item $signedMsi $msi -Force

foreach ($artifact in @($exe, $msi)) {
  $signature = Get-AuthenticodeSignature $artifact
  if ($signature.Status -ne 'Valid') {
    throw "Invalid signature for $artifact`: $($signature.Status) - $($signature.StatusMessage)"
  }

  $file = Get-Item $artifact
  Write-Output ('ARTIFACT: ' + $file.FullName)
  Write-Output ('  bytes: ' + $file.Length)
  Write-Output ('  sha256: ' + (Get-FileHash $artifact -Algorithm SHA256).Hash)
  Write-Output ('  status: ' + $signature.Status)
  Write-Output ('  signer: ' + $signature.SignerCertificate.Subject)
  Write-Output ('  timestamp: ' + $signature.TimeStamperCertificate.Subject)
}
