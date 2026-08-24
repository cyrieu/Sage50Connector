# Sage COM General Ledger credentials and approval

`TRANSACTIONS` is different from every other Sage 50 connector entity. It uses
the legacy Sage COM General Ledger Rows exporter, so it needs both a Sage-issued
COM partner credential and Sage's consent for the company. The normal .NET SDK
authorization does not supply the COM credential.

Customer onboarding provisions this credential automatically. The customer
never sees or types Rutter's Sage-issued partner credential.

## Know which credential this is

Use the account name and password issued by Sage for COM third-party application
access. It is **not** any of the following:

- the Windows account or Sage company administrator login;
- the Sage `Peachtree` Data Access/Crystal Reports credential;
- a Rutter API token, connection ID, or inbound access token.

The values are secrets and are intentionally absent from this repository.
Production reads them from `SAGE_50_COM_USERNAME` and
`SAGE_50_COM_PASSWORD`; configure those variables through the backend's secret
manager. Do not put them in source, an Item credential, a ticket, or a log.

## What customer setup does

1. The connector generates a one-use 2048-bit RSA key pair and sends only the
   public modulus/exponent with `/sage-50/complete-setup`.
2. The backend encrypts the Sage application username/password using RSA
   OAEP-SHA1 (the algorithm supported by .NET Framework's
   `RSACryptoServiceProvider`) and returns only ciphertext.
3. The connector decrypts the response in memory and protects it again with
   Windows DPAPI for the current user at:

```text
%ProgramData%\Rutter\Sage50Connector\sage-com-credential.bin
```

4. The ephemeral private key is destroyed. The backend does not persist the COM
   secret on the Rutter Item or Credential.
5. On an upgrade or deleted local file, the connector authenticates to
   `/sage-50/com-credential` with its existing inbound token and repeats the
   encrypted handoff.

DPAPI binds the stored ciphertext to the Windows account that completed setup.
Do not copy it to another user or machine.

## Legacy lab credential migration

Older lab installs may have this CLIXML file:

```text
%ProgramData%\Rutter\Sage50Connector\diagnostics\sage-com-credential.xml
```

The connector reads it once, writes the current DPAPI format, and continues.
`Set-GeneralLedgerComCredential.ps1` remains a development diagnostic only; it
is not part of customer installation or recovery.

## Approve the company and verify the export

1. Sign into Windows as the same user that completed connector setup.
2. Open Sage 50 and open the connector's configured company as a Sage
   administrator. The COM exporter attaches to this open company; opening the
   wrong company fails the GUID/name safety check.
3. Start the connector. Before it polls Rutter, the first
   `GetApplication(partnerName, partnerPassword)` call registers or presents
   Sage's transaction-access request.
4. In Sage, review the company-data access request, select **Always allow
   access**, and click **OK**. This is a persistent financial-data consent
   decision. Automating it is permitted only on Rutter's development VM, never
   on a customer's machine.
5. If no prompt appears, use **File → Close Company**, reopen the company as an
   administrator, and retry the request. Sage commonly presents pending access
   requests only while opening a company.
6. Click **Check access**. The tray status must say **Sage access: Fully
   approved** before initial sync begins.

The normal connector approval and COM company grant are separate checks. A new connector
EXE hash still requires the normal Sage SDK re-approval described in `AGENTS.md`.
Replacing the COM credential file does not approve a new connector executable.

## Common failures

| Symptom | Meaning / recovery |
|---|---|
| Credential file missing | Connector retrieves a fresh encrypted copy from `/sage-50/com-credential`; verify its inbound token and backend COM secret configuration. |
| DPAPI/decryption failure | The file belongs to another Windows user/machine or is corrupt; remove it and restart as the approved connector user to re-provision. |
| `GetApplication` rejects the account | Rotate/correct `SAGE_50_COM_USERNAME` / `SAGE_50_COM_PASSWORD` in the backend secret manager, remove the local DPAPI file, and restart. |
| No company open / company mismatch | Open the exact configured company in Sage in the connector user's desktop session. |
| Sage access is pending | Close and reopen the company as an administrator, approve the request, then retry. |
| Connector remains at transaction approval | Open the configured company as an administrator, approve the second company-data request, and keep Sage open. |

## Remaining product constraints

The secret handoff is automated, but Sage's persistent company-data approval is
intentionally customer-driven and cannot be automated. The COM exporter also
requires the configured company to remain open in the interactive Sage user
session while a General Ledger export runs.
