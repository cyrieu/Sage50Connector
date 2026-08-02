#!/usr/bin/env bash
# Build on the Windows VM, sign on this Mac, and move the immutable artifacts
# back and forth over SSH/SCP. Azure credentials never need to be stored on the VM.
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/../../../.." && pwd)
host="${SAGE50_SSH_HOST:-<SAGE50_VM_HOST>}"
user="${SAGE50_SSH_USER:-<ssh-user>}"
key="${SAGE50_SSH_KEY:-$HOME/.ssh/<ssh-key-name>}"
remote="$user@$host"
subscription='<SAGE50_SIGNING_SUBSCRIPTION>'
endpoint='https://eus.codesigning.azure.net'
alias='<SAGE50_SIGNING_ACCOUNT>/<SAGE50_SIGNING_CERTIFICATE_PROFILE>'
ssh_opts=(-i "$key" -o BatchMode=yes -o ConnectTimeout=10)

for command in az jsign ssh scp; do
  command -v "$command" >/dev/null || { echo "missing required command: $command" >&2; exit 1; }
done
[ -f "$key" ] || { echo "SSH key not found: $key" >&2; exit 1; }

scp "${ssh_opts[@]}" "$script_dir/stop-for-release.ps1" \
  "$remote:C:/Users/<SAGE50_SSH_USER>/sage50-stop-for-release.ps1"
ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\Users\\<SAGE50_SSH_USER>\\sage50-stop-for-release.ps1"
ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\src\\Sage50Connector\\.claude\\skills\\sage50-iterate\\scripts\\build.ps1"

head=$(ssh "${ssh_opts[@]}" "$remote" \
  "powershell.exe -NoProfile -Command \"Set-Location C:\\src\\Sage50Connector; git rev-parse --short HEAD\"" | tr -d '\r')
release_dir="$repo_root/artifacts/sage50-release-$head"
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
