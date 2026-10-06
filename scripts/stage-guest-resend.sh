#!/usr/bin/env bash
# Stage only a new owned overlay; never alter a template or existing session.
set -euo pipefail
parent=$(realpath -- "${1:?reviewed candidate required}")
expected=${2:?candidate SHA256 required}
repair=$(realpath -- "${3:?staged receiver required}")
report=$(realpath -m -- "${4:?new private report directory required}")
project=$(realpath -- "$(dirname -- "$0")/..")
case "$parent" in "$project"/tests/LaunchPad.Tests/TestResults/*/template.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
test "$(sha256sum "$parent" | cut -d' ' -f1)" = "$expected"
test ! -e "$report"
perl -c "$repair"
mkdir -p "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
candidate="$report/template.qcow2"
qemu-img create -f qcow2 -F qcow2 -b "$(realpath --relative-to="$report" "$parent")" "$candidate"
guestfish --rw -a "$candidate" -i <<EOF
upload "$repair" /usr/local/bin/bl-proof.sh
chmod 0755 /usr/local/bin/bl-proof.sh
EOF
guestfish --ro -a "$candidate" -i <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-verified.sh"
EOF
cmp "$repair" "$report/bl-proof-verified.sh"
qemu-img check "$candidate" > "$report/qcow-check.txt"
test "$(sha256sum "$parent" | cut -d' ' -f1)" = "$expected"
sha256sum "$parent" "$candidate" "$repair" > "$report/identity.sha256"
printf 'Staged resend candidate; parent template and existing sessions unchanged.\n'
