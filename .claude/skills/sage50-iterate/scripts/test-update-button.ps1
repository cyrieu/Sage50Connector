# Smoke-test the status-window update button against the built x86 connector.
# Run through vmrun.sh after build.ps1. No Sage session or release publication
# is required: this injects the same SyncStatus result produced by a manifest.

if ([Environment]::Is64BitProcess) {
  $powershell32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
  & $powershell32 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
  exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$bin = 'C:\src\Sage50Connector\bin\Release'
Set-Location $bin

Add-Type -AssemblyName System.Windows.Forms
[Reflection.Assembly]::LoadFrom((Join-Path $bin 'Sage50Connector.exe')) | Out-Null

$form = New-Object Sage50Connector.Ui.StatusForm
$raised = $false
$form.add_UpdateRequested({ $script:raised = $true })
$form.Show()

$release = New-Object Sage50Connector.Helpers.ConnectorRelease
$release.Version = '9.9.9'
$result = New-Object Sage50Connector.Helpers.UpdateCheckResult
$result.Availability = [Sage50Connector.Helpers.UpdateAvailability]::OptionalUpdate
$result.Release = $release
[Sage50Connector.Helpers.SyncStatus]::Instance.SetUpdateAvailability($result)
[System.Windows.Forms.Application]::DoEvents()

$button = $form.Controls |
  Where-Object { $_ -is [System.Windows.Forms.Button] -and $_.Text -eq 'Update to 9.9.9' } |
  Select-Object -First 1

if (-not $button) {
  throw 'Update button was not rendered with the available version.'
}
if (-not $button.Visible -or -not $button.Enabled) {
  throw 'Update button was not visible and enabled.'
}

$button.PerformClick()
[System.Windows.Forms.Application]::DoEvents()
if (-not $raised) {
  throw 'Clicking the update button did not raise UpdateRequested.'
}

$form.Close()
$form.Dispose()
Write-Output 'UPDATE BUTTON SMOKE TEST OK'
