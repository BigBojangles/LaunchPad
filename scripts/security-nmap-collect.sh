#!/usr/bin/env bash
# Collect diagnostic build/scan evidence after the disposable VM has stopped.
set -euo pipefail
overlay=$(realpath -- "${1:?diagnostic overlay required}")
template=$(realpath -- "${2:?exact backing template required}")
report=${3:?private generated report directory required}
project=$(realpath -- "$(dirname -- "$0")/..")
mkdir -p "$report"
report=$(realpath -- "$report")
case "$overlay" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
test "$overlay" != "$template"
backing=$(realpath --relative-to="$(dirname -- "$overlay")" "$template")
qemu-img rebase -u -f qcow2 -F qcow2 -b "$backing" "$overlay"
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
guestfish --ro -a "$overlay" -i <<EOF
download /home/builder/in/project/.lp-nmap-build/configure-private.log "$report/configure-private.log"
download /home/builder/in/project/.lp-nmap-build/make-private.log "$report/make-private.log"
EOF
