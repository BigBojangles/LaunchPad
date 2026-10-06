#!/usr/bin/env bash
# Read a stopped disposable application fixture; never open user session disks.
set -euo pipefail
overlay=$(realpath -- "${1:?stopped diagnostic overlay required}")
template=$(realpath -- "${2:?recorded backing image required}")
report=$(realpath -m -- "${3:?new private evidence directory required}")
project=$(realpath -- "$(dirname -- "$0")/..")
case "$overlay" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;; *) exit 2 ;; esac
case "$template" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*/template.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
test ! -e "$report"
mkdir -p "$report"
# Only diagnostic metadata changes, preserving the same recorded backing bytes.
# Relative names allow this fixture to remain readable in Windows and WSL.
qemu-img info --backing-chain --output=json "$template" > "$report/candidate-chain-private.json"
qemu-img rebase -u -f qcow2 -F qcow2 -b "$(realpath --relative-to="$(dirname -- "$overlay")" "$template")" "$overlay"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$overlay" -i <<EOF > "$report/home-tree-private.txt"
find /home/builder
EOF
if guestfish --ro -a "$overlay" -i is-dir /home/builder/.claude/debug | grep -qx true; then
    guestfish --ro -a "$overlay" -i <<EOF
copy-out /home/builder/.claude/debug "$report"
EOF
fi
if guestfish --ro -a "$overlay" -i is-dir /home/builder/.codex/log | grep -qx true; then
    guestfish --ro -a "$overlay" -i <<EOF
copy-out /home/builder/.codex/log "$report"
EOF
fi
guestfish --ro -a "$overlay" -i <<EOF > "$report/codex-daemon-private.txt"
is-file /home/builder/.codex/packages/app-server-daemon/current/bin/codex
statns /home/builder/.codex/packages/app-server-daemon/current/bin/codex
EOF
printf 'Disposable guest application evidence collected read-only.\n'
