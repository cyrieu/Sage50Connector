---
name: sage50-release
description: Build, Authenticode-sign, package, and verify a customer-ready Sage 50 Connector EXE and MSI using the Windows VM over SSH and Rutter's Azure Artifact Signing account. Use only when the user explicitly asks for a signed release, signed installer, customer distributable, or Artifact Signing. Do not use for ordinary development builds, rebuilds, testing, iteration, or validation.
---

# Release the Sage 50 Connector

Produce one immutable signed EXE and MSI from the pushed tip of
`rutter/productionize-v1`, then publish them for **Link first install** and
**in-app assisted auto-update**. Build and package on the Windows VM; sign and
publish on the Mac. Azure credentials never need to be stored on the VM.

## Invocation gate

Run this workflow only after an explicit request for a **signed** release or
customer distributable. A request to build, rebuild, test, iterate, deploy to
the development VM, or produce an MSI is not authorization to sign. For those
requests, use the unsigned development workflow in `$sage50-iterate` and stop
after build/test verification.

Read `CLAUDE.md` and `docs/updates.md` at the repository root before releasing.

## Preconditions

Confirm all of the following:

- **`Version.props` and `Properties/AssemblyInfo.cs` are bumped** and match
  (`Sage50ConnectorVersion` / `AssemblyVersion` / `AssemblyFileVersion`, and
  three-part `Sage50ConnectorMsiVersion` for WiX). The release script refuses
  to run if they disagree.
- The working tree is clean and the intended commit is pushed to
  `origin/rutter/productionize-v1`.
- Lab SSH env is set and works: `SAGE50_SSH_HOST`, `SAGE50_SSH_USER`,
  `SAGE50_SSH_KEY`. If SSH times out, the VM may be deallocated — start it
  first with `az vm start` using `SAGE50_VM_RG` / `SAGE50_VM_NAME` /
  `SAGE50_VM_SUBSCRIPTION`.
- Signing env is set: `SAGE50_SIGNING_SUBSCRIPTION`,
  `SAGE50_SIGNING_ENDPOINT`, `SAGE50_SIGNING_ACCOUNT`,
  `SAGE50_SIGNING_CERTIFICATE_PROFILE`.
- `az`, `jsign`, `ssh`, `scp`, `aws`, and `zip` are installed on the Mac
  (`aws`/`zip` required unless `SAGE50_SKIP_PUBLISH=1`).
- The Mac's Azure CLI identity is authenticated for the signing subscription.
- AWS can write the public installer bucket (default
  `s3://rutterpublicimages/` in `us-east-2`).
- No release directory already exists for the current commit. Never overwrite,
  rebuild, modify, or re-sign an existing release.

Optional publish env:

| Env | Default | Purpose |
|---|---|---|
| `SAGE50_RELEASE_NOTES` | empty | Customer-facing notes in `release.json` |
| `SAGE50_MIN_VERSION` | `1.0.0` | Forced-update floor for in-app checker |
| `SAGE50_PUBLISH_BUCKET` | `rutterpublicimages` | S3 bucket |
| `SAGE50_PUBLISH_REGION` | `us-east-2` | S3 region |
| `SAGE50_SKIP_PUBLISH` | unset | `1` = sign only, no S3 |
| `SAGE50_SKIP_LINK_ZIP` | unset | `1` = skip Link installer zip |
| `SAGE50_SKIP_VERSIONED_MSI` | unset | `1` = skip versioned MSI object |

Do not print Azure access tokens, connector access keys, or other credentials.

## Create the release

From the repository root:

```bash
# Optional: customer-facing release notes for the updater dialog
export SAGE50_RELEASE_NOTES='Bug fixes and improved setup.'

.claude/skills/sage50-iterate/scripts/release-via-ssh.sh
```

That script:

1. Verifies version files, clean tree, and `origin/rutter/productionize-v1`.
2. Stops the connector on the VM and builds Release x86 artifacts.
3. Copies the unsigned EXE to the Mac and signs with Azure Artifact Signing.
4. Returns the signed EXE and packages the MSI (signed EXE embedded).
5. Signs the MSI, returns it, verifies Authenticode on the VM.
6. Writes local artifacts under `artifacts/sage50-release-<git-sha>/`.
7. **Publishes** (unless `SAGE50_SKIP_PUBLISH=1`) via `publish-release.sh`:
   - Versioned MSI:
     `s3://…/sage50-connector/RutterSage50ConnectorSetup-<version>.msi`
   - Update manifest:
     `s3://…/sage50-connector/release.json`
   - Link first-install zip:
     `s3://…/Sage 50 Connector Installer.zip`

### Sign-only (no publish)

```bash
SAGE50_SKIP_PUBLISH=1 .claude/skills/sage50-iterate/scripts/release-via-ssh.sh
# later:
.claude/skills/sage50-iterate/scripts/publish-release.sh \
  artifacts/sage50-release-<short-sha>
```

### Re-publish an existing signed release dir

```bash
.claude/skills/sage50-iterate/scripts/publish-release.sh \
  artifacts/sage50-release-<short-sha>
```

## Verify

Success requires:

- Script exits zero and prints `SIGNED RELEASE OK` (and `PUBLISH OK` unless skip).
- Windows Authenticode `status: Valid` for EXE and MSI (finalize step).
- Local `artifacts/sage50-release-<sha>/` contains EXE, MSI, `release.json`,
  `PUBLISH.txt` (after publish).
- `curl -sI` on the public manifest and versioned MSI shows 200.
- Backend `GET /sage-50/connector-release` proxies the same manifest (or use
  `SAGE_50_CONNECTOR_RELEASE_JSON` override).

## Installer URLs

| Consumer | Object |
|---|---|
| Link first install (`GET /sage-50/installer`) | `Sage 50 Connector Installer.zip` (latest MSI at zip root) |
| In-app **Check for updates** | `sage50-connector/release.json` → versioned MSI URL |
| Backend proxy | `GET /sage-50/connector-release` |

Default public base:

```text
https://rutterpublicimages.s3.us-east-2.amazonaws.com/
```

Production may override Link redirect with `SAGE_50_INSTALLER_URL`.

## Report

Report:

- Source commit and product version (`Version.props`)
- Absolute artifact directory
- SHA-256 of EXE + MSI
- Signature status
- S3 keys published + public URLs
- Reminder: **new EXE MD5 → every customer must re-approve in Sage 50**

Do not install the release on the VM or start the connector unless the user
separately requests that. Publishing is part of a customer release once signing
succeeds.
