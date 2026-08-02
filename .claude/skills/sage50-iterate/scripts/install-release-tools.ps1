# Install the command-line tools required by release.ps1.
# Run once from an elevated PowerShell session on the Windows build VM.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Run this script from an elevated PowerShell session.'
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
  $azureCliInstaller = Join-Path $env:TEMP 'AzureCLI-x64.msi'
  Invoke-WebRequest 'https://aka.ms/installazurecliwindowsx64' -OutFile $azureCliInstaller
  $install = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
    '/I', $azureCliInstaller, '/quiet', '/norestart'
  )
  if ($install.ExitCode -ne 0) { throw "Azure CLI installation failed with exit code $($install.ExitCode)" }
}

if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
  [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
  $installChocolatey = New-Object Net.WebClient
  Invoke-Expression ($installChocolatey.DownloadString('https://community.chocolatey.org/install.ps1'))
}

# Jsign's Chocolatey package installs its Temurin Java runtime dependency.
& "$env:ProgramData\chocolatey\bin\choco.exe" install jsign -y --no-progress
if ($LASTEXITCODE -ne 0) { throw "Jsign installation failed with exit code $LASTEXITCODE" }

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
  [Environment]::GetEnvironmentVariable('Path', 'User')

Write-Output 'Release tools installed. Open a new PowerShell window before running release.ps1.'
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' version
& "$env:ProgramData\chocolatey\bin\jsign.exe" --version
