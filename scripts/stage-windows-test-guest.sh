#!/usr/bin/env bash
# Offline preparation of a NEW private candidate only. Selected templates and
# session disks are read-only backing inputs, never installation destinations.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
runtime=$(realpath -- "$project/../build-launch-qemu")
template=$(realpath -- "${1:?selected template required}")
expected=${2:?selected SHA256 required}
report=$(realpath -m -- "${3:?new private candidate directory required}")
case "$template" in "$runtime"/images/*.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*) ;; *) exit 2 ;; esac
[[ "$expected" =~ ^[0-9a-f]{64}$ ]]
test ! -e "$report"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
mkdir -p "$report/payload" "$report/installed"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
for name in windows-test-broker.py windows-test-client.py launchpad-windows-test.service; do
    cp -- "$project/scripts/$name" "$report/payload/$name"
done
printf '#!/bin/sh\nexec /usr/bin/python3 /usr/local/lib/launchpad/windows-test-client.py run "$@"\n' > "$report/payload/launchpad-windows-test"
printf '%s\n' 'SUBSYSTEM=="virtio-ports", ATTR{name}=="launchpad-windows-test", OWNER="root", GROUP="root", MODE="0600"' > "$report/payload/launchpad-windows-test.rules"
guestfish --ro -a "$template" -i download /etc/apparmor.d/usr.local.bin.grok "$report/profile-before"
python3 - "$report" <<'PY'
import pathlib, sys
root = pathlib.Path(sys.argv[1])
text = (root / 'profile-before').read_text()
rule = '  /run/launchpad-windows-test/bridge.sock rw,\n'
if rule not in text:
    needle = '  network,\n'
    if text.count(needle) != 1:
        raise SystemExit('Unexpected policy input; preserve for review.')
    text = text.replace(needle, needle + rule)
(root / 'payload/usr.local.bin.grok').write_text(text)
PY
candidate="$report/template.qcow2"
qemu-img create -f qcow2 -F qcow2 -b "$(realpath --relative-to="$report" "$template")" "$candidate"
# Refuse linked installation paths before the first candidate mutation.
guestfish --ro -a "$candidate" -i > "$report/link-check.log" <<'EOF'
is-symlink /usr/local/lib
is-symlink /usr/local/lib/launchpad
is-symlink /usr/local/bin
is-symlink /etc/systemd/system
is-symlink /etc/systemd/system/multi-user.target.wants
is-symlink /etc/apparmor.d
is-symlink /etc/udev/rules.d
is-symlink /usr/local/lib/launchpad/windows-test-broker.py
is-symlink /usr/local/lib/launchpad/windows-test-client.py
is-symlink /usr/local/bin/launchpad-windows-test
is-symlink /etc/systemd/system/launchpad-windows-test.service
is-symlink /etc/apparmor.d/usr.local.bin.grok
is-symlink /etc/udev/rules.d/70-launchpad-windows-test.rules
EOF
test "$(wc -l < "$report/link-check.log")" = 13
test "$(sort -u "$report/link-check.log")" = false
state_commands=''
for path in /home/builder/.local /home/builder/.local/state /home/builder/.local/state/launchpad /home/builder/.local/state/launchpad/windows-tests; do
    test "$(guestfish --ro -a "$candidate" -i is-symlink "$path")" = false
    if [ "$(guestfish --ro -a "$candidate" -i exists "$path")" = false ]; then
        state_commands+="$(printf 'mkdir-p %s\nchown 1000 1000 %s\nchmod 0700 %s\n' "$path" "$path" "$path")"$'\n'
    else
        test "$(guestfish --ro -a "$candidate" -i is-dir "$path")" = true
    fi
done
guestfish --rw -a "$candidate" -i <<EOF
$state_commands
mkdir-p /usr/local/lib/launchpad
upload "$report/payload/windows-test-broker.py" /usr/local/lib/launchpad/windows-test-broker.py
upload "$report/payload/windows-test-client.py" /usr/local/lib/launchpad/windows-test-client.py
upload "$report/payload/launchpad-windows-test" /usr/local/bin/launchpad-windows-test
upload "$report/payload/launchpad-windows-test.service" /etc/systemd/system/launchpad-windows-test.service
upload "$report/payload/launchpad-windows-test.rules" /etc/udev/rules.d/70-launchpad-windows-test.rules
upload "$report/payload/usr.local.bin.grok" /etc/apparmor.d/usr.local.bin.grok
chown 0 0 /usr/local/lib/launchpad
chmod 0755 /usr/local/lib/launchpad
chown 0 0 /usr/local/lib/launchpad/windows-test-broker.py
chmod 0750 /usr/local/lib/launchpad/windows-test-broker.py
chown 0 0 /usr/local/lib/launchpad/windows-test-client.py
chmod 0644 /usr/local/lib/launchpad/windows-test-client.py
chown 0 0 /usr/local/bin/launchpad-windows-test
chmod 0755 /usr/local/bin/launchpad-windows-test
chown 0 0 /etc/systemd/system/launchpad-windows-test.service
chmod 0644 /etc/systemd/system/launchpad-windows-test.service
chown 0 0 /etc/udev/rules.d/70-launchpad-windows-test.rules
chmod 0644 /etc/udev/rules.d/70-launchpad-windows-test.rules
chown 0 0 /etc/apparmor.d/usr.local.bin.grok
chmod 0644 /etc/apparmor.d/usr.local.bin.grok
ln-s /etc/systemd/system/launchpad-windows-test.service /etc/systemd/system/multi-user.target.wants/launchpad-windows-test.service
EOF
virt-customize -a "$candidate" --run-command 'apparmor_parser --skip-kernel-load --skip-cache /etc/apparmor.d/usr.local.bin.grok' > "$report/policy-compile.log" 2>&1
guestfish --ro -a "$candidate" -i <<EOF
download /usr/local/lib/launchpad/windows-test-broker.py "$report/installed/windows-test-broker.py"
download /usr/local/lib/launchpad/windows-test-client.py "$report/installed/windows-test-client.py"
download /usr/local/bin/launchpad-windows-test "$report/installed/launchpad-windows-test"
download /etc/systemd/system/launchpad-windows-test.service "$report/installed/launchpad-windows-test.service"
download /etc/udev/rules.d/70-launchpad-windows-test.rules "$report/installed/launchpad-windows-test.rules"
download /etc/apparmor.d/usr.local.bin.grok "$report/installed/usr.local.bin.grok"
readlink /etc/systemd/system/multi-user.target.wants/launchpad-windows-test.service
EOF
for path in "$report/payload/"*; do cmp "$path" "$report/installed/$(basename "$path")"; done
qemu-img check "$candidate" > "$report/qcow-check.log"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
sha256sum "$template" "$candidate" "$report/payload/"* > "$report/identities.sha256"
printf 'Windows test bridge staged in a new private candidate; selected template unchanged.\n'
