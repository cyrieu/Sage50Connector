# Sage 50 Connector Runbook

## What runs where

- Develop and commit C# changes on macOS if preferred.
- Build and run the connector on the Windows VM: it requires Sage 50 and the Sage Peachtree SDK.
- The SDK is installed on the VM at `C:\Program Files (x86)\Sage\Peachtree\API`, including `Sage.Peachtree.API.dll`.
- Build the `Sage50Connector.sln` solution as **x86** and target .NET Framework 4.8. `Any CPU` is not appropriate for the Sage SDK.

## Build and run on the VM

1. Pull the desired Git commit on the VM.
2. Open `Sage50Connector.sln` in Visual Studio 2022 (not merely the repository folder).
3. Select `Debug` and `x86` in the toolbar.
4. Use **Build -> Rebuild Solution** after source changes. Changing only the external config does not require a rebuild.
5. Keep Sage 50 open with the target company, then run the `Sage50Connector` startup project.

The one-shot executable polls for a desktop job and exits when there is no job. `Sage50ConnectorService` is the long-running Windows Service wrapper; it calls the connector at startup and then once per minute.

## Runtime configuration and logs

The executable does **not** use the checked-in `Connector.config` for its ingest credentials. It reads this external file instead:

`C:\Users\Default\Documents\sage50Config.json`

Use normal JSON:

```json
{
  "CompanyName": "Bellwether Garden Supply",
  "AccessKey": "<credential inbound access token>",
  "ConnectionId": "<Rutter item ID>"
}
```

Never commit this file or paste its credential values into tickets, chat, or logs.

`Program.cs` parses this file with `JObject.Parse`. Do not revert this to hand-written string slicing: string slicing includes quotes in normal JSON values and causes authentication failures.

Runtime logs are written to:

`C:\Users\Default\Documents\log.txt`

The connector logs the config path, company name, connection ID, and access-token length, but must never log a token value.

## Rutter authentication contract

The ingest endpoint pairs:

- `ConnectionId`: the Rutter item ID.
- `AccessKey`: the item's credential **inbound access token**.

Do **not** use the item's regular `access_token`, public token, organization client ID, or organization client secret as `AccessKey`. A 401 with `Invalid access token/connectionId pair` means this pair is wrong or the executable is reading a different config/build.

## Sage authorization

On first access, the Sage API returns `Authorization result = Pending`.

1. Run the connector once to register the request.
2. Close and reopen the Sage company as a Sage administrator.
3. Sage displays the third-party application access request.
4. Select **Always Allow Access**.
5. Run the connector again.

The approval may not appear while the company is already open. A pending authorization is not a company-name mismatch; the exact company used in this setup is `Bellwether Garden Supply`.

## Expected behavior and verification

The connector polls `POST /versioned/ingest` and handles:

- `LIST_FETCH` for Accounts, Vendors, and Customers.
- `CREATE` for Vendors.
- `NOOP` by waiting five minutes before polling again.

After Rutter authentication succeeds, an `Authorization result = Pending` is a Sage-side approval issue. An internal-server-error response while posting that Sage error is secondary; approve Sage access first.

For an authorized production verification, use a read-only database lookup to check the item's `desktop_platform_jobs` and `platform_entities` records. Do not query or print credential values in shared output.
