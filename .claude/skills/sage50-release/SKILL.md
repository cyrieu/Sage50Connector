---
name: sage50-release
description: Build, Authenticode-sign, package, and verify a customer-ready Sage 50 Connector EXE and MSI using the Windows VM over SSH and Rutter's Azure Artifact Signing account. Use only when the user explicitly asks for a signed release, signed installer, customer distributable, or Artifact Signing. Do not use for ordinary development builds, rebuilds, testing, iteration, or validation.
---

# Release the Sage 50 Connector

Produce one immutable signed EXE and MSI from the pushed tip of
`rutter/productionize-v1`. Build and package on the Windows VM, but authenticate
to Azure and sign on the Mac.

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
- SSH works with `~/.ssh/<ssh-key-name>` to `<SAGE50_SSH_USER>@<SAGE50_VM_HOST>`.
- `az`, `jsign`, `ssh`, and `scp` are installed on the Mac.
- The Mac's Azure CLI identity is authenticated and can use the
  `<SAGE50_SIGNING_SUBSCRIPTION>` subscription.
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

## Verify and report

Success requires all of these:

- The script exits zero and prints `SIGNED RELEASE OK`.
- Windows reports `status: Valid` for both the EXE and MSI.
- The signer is `Rutter API` and each artifact has a Microsoft timestamp.
- The local and VM SHA-256 values agree.
- The output directory is `artifacts/sage50-release-<git-sha>/` and contains
  `Sage50Connector.exe` and `RutterSage50ConnectorSetup.msi`.

Report the source commit, absolute artifact directory, both SHA-256 checksums,
and signature status. Remind the user that a newly signed EXE has a new MD5, so
Sage approval must be performed against this exact shipping binary.

Do not install, distribute, or start the release unless the user separately
requests that action.
