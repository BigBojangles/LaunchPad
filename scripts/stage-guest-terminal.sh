#!/usr/bin/env bash
# Repair only a disposable child; retain every runtime/session backing image.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
runtime=$(realpath -- "$project/../build-launch-qemu")
template=$(realpath -- "${1:?reviewed runtime required}")
expected=${2:?reviewed runtime SHA256 required}
report=$(realpath -m -- "${3:?new private output directory required}")
case "$template" in "$runtime"/images/*.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*) ;; *) exit 2 ;; esac
[[ "$expected" =~ ^[0-9a-f]{64}$ ]]
test ! -e "$report"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
mkdir -p "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$template" -i <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-before.sh"
EOF
python3 "$project/scripts/repair-guest-terminal.py" "$report/bl-proof-before.sh" "$report/bl-proof.sh"
perl -c "$report/bl-proof.sh"
candidate="$report/template.qcow2"
qemu-img create -f qcow2 -F qcow2 -b "$(realpath --relative-to="$report" "$template")" "$candidate"
guestfish --rw -a "$candidate" -i <<EOF
upload "$report/bl-proof.sh" /usr/local/bin/bl-proof.sh
chmod 0750 /usr/local/bin/bl-proof.sh
chown 0 0 /usr/local/bin/bl-proof.sh
EOF
guestfish --ro -a "$candidate" -i <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-installed.sh"
EOF
cmp "$report/bl-proof.sh" "$report/bl-proof-installed.sh"
qemu-img check "$candidate" > "$report/qcow-check.txt"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
sha256sum "$template" "$candidate" "$report/bl-proof.sh" > "$report/identities.sha256"
printf 'Terminal repair staged only in a disposable child; runtime untouched.\n'
