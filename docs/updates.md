# Connector install + assisted auto-update

## Constraints

Sage 50 authorizes third-party apps by **MD5 of the executable**. Any real
version bump produces a new EXE hash, so the customer must open the company as
an admin and choose **Always Allow Access** again.

**Silent auto-update that keeps syncing without a human is impossible.** The
product model is **assisted upgrade**:

1. Tray detects a newer release.
2. Customer confirms install (UAC / elevated MSI).
3. Connector restarts.
4. UI shows “Approval required for this version” until they re-approve in Sage.

Config under `%ProgramData%\Rutter\Sage50Connector\` is preserved across MSI
upgrades (company, inbound token, connection id).

## Versioning

| File | Role |
|---|---|
| `Version.props` | Source of truth: `Sage50ConnectorVersion` (4-part), `Sage50ConnectorMsiVersion` (3-part) |
| `Properties/AssemblyInfo.cs` | Must match `Sage50ConnectorVersion` |
| WiX `Product.wxs` | `Version="$(var.ProductVersion)"` from MSI version |

Bump **both** props (and AssemblyInfo) on every customer release. WiX
`MajorUpgrade` needs a higher three-part version than the installed product.

## Release manifest

Published JSON (example):

```json
{
  "version": "1.1.0",
  "min_version": "1.0.0",
  "msi_url": "https://rutterpublicimages.s3.us-east-2.amazonaws.com/sage50-connector/RutterSage50ConnectorSetup-1.1.0.msi",
  "sha256": "<lowercase hex of the MSI>",
  "released_at": "2026-08-06",
  "notes": "Assisted auto-update support.",
  "requires_sage_reapproval": true
}
```

### Where the connector looks

1. `{ApiBaseUrl}/sage-50/connector-release` (Rutter backend; prefer this)
2. Fallback:
   `https://rutterpublicimages.s3.us-east-2.amazonaws.com/sage50-connector/release.json`

### Publishing (automated)

`release-via-ssh.sh` calls `publish-release.sh` after signing unless
`SAGE50_SKIP_PUBLISH=1`. That uploads:

| Object | Purpose |
|---|---|
| `sage50-connector/RutterSage50ConnectorSetup-<ver>.msi` | Immutable updater target |
| `sage50-connector/release.json` | Manifest for Check for updates |
| `Sage 50 Connector Installer.zip` | Link first-install download (**signed MSI** at zip root) |

Customer Link/updater artifacts must be Authenticode-signed (EXE **and** MSI)
via `release-via-ssh.sh`. Unsigned `build.ps1` MSIs must not be uploaded to
these keys. SmartScreen can still warn on a signed MSI until the publisher has
reputation; that is not a missing-signature bug. Prefer
`SAGE_50_INSTALLER_URL` pointing at the versioned `.msi` URL rather than the
zip.

```bash
# Full customer release (sign + publish)
# Use ericincident for S3 — not default (default is usually an expired aws login).
export AWS_PROFILE=ericincident
export SAGE50_RELEASE_NOTES='What customers see in the update dialog.'
.claude/skills/sage50-iterate/scripts/release-via-ssh.sh

# Sign only, publish later
SAGE50_SKIP_PUBLISH=1 .claude/skills/sage50-iterate/scripts/release-via-ssh.sh
export AWS_PROFILE=ericincident
.claude/skills/sage50-iterate/scripts/publish-release.sh \
  artifacts/sage50-release-<short-sha>
```

Before releasing, bump `Version.props` **and** `Properties/AssemblyInfo.cs` to
the same four-part assembly version (MSI uses the three-part
`Sage50ConnectorMsiVersion`). The release script refuses to start if they
disagree.

Backend env (optional):

- `SAGE_50_CONNECTOR_RELEASE_JSON` — raw JSON for `GET /sage-50/connector-release`
- `SAGE_50_CONNECTOR_RELEASE_URL` — alternate upstream URL for the proxy
- If unset, backend proxies the public S3 `release.json`

## In-app UX

- Tray → **Check for updates…**
- Background check ~every 6h (throttled to 24h between successful comparisons)
- Balloon when optional/required update is found
- Status form shows version + update line
- Apply: download MSI → SHA-256 verify → elevated `msiexec` → restart EXE

## Forced updates

Set `min_version` in the manifest higher than some fielded builds. Those
installs see **Required update** and are prompted harder; we do not hard-kill
sync yet (so support can still diagnose).

## First install (unchanged)

Link → `/sage-50/installer` → zip of latest MSI → company select deep link →
Sage Always allow.
