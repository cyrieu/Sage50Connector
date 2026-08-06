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

### Publishing after a signed release

After `release-via-ssh.sh` prints `SIGNED RELEASE OK`:

```bash
release_dir=artifacts/sage50-release-<git-sha>
ver=1.1.0   # must match Version.props / MSI ProductVersion
msi="$release_dir/RutterSage50ConnectorSetup.msi"
sha=$(shasum -a 256 "$msi" | awk '{print $1}')

# Immutable versioned MSI (preferred for in-app updater)
aws s3 cp "$msi" \
  "s3://rutterpublicimages/sage50-connector/RutterSage50ConnectorSetup-${ver}.msi" \
  --region us-east-2 \
  --content-type application/octet-stream \
  --metadata "git-sha=<sha>,version=${ver}"

# Manifest (updater reads this)
cat > /tmp/sage50-release.json <<EOF
{
  "version": "${ver}",
  "min_version": "1.0.0",
  "msi_url": "https://rutterpublicimages.s3.us-east-2.amazonaws.com/sage50-connector/RutterSage50ConnectorSetup-${ver}.msi",
  "sha256": "${sha}",
  "released_at": "$(date -u +%Y-%m-%d)",
  "notes": "Describe the release for customers.",
  "requires_sage_reapproval": true
}
EOF

aws s3 cp /tmp/sage50-release.json \
  "s3://rutterpublicimages/sage50-connector/release.json" \
  --region us-east-2 \
  --content-type application/json \
  --cache-control "max-age=60"

# Keep Link first-install zip in sync (latest MSI only)
zip -j "$release_dir/Sage 50 Connector Installer.zip" "$msi"
aws s3 cp "$release_dir/Sage 50 Connector Installer.zip" \
  "s3://rutterpublicimages/Sage 50 Connector Installer.zip" \
  --region us-east-2 \
  --content-type application/zip
```

Backend env (optional override for the JSON body):

- `SAGE_50_CONNECTOR_RELEASE_JSON` — raw JSON string for `GET /sage-50/connector-release`
- If unset, backend **proxies** the public S3 `release.json` (or returns a
  documented empty/up-to-date stub if fetch fails)

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
