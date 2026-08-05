---
name: sage50-release
description: Build, Authenticode-sign, package, and verify a customer-ready Sage 50 Connector EXE and MSI using the Windows VM over SSH and Rutter's Azure Artifact Signing account. Use only when the user explicitly asks for a signed release, signed installer, customer distributable, or Artifact Signing. Do not use for ordinary development builds, rebuilds, testing, iteration, or validation.
---

# Release the Sage 50 Connector

Produce one immutable signed EXE and MSI from the pushed tip of
`rutter/productionize-v1`. Build and package on the Windows VM, but authenticate
to Azure and sign on the Mac. After verification, upload the installer to the
public S3 object that Rutter Link / rutter-backend redirects customers to during
Sage 50 setup.

## Invocation gate

Run this workflow only after an explicit request for a **signed** release or
customer distributable. A request to build, rebuild, test, iterate, deploy to
the development VM, or produce an MSI is not authorization to sign. For those
requests, use the unsigned development workflow in `$sage50-iterate` and stop
after build/test verification.

Read `CLAUDE.md` at the repository root before releasing. Treat its signing,
Sage approval, and installer requirements as authoritative.

## Preconditions

Confirm all of the following:

- The working tree is clean and the intended commit is pushed to
  `origin/rutter/productionize-v1`.
- Lab SSH env is set and works: `SAGE50_SSH_HOST`, `SAGE50_SSH_USER`,
  `SAGE50_SSH_KEY`. If SSH times out, the VM may be deallocated — start it
  first with `az vm start` using `SAGE50_VM_RG` / `SAGE50_VM_NAME` /
  `SAGE50_VM_SUBSCRIPTION`.
- Signing env is set: `SAGE50_SIGNING_SUBSCRIPTION`,
  `SAGE50_SIGNING_ENDPOINT`, `SAGE50_SIGNING_ACCOUNT`,
  `SAGE50_SIGNING_CERTIFICATE_PROFILE`.
- `az`, `jsign`, `ssh`, `scp`, and `aws` are installed on the Mac.
- The Mac's Azure CLI identity is authenticated for the signing subscription.
- AWS can write the public installer object (use whatever profile/credentials
  your org uses for that bucket — do not commit profile names):
  `aws s3 ls s3://rutterpublicimages/ --region us-east-2`.
- No release directory already exists for the current commit. Never overwrite,
  rebuild, modify, or re-sign an existing release.

Do not print Azure access tokens, connector access keys, service passwords, or
other credentials.

## Create the release

From the repository root, run:

```bash
.claude/skills/sage50-iterate/scripts/release-via-ssh.sh
```

Use this script as the release implementation; do not reproduce its signing
commands manually. It performs the required order:

1. Stop the connector on the VM.
2. Reset the VM checkout to the pushed branch and build x86 Release artifacts.
3. Copy the unsigned EXE to the Mac and sign it with Azure Artifact Signing.
4. Return the signed EXE and package the MSI without rebuilding project references.
5. Copy the MSI to the Mac and sign it.
6. Return both signed artifacts to the VM and verify Windows Authenticode.
7. Print SHA-256 checksums and retain the signed files locally.

If MSBuild reports `MSB4166` because a parallel worker exited, first confirm
that no MSBuild process is stuck and that the VM has adequate free memory. A
single clean retry is acceptable only if signing had not started. Do not retry
after either artifact was signed; investigate instead to avoid replacing an
immutable release.

## Verify the signed artifacts

Success requires all of these:

- The script exits zero and prints `SIGNED RELEASE OK`.
- Windows reports `status: Valid` for both the EXE and MSI.
- The signer is `Rutter API` and each artifact has a Microsoft timestamp.
- The local and VM SHA-256 values agree.
- The output directory is `artifacts/sage50-release-<git-sha>/` and contains
  `Sage50Connector.exe` and `RutterSage50ConnectorSetup.msi`.

## Upload to the Sage 50 auth-flow installer URL

Link and rutter-backend do **not** host the MSI themselves. During setup:

1. Link calls `POST /sage-50/setup-session`.
2. The response's `installer_url` is `{backend}/sage-50/installer`.
3. That route **302-redirects** to `SAGE_50_INSTALLER_URL`, defaulting to:

```text
https://rutterpublicimages.s3.us-east-2.amazonaws.com/Sage+50+Connector+Installer.zip
```

Source of truth in rutter-backend:

```text
src/platformization/platforms/sage_50/routes/index.ts
  DEFAULT_SAGE_50_INSTALLER_URL
  getSage50InstallerUrl()
  GET /sage-50/installer  →  res.redirect(...)
```

rutter-link-web only consumes `installer_url` from that setup response
(`src/stores/Sage50.tsx`); it does not hardcode the S3 path. Production's
`SAGE_50_INSTALLER_URL` env, if set, overrides the default — confirm prod still
points at this object (or update the upload target to match) before shipping.

After the signed release verifies, publish the new MSI to that object. **Do not
upload until the release script has printed `SIGNED RELEASE OK`.**

From the release directory:

```bash
release_dir=artifacts/sage50-release-<git-sha>
msi="$release_dir/RutterSage50ConnectorSetup.msi"
zip_path="$release_dir/Sage 50 Connector Installer.zip"

# Customers download a zip; keep the product MSI at the root of the archive.
rm -f "$zip_path"
zip -j "$zip_path" "$msi"

# Use an AWS identity that can write the public installer bucket.
aws s3 cp "$zip_path" \
  "s3://rutterpublicimages/Sage 50 Connector Installer.zip" \
  --region us-east-2 \
  --content-type application/zip \
  --metadata "git-sha=<full-or-short-sha>,product=RutterSage50ConnectorSetup.msi"

# Confirm the public redirect target serves the new bytes.
curl -sI "https://rutterpublicimages.s3.us-east-2.amazonaws.com/Sage+50+Connector+Installer.zip" \
  | grep -iE 'HTTP/|Last-Modified|Content-Length|ETag'
shasum -a 256 "$zip_path" "$msi"
```

Notes:

- The S3 key is exactly `Sage 50 Connector Installer.zip` (spaces in the key;
  URL-encoded as `Sage+50+Connector+Installer.zip`).
- Overwriting this key updates production downloads immediately for every
  customer who hits `/sage-50/installer`. Treat it as a production publish.
- The previous object was an older Debug-era zip (`Debug/Sage50ConnectorSetup.msi`
  + `setup.exe`). The current product ships a single signed
  `RutterSage50ConnectorSetup.msi` at the zip root — that is intentional.
- Do not commit the zip or MSI into git; leave them under `artifacts/`.
- If prod uses a non-default `SAGE_50_INSTALLER_URL`, upload there instead (or
  change the env to this default and upload here). Do not leave Link pointing at
  a stale object after a customer release.

## Report

Report the source commit, absolute artifact directory, both SHA-256 checksums
(EXE + MSI), signature status, and the S3 upload confirmation (key, region,
new `Last-Modified` / `ETag` / size). Remind the user that a newly signed EXE
has a new MD5, so Sage approval must be performed against this exact shipping
binary.

Do not install the release on the VM or start the connector unless the user
separately requests that action. Uploading the installer zip **is** part of a
customer release once signing succeeds — it is the distribution step for the
auth flow.
