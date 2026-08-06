#!/usr/bin/env bash
# Publish a signed Sage 50 connector release for customer install + assisted
# auto-update:
#   1. Versioned immutable MSI on S3
#   2. release.json manifest (tray Check for updates)
#   3. Link first-install zip (latest MSI only)
#
# Usage:
#   publish-release.sh <release_dir>
#   publish-release.sh artifacts/sage50-release-<short-sha>
#
# release_dir must contain:
#   Sage50Connector.exe
#   RutterSage50ConnectorSetup.msi
#
# Required:
#   AWS credentials that can write s3://rutterpublicimages/ (or override bucket)
# Optional env:
#   SAGE50_PUBLISH_BUCKET          (default: rutterpublicimages)
#   SAGE50_PUBLISH_REGION          (default: us-east-2)
#   SAGE50_RELEASE_NOTES           (customer-facing notes in release.json)
#   SAGE50_MIN_VERSION             (default: 1.0.0)
#   SAGE50_SKIP_LINK_ZIP           (set to 1 to skip the Link installer zip)
#   SAGE50_SKIP_VERSIONED_MSI      (set to 1 to only update zip + manifest)
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/../../../.." && pwd)

release_dir=${1:-}
if [ -z "$release_dir" ]; then
  echo "usage: $(basename "$0") <release_dir>" >&2
  exit 2
fi
# Resolve relative paths from repo root for convenience.
case "$release_dir" in
  /*) ;;
  *) release_dir="$repo_root/$release_dir" ;;
esac
[ -d "$release_dir" ] || { echo "release dir not found: $release_dir" >&2; exit 1; }

exe="$release_dir/Sage50Connector.exe"
msi="$release_dir/RutterSage50ConnectorSetup.msi"
[ -f "$exe" ] || { echo "missing EXE: $exe" >&2; exit 1; }
[ -f "$msi" ] || { echo "missing MSI: $msi" >&2; exit 1; }

for command in aws shasum zip jq; do
  if ! command -v "$command" >/dev/null 2>&1; then
    # jq is optional — we can write JSON without it
    if [ "$command" = jq ]; then
      continue
    fi
    echo "missing required command: $command" >&2
    exit 1
  fi
done

# Version from Version.props (source of truth for this build)
props="$repo_root/Version.props"
[ -f "$props" ] || { echo "Version.props not found: $props" >&2; exit 1; }

read_prop() {
  # <Sage50ConnectorMsiVersion>1.1.0</Sage50ConnectorMsiVersion>
  local name=$1
  sed -n "s/.*<$name>\\([^<]*\\)<\\/$name>.*/\\1/p" "$props" | head -1 | tr -d '[:space:]'
}

msi_version=$(read_prop Sage50ConnectorMsiVersion)
asm_version=$(read_prop Sage50ConnectorVersion)
[ -n "$msi_version" ] || { echo "could not read Sage50ConnectorMsiVersion from Version.props" >&2; exit 1; }
[ -n "$asm_version" ] || { echo "could not read Sage50ConnectorVersion from Version.props" >&2; exit 1; }

# AssemblyInfo must match Version.props (release skill checks this before build too)
assembly_info="$repo_root/Properties/AssemblyInfo.cs"
if [ -f "$assembly_info" ]; then
  if ! grep -q "AssemblyFileVersion(\"$asm_version\")" "$assembly_info"; then
    echo "AssemblyInfo.cs FileVersion does not match Version.props ($asm_version)" >&2
    echo "Bump AssemblyInfo before releasing." >&2
    exit 1
  fi
fi

bucket="${SAGE50_PUBLISH_BUCKET:-rutterpublicimages}"
region="${SAGE50_PUBLISH_REGION:-us-east-2}"
min_version="${SAGE50_MIN_VERSION:-1.0.0}"
notes="${SAGE50_RELEASE_NOTES:-}"
git_sha=$(git -C "$repo_root" rev-parse HEAD 2>/dev/null || echo unknown)
git_short=$(git -C "$repo_root" rev-parse --short HEAD 2>/dev/null || echo unknown)
released_at=$(date -u +%Y-%m-%d)

msi_sha=$(shasum -a 256 "$msi" | awk '{print $1}')
exe_sha=$(shasum -a 256 "$exe" | awk '{print $1}')

versioned_key="sage50-connector/RutterSage50ConnectorSetup-${msi_version}.msi"
manifest_key="sage50-connector/release.json"
link_zip_key="Sage 50 Connector Installer.zip"
public_base="https://${bucket}.s3.${region}.amazonaws.com"
# Space-safe URL for the versioned MSI (no spaces in key)
msi_public_url="${public_base}/${versioned_key}"

echo "=== publish release ==="
echo "  version (MSI):  $msi_version"
echo "  version (asm):  $asm_version"
echo "  git:            $git_short"
echo "  MSI sha256:     $msi_sha"
echo "  EXE sha256:     $exe_sha"
echo "  bucket:         s3://$bucket ($region)"

if [ "${SAGE50_SKIP_VERSIONED_MSI:-0}" != "1" ]; then
  echo "Uploading versioned MSI → s3://$bucket/$versioned_key"
  aws s3 cp "$msi" "s3://$bucket/$versioned_key" \
    --region "$region" \
    --content-type application/octet-stream \
    --metadata "git-sha=$git_sha,version=$msi_version,product=RutterSage50ConnectorSetup.msi"
else
  echo "Skipping versioned MSI upload (SAGE50_SKIP_VERSIONED_MSI=1)"
  # Still need a public URL for the manifest — assume already published
  msi_public_url="${public_base}/${versioned_key}"
fi

# Write manifest into the release dir (immutable local record)
manifest_path="$release_dir/release.json"
if command -v jq >/dev/null 2>&1; then
  jq -n \
    --arg version "$msi_version" \
    --arg min_version "$min_version" \
    --arg msi_url "$msi_public_url" \
    --arg sha256 "$msi_sha" \
    --arg released_at "$released_at" \
    --arg notes "$notes" \
    --arg git_sha "$git_sha" \
    --arg exe_sha256 "$exe_sha" \
    '{
      version: $version,
      min_version: $min_version,
      msi_url: $msi_url,
      sha256: $sha256,
      released_at: $released_at,
      notes: $notes,
      requires_sage_reapproval: true,
      git_sha: $git_sha,
      exe_sha256: $exe_sha256
    }' > "$manifest_path"
else
  # Minimal JSON without jq (escape notes crudely)
  notes_escaped=${notes//\\/\\\\}
  notes_escaped=${notes_escaped//\"/\\\"}
  notes_escaped=${notes_escaped//$'\n'/\\n}
  cat > "$manifest_path" <<EOF
{
  "version": "${msi_version}",
  "min_version": "${min_version}",
  "msi_url": "${msi_public_url}",
  "sha256": "${msi_sha}",
  "released_at": "${released_at}",
  "notes": "${notes_escaped}",
  "requires_sage_reapproval": true,
  "git_sha": "${git_sha}",
  "exe_sha256": "${exe_sha}"
}
EOF
fi

echo "Uploading update manifest → s3://$bucket/$manifest_key"
aws s3 cp "$manifest_path" "s3://$bucket/$manifest_key" \
  --region "$region" \
  --content-type application/json \
  --cache-control "max-age=60" \
  --metadata "git-sha=$git_sha,version=$msi_version"

if [ "${SAGE50_SKIP_LINK_ZIP:-0}" != "1" ]; then
  zip_path="$release_dir/Sage 50 Connector Installer.zip"
  rm -f "$zip_path"
  # Customers download a zip; product MSI at the root of the archive.
  (cd "$release_dir" && zip -j "Sage 50 Connector Installer.zip" "RutterSage50ConnectorSetup.msi")
  echo "Uploading Link installer zip → s3://$bucket/$link_zip_key"
  aws s3 cp "$zip_path" "s3://$bucket/$link_zip_key" \
    --region "$region" \
    --content-type application/zip \
    --metadata "git-sha=$git_sha,version=$msi_version,product=RutterSage50ConnectorSetup.msi"
else
  echo "Skipping Link installer zip (SAGE50_SKIP_LINK_ZIP=1)"
fi

# Write a local publish summary
cat > "$release_dir/PUBLISH.txt" <<EOF
version=$msi_version
assembly_version=$asm_version
git_sha=$git_sha
msi_sha256=$msi_sha
exe_sha256=$exe_sha
versioned_msi_s3=s3://$bucket/$versioned_key
versioned_msi_url=$msi_public_url
manifest_s3=s3://$bucket/$manifest_key
manifest_url=${public_base}/sage50-connector/release.json
link_zip_s3=s3://$bucket/$link_zip_key
released_at=$released_at
requires_sage_reapproval=true
EOF

echo ""
echo "=== verify public URLs ==="
curl -sI "${public_base}/sage50-connector/release.json" | grep -iE 'HTTP/|Last-Modified|Content-Length|ETag|Content-Type' || true
curl -sI "$msi_public_url" | grep -iE 'HTTP/|Last-Modified|Content-Length|ETag' || true

echo ""
echo "PUBLISH OK"
echo "  manifest: ${public_base}/sage50-connector/release.json"
echo "  MSI:      $msi_public_url"
echo "  local:    $release_dir"
echo ""
echo "NOTE: Customers who install this EXE must re-approve in Sage 50"
echo "      (grant is MD5 of the executable)."
