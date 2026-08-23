# Sage COM General Ledger credentials and approval

`TRANSACTIONS` is different from every other Sage 50 connector entity. It uses
the legacy Sage COM General Ledger Rows exporter, so it needs both a Sage-issued
COM partner credential and Sage's consent for the company. The normal .NET SDK
authorization does not supply the COM credential.

This is currently a lab/development procedure, not customer onboarding. The
credential helper is deliberately excluded from the MSI, and customers do not
receive Rutter's Sage-issued partner credential.

## Know which credential this is

Use the account name and password issued by Sage for COM third-party application
access. It is **not** any of the following:

- the Windows account or Sage company administrator login;
- the Sage `Peachtree` Data Access/Crystal Reports credential;
- a Rutter API token, connection ID, or inbound access token.

The values are secrets and are intentionally absent from this repository. Get
them from Rutter's approved secret store or the owner of the Sage partner
relationship. Do not put them in a shell command, source file, ticket, or log.

## Check whether this Windows user is already configured

Run this in PowerShell inside the same interactive Windows account that runs the
connector:

```powershell
$path = "$env:ProgramData\Rutter\Sage50Connector\diagnostics\sage-com-credential.xml"
Test-Path -LiteralPath $path
```

`True` means a credential file exists. It does not prove that the password is
current or that the selected company has approved access. Do not copy the file
to another user or machine: Windows DPAPI encrypts it for the Windows account
that created it.

## Create or replace the encrypted credential file

The helper lives in the source checkout and is not installed by the MSI. In an
interactive PowerShell window as the user who will run the connector:

```powershell
Set-Location C:\src\Sage50Connector
.\diagnostics\Set-GeneralLedgerComCredential.ps1
```

Enter the Sage-issued COM partner account name and password in the Windows
credential dialog. The script writes the default file:

```text
%ProgramData%\Rutter\Sage50Connector\diagnostics\sage-com-credential.xml
```

The password is DPAPI-encrypted and the file ACL is restricted to the current
Windows account. Re-running the helper replaces the file, which is the recovery
procedure after a password rotation or a corrupt file.

## Approve the company and verify the export

1. Sign into Windows as the same user that created the credential file.
2. Open Sage 50 and open the connector's configured company as a Sage
   administrator. The COM exporter attaches to this open company; opening the
   wrong company fails the GUID/name safety check.
3. Start the connector and request a `TRANSACTIONS` refresh. The first
   `GetApplication(partnerName, partnerPassword)` call registers or presents
   Sage's third-party application access request.
4. In Sage, review the company-data access request, select **Always allow
   access**, and click **OK**. This is a persistent financial-data consent
   decision. Automating it is permitted only on Rutter's development VM, never
   on a customer's machine.
5. If no prompt appears, use **File → Close Company**, reopen the company as an
   administrator, and retry the request. Sage commonly presents pending access
   requests only while opening a company.
6. Run the refresh again. Success is visible as `Retrieved <n> GL transactions
   from Sage 50 COM exporter` in the connector log, followed by successful
   `TRANSACTIONS` ingest reports.

The normal connector approval and COM setup are separate checks. A new connector
EXE hash still requires the normal Sage SDK re-approval described in `AGENTS.md`.
Replacing the COM credential file does not approve a new connector executable.

## Common failures

| Symptom | Meaning / recovery |
|---|---|
| Credential file missing | Run `Set-GeneralLedgerComCredential.ps1` interactively. |
| DPAPI/decryption failure | The file was created by another Windows user/machine or is corrupt; recreate it as the connector user. |
| `GetApplication` rejects the account | The Sage-issued partner name/password is wrong or rotated; retrieve the current secret and recreate the file. |
| No company open / company mismatch | Open the exact configured company in Sage in the connector user's desktop session. |
| Sage access is pending | Close and reopen the company as an administrator, approve the request, then retry. |
| Other entities work but `TRANSACTIONS` fails | Expected when only .NET SDK authorization exists; configure the separate COM credential and keep Sage open. |

## Product constraint

This flow is not zero-touch and is not ready for customer deployment. A
production design must arrange Sage partner credential provisioning without
exposing a shared secret to customers and must account for the COM exporter's
requirement that Sage be open in an interactive user session.
