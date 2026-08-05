#!/usr/bin/env bash
# Build on the Windows VM, sign on this Mac, and move the immutable artifacts
# back and forth over SSH/SCP. Azure credentials never need to be stored on the VM.
#
# Required env (no defaults — keep lab infrastructure out of the public tree):
#   SAGE50_SSH_HOST
#   SAGE50_SSH_USER
#   SAGE50_SSH_KEY
#   SAGE50_SIGNING_SUBSCRIPTION
#   SAGE50_SIGNING_ENDPOINT
#   SAGE50_SIGNING_ACCOUNT
#   SAGE50_SIGNING_CERTIFICATE_PROFILE
# Optional:
#   SAGE50_SSH_REMOTE_WIN_HOME  (default: C:/Users/$SAGE50_SSH_USER)
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/../../../.." && pwd)

: "${SAGE50_SSH_HOST:?Set SAGE50_SSH_HOST to the lab VM host or IP}"
: "${SAGE50_SSH_USER:?Set SAGE50_SSH_USER to the SSH username on the lab VM}"
: "${SAGE50_SSH_KEY:?Set SAGE50_SSH_KEY to the path of the SSH private key}"
: "${SAGE50_SIGNING_SUBSCRIPTION:?Set SAGE50_SIGNING_SUBSCRIPTION}"
: "${SAGE50_SIGNING_ENDPOINT:?Set SAGE50_SIGNING_ENDPOINT (Azure Trusted Signing endpoint URL)}"
: "${SAGE50_SIGNING_ACCOUNT:?Set SAGE50_SIGNING_ACCOUNT}"
: "${SAGE50_SIGNING_CERTIFICATE_PROFILE:?Set SAGE50_SIGNING_CERTIFICATE_PROFILE}"

host="$SAGE50_SSH_HOST"
user="$SAGE50_SSH_USER"
key="$SAGE50_SSH_KEY"
remote="$user@$host"
subscription="$SAGE50_SIGNING_SUBSCRIPTION"
endpoint="$SAGE50_SIGNING_ENDPOINT"
alias="$SAGE50_SIGNING_ACCOUNT/$SAGE50_SIGNING_CERTIFICATE_PROFILE"
remote_win_home="${SAGE50_SSH_REMOTE_WIN_HOME:-C:/Users/$user}"
# PowerShell path form of the staging directory (backslashes).
remote_win_home_ps="${remote_win_home//\//\\}"
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
  "$remote:$remote_win_home/sage50-stop-for-release.ps1"
ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $remote_win_home_ps\\sage50-stop-for-release.ps1"
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
