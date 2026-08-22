[CmdletBinding()]
param(
    [string]$CredentialPath = "$env:ProgramData\Rutter\Sage50Connector\diagnostics\sage-com-credential.xml"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host 'Enter the Sage-issued COM third-party account name and password.'
Write-Host 'These are partner credentials, not a Sage company user and not the'
Write-Host 'Data Access/Crystal Reports credential.'

$credential = Get-Credential -Message 'Sage 50 COM third-party account credential'
if ($null -eq $credential) { throw 'Credential entry was cancelled.' }
if ([string]::IsNullOrWhiteSpace($credential.UserName)) { throw 'The third-party account name cannot be blank.' }
if ([string]::IsNullOrWhiteSpace($credential.GetNetworkCredential().Password)) {
    throw 'The third-party account password cannot be blank.'
}

$directory = Split-Path -Parent $CredentialPath
[void](New-Item -ItemType Directory -Path $directory -Force)
$credential | Export-Clixml -LiteralPath $CredentialPath -Force

# Export-Clixml uses Windows DPAPI. Only this Windows account on this machine
# can decrypt the password; it is never written as plaintext.
$acl = Get-Acl -LiteralPath $CredentialPath
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object Security.AccessControl.FileSystemAccessRule(
    [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [Security.AccessControl.FileSystemRights]::FullControl,
    [Security.AccessControl.AccessControlType]::Allow)
$acl.SetAccessRule($rule)
Set-Acl -LiteralPath $CredentialPath -AclObject $acl

Write-Host ('Encrypted Sage COM partner credential saved to ' + $CredentialPath)
