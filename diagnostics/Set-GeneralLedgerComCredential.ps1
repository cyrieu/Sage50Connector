[CmdletBinding()]
param(
    [string]$CredentialPath = "$env:ProgramData\Rutter\Sage50Connector\diagnostics\sage-com-credential.xml"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host 'Enter the Sage 50 external-data password configured under:'
Write-Host 'Maintain > Users > Set Up Security > Data Access/Crystal Reports.'
Write-Host 'The user ID is always Peachtree and the password is at most eight characters.'

$credential = Get-Credential -UserName 'Peachtree' -Message 'Sage 50 external-data credential'
if ($null -eq $credential) { throw 'Credential entry was cancelled.' }
if ($credential.UserName -ne 'Peachtree') { throw "The user ID must be 'Peachtree'." }
if ([string]::IsNullOrWhiteSpace($credential.GetNetworkCredential().Password)) {
    throw 'The Sage external-data password cannot be blank.'
}
if ($credential.GetNetworkCredential().Password.Length -gt 8) {
    throw 'Sage external-data passwords are limited to eight characters.'
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

Write-Host ('Encrypted Sage COM credential saved to ' + $CredentialPath)
