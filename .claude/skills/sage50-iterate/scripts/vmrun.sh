#!/usr/bin/env bash
# Run a PowerShell script on the Sage 50 VM via az vm run-command.
#
# Handles the two things that always go wrong doing this by hand:
#   - the script must reach the VM with CRLF line endings (the repo stores LF)
#   - only one run-command may execute at a time; a second returns Conflict,
#     and a timed-out extension wedges the channel for minutes
#
# Usage: vmrun.sh <script.ps1>
set -euo pipefail

RG="${SAGE50_VM_RG:-MICROSOFTGREATPLAINS_GROUP}"
VM="${SAGE50_VM_NAME:-microsoftgreatplains}"
# Pin the subscription. `az login` can leave a different one as default (the
# signing-certificate subscription, in this account), and every call then fails
# with AuthorizationFailed naming a subscription that has nothing to do with
# this VM - which reads like an expired token rather than a wrong default.
SUB="${SAGE50_VM_SUBSCRIPTION:-Azure subscription 1}"

[ $# -eq 1 ] || { echo "usage: $(basename "$0") <script.ps1>" >&2; exit 2; }
[ -f "$1" ] || { echo "no such script: $1" >&2; exit 2; }

crlf=$(mktemp -t vmrun).ps1
# shellcheck disable=SC2064
trap "rm -f '$crlf'" EXIT
perl -pe 's/\r?\n/\r\n/' "$1" > "$crlf"

# Wait out any in-flight run-command rather than failing with Conflict.
for _ in $(seq 1 40); do
  if out=$(az vm run-command invoke -g "$RG" -n "$VM" --subscription "$SUB" \
        --command-id RunPowerShellScript --scripts @"$crlf" \
        -o tsv --query "value[0].message" 2>&1); then
    printf '%s\n' "$out"
    exit 0
  fi
  case "$out" in
    *Conflict*|*"in progress"*) sleep 15 ;;
    *) printf '%s\n' "$out" >&2; exit 1 ;;
  esac
done

echo "run-command stayed locked; the extension is probably wedged. Wait a few minutes." >&2
exit 1
