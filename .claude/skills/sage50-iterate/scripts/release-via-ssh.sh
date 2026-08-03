#!/usr/bin/env bash
# Build on the Windows VM, sign on this Mac, and move the immutable artifacts
# back and forth over SSH/SCP. Azure credentials never need to be stored on the VM.
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/../../../.." && pwd)
host="${SAGE50_SSH_HOST:-20.51.189.20}"
user="${SAGE50_SSH_USER:-RutterAdmin}"
key="${SAGE50_SSH_KEY:-$HOME/.ssh/tally_azure}"
remote="$user@$host"
subscription='Azure Signing Certificate'
endpoint='https://eus.codesigning.azure.net'
alias='RutterSigning/DynamicsCertificate'
ssh_opts=(-i "$key" -o BatchMode=yes -o ConnectTimeout=10)

for command in az jsign ssh scp; do
  command -v "$command" >/dev/null || { echo "missing required command: $command" >&2; exit 1; }
done
[ -f "$key" ] || { echo "SSH key not found: $key" >&2; exit 1; }

[ -z "$(git -C "$repo_root" status --porcelain)" ] || {
  echo 'working tree is not clean; commit or stash changes before releasing' >&2
  exit 1
}
expected_head=$(git -C "$repo_root" rev-parse HEAD)
expected_remote_head=$(git -C "$repo_root" rev-parse origin/rutter/productionize-v1)
[ "$expected_head" = "$expected_remote_head" ] || {
  echo 'HEAD does not match origin/rutter/productionize-v1; push the intended release first' >&2
  exit 1
}
expected_short=$(git -C "$repo_root" rev-parse --short HEAD)
release_dir="$repo_root/artifacts/sage50-release-$expected_short"
[ ! -e "$release_dir" ] || {
  echo "release already exists and will not be overwritten: $release_dir" >&2
  exit 1
}

scp "${ssh_opts[@]}" "$script_dir/stop-for-release.ps1" \
  "$remote:C:/Users/RutterAdmin/sage50-stop-for-release.ps1"
ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\Users\\RutterAdmin\\sage50-stop-for-release.ps1"
ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\src\\Sage50Connector\\.claude\\skills\\sage50-iterate\\scripts\\build.ps1"

head=$(ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -Command \"Set-Location C:\\src\\Sage50Connector; git rev-parse --short HEAD\"" | tr -d '\r')
[ "$head" = "$expected_short" ] || {
  echo "VM built unexpected commit: expected $expected_short, got $head" >&2
  exit 1
}
mkdir -p "$release_dir"

exe="$release_dir/Sage50Connector.exe"
msi="$release_dir/RutterSage50ConnectorSetup.msi"
scp "${ssh_opts[@]}" "$remote:C:/src/Sage50Connector/bin/Release/Sage50Connector.exe" "$exe"

previous_subscription=$(az account show --query id -o tsv)
trap 'unset signing_token; az account set --subscription "$previous_subscription" >/dev/null 2>&1 || true' EXIT
az account set --subscription "$subscription"
signing_token=$(az account get-access-token \
  --resource https://codesigning.azure.net \
  --query accessToken \
  --output tsv)

jsign --storetype TRUSTEDSIGNING \
  --keystore "$endpoint" \
  --storepass "$signing_token" \
  --alias "$alias" \
  --name 'Rutter Sage 50 Connector' \
  "$exe"

scp "${ssh_opts[@]}" "$exe" \
  "$remote:C:/src/Sage50Connector/bin/Release/Sage50Connector.exe.signed"
ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\src\\Sage50Connector\\.claude\\skills\\sage50-iterate\\scripts\\package-signed-release.ps1"

scp "${ssh_opts[@]}" \
  "$remote:C:/src/Sage50Connector/Sage50ConnectorSetup/bin/Release/RutterSage50ConnectorSetup.msi" \
  "$msi"
jsign --storetype TRUSTEDSIGNING \
  --keystore "$endpoint" \
  --storepass "$signing_token" \
  --alias "$alias" \
  --name 'Rutter Sage 50 Connector' \
  "$msi"

scp "${ssh_opts[@]}" "$msi" \
  "$remote:C:/src/Sage50Connector/Sage50ConnectorSetup/bin/Release/RutterSage50ConnectorSetup.msi.signed"
ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\src\\Sage50Connector\\.claude\\skills\\sage50-iterate\\scripts\\finalize-signed-release.ps1"

shasum -a 256 "$exe" "$msi"
echo "SIGNED RELEASE OK: $release_dir"
