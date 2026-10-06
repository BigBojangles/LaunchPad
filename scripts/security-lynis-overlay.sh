#!/usr/bin/env bash
# Stage or collect system-audit setup ONLY in a disposable TestResults overlay.
set -euo pipefail
mode=${1:?stage or collect required}
overlay=$(realpath -- "${2:?disposable overlay required}")
archive=$(realpath -- "${3:?pinned Lynis archive required}")
template=$(realpath -- "${4:?exact backing template required}")
report=${5:?private evidence directory required}
project=$(realpath -- "$(dirname -- "$0")/..")
mkdir -p "$report"
report=$(realpath -- "$report")
case "$overlay" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
test "$overlay" != "$template"
test "$(sha256sum "$archive" | cut -d' ' -f1)" = 'b315c848323572500225312de7e9a3bf3b0c462f5b2d6f4ff56fe6ae521ad169'
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
if [ "$mode" = stage ]; then
    backing=$(realpath --relative-to="$(dirname -- "$overlay")" "$template")
    qemu-img rebase -u -f qcow2 -F qcow2 -b "$backing" "$overlay"
    cat > "$report/launchpad-lynis-audit.service" <<'UNIT'
[Unit]
Description=LaunchPad disposable guest system audit
After=local-fs.target apparmor.service
Wants=apparmor.service
[Service]
Type=oneshot
ExecStart=/bin/sh /opt/launchpad-lynis-audit/audit.sh
TimeoutStartSec=600
[Install]
WantedBy=multi-user.target
UNIT
    guestfish --rw -a "$overlay" -i <<EOF
mkdir-p /opt/launchpad-lynis-audit
upload "$archive" /opt/launchpad-lynis-audit/lynis.tar.gz
upload "$project/scripts/security-lynis-guest.sh" /opt/launchpad-lynis-audit/audit.sh
upload "$report/launchpad-lynis-audit.service" /etc/systemd/system/launchpad-lynis-audit.service
ln-s /etc/systemd/system/launchpad-lynis-audit.service /etc/systemd/system/multi-user.target.wants/launchpad-lynis-audit.service
EOF
elif [ "$mode" = collect ]; then
    guestfish --ro -a "$overlay" -i <<EOF
download /var/tmp/launchpad-lynis-audit/identity.txt "$report/identity.txt"
download /var/tmp/launchpad-lynis-audit/runtime-ready.txt "$report/runtime-ready.txt"
download /var/tmp/launchpad-lynis-audit/runtime-kernel.txt "$report/runtime-kernel.txt"
download /var/tmp/launchpad-lynis-audit/apparmor-profiles-private.txt "$report/apparmor-profiles-private.txt"
download /var/tmp/launchpad-lynis-audit/apparmor-unavailable.txt "$report/apparmor-unavailable.txt"
download /var/tmp/launchpad-lynis-audit/netfilter-private.txt "$report/netfilter-private.txt"
download /var/tmp/launchpad-lynis-audit/netfilter-unavailable.txt "$report/netfilter-unavailable.txt"
download /var/tmp/launchpad-lynis-audit/pin-check.txt "$report/pin-check.txt"
download /var/tmp/launchpad-lynis-audit/exit.txt "$report/exit.txt"
download /var/tmp/launchpad-lynis-audit/lynis.log "$report/lynis.log"
download /var/tmp/launchpad-lynis-audit/lynis.dat "$report/lynis.dat"
download /var/tmp/launchpad-lynis-audit/console-private.txt "$report/console-private.txt"
EOF
else
    exit 2
fi
