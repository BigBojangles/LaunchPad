#!/usr/bin/env bash
# New disposable candidate only. No selected template/session/rebuild mutation.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
runtime=$(realpath -- "$project/../build-launch-qemu")
template=$(realpath -- "${1:?exact selected image required}")
expected=${2:?selected image SHA256 required}
report=$(realpath -m -- "${3:?new private output directory required}")
case "$template" in "$runtime"/images/*.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*) ;; *) exit 2 ;; esac
test ! -e "$report"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
mkdir -p "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$template" -i <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-before.sh"
download /etc/apparmor.d/usr.local.bin.grok "$report/profile-before"
download /etc/systemd/system/bl-proof.service "$report/unit-before.service"
is-file /usr/bin/setpriv
EOF
python3 "$project/scripts/repair-guest-policy.py" "$report"
cp -- "$project/scripts/guest-agent-entry.pl" "$report/launchpad-agent"
cp -- "$project/scripts/guest-quiesce.py" "$report/quiesce.py"
perl -c "$report/bl-proof.sh"
perl -c "$report/launchpad-agent"
candidate="$report/template.qcow2"
qemu-img create -f qcow2 -F qcow2 -b "$(realpath --relative-to="$report" "$template")" "$candidate"
guestfish --rw -a "$candidate" -i <<EOF
mkdir-p /usr/local/lib/launchpad
upload "$report/quiesce.py" /usr/local/lib/launchpad/quiesce.py
chmod 0750 /usr/local/lib/launchpad/quiesce.py
chown 0 0 /usr/local/lib/launchpad/quiesce.py
upload "$report/bl-proof.sh" /usr/local/bin/bl-proof.sh
upload "$report/launchpad-agent" /usr/local/bin/launchpad-agent
upload "$report/usr.local.bin.grok" /etc/apparmor.d/usr.local.bin.grok
upload "$report/bl-proof.service" /etc/systemd/system/bl-proof.service
chmod 0750 /usr/local/bin/bl-proof.sh
chmod 0755 /usr/local/bin/launchpad-agent
chmod 0644 /etc/apparmor.d/usr.local.bin.grok
chmod 0644 /etc/systemd/system/bl-proof.service
chown 0 0 /usr/local/bin/bl-proof.sh
chown 0 0 /usr/local/bin/launchpad-agent
chown 0 0 /etc/apparmor.d/usr.local.bin.grok
chown 0 0 /etc/systemd/system/bl-proof.service
EOF
virt-customize -a "$candidate" --run-command 'apparmor_parser --skip-kernel-load --skip-cache /etc/apparmor.d/usr.local.bin.grok' > "$report/parser-compile.txt" 2>&1
guestfish --ro -a "$candidate" -i <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-installed.sh"
download /usr/local/bin/launchpad-agent "$report/entry-installed"
download /etc/apparmor.d/usr.local.bin.grok "$report/profile-installed"
download /etc/systemd/system/bl-proof.service "$report/unit-installed.service"
download /usr/local/lib/launchpad/quiesce.py "$report/quiesce-installed.py"
EOF
cmp "$report/bl-proof.sh" "$report/bl-proof-installed.sh"
cmp "$report/launchpad-agent" "$report/entry-installed"
cmp "$report/usr.local.bin.grok" "$report/profile-installed"
cmp "$report/bl-proof.service" "$report/unit-installed.service"
cmp "$report/quiesce.py" "$report/quiesce-installed.py"
qemu-img check "$candidate" > "$report/qcow-check.txt"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
sha256sum "$template" "$candidate" "$report/bl-proof.sh" "$report/launchpad-agent" "$report/usr.local.bin.grok" "$report/bl-proof.service" "$report/quiesce.py" > "$report/identities.sha256"
printf 'Common agent confinement staged only in a disposable child.\n'
