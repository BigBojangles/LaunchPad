#!/bin/sh
# Trusted maintenance input only. Stage in an owned candidate/overlay; never
# execute from the unprivileged agent or replace a live backing template.
set -eu
test "$(id -u)" = 0
test "$(cat /proc/1/comm)" = sh
sha256sum -c SHA256SUMS
for path in /usr/local/lib /usr/local/bin /etc/systemd/system /etc/systemd/system/multi-user.target.wants /etc/apparmor.d /etc/udev/rules.d /usr/local/lib/launchpad/windows-test-broker.py /usr/local/lib/launchpad/windows-test-client.py; do
    test ! -L "$path"
done
test ! -L /usr/local/lib/launchpad
test ! -L /usr/local/bin/launchpad-windows-test
test ! -L /etc/systemd/system/launchpad-windows-test.service
test ! -L /etc/apparmor.d/usr.local.bin.grok
test ! -L /etc/udev/rules.d/70-launchpad-windows-test.rules
enabled=/etc/systemd/system/multi-user.target.wants/launchpad-windows-test.service
if [ -e "$enabled" ] || [ -L "$enabled" ]; then
    test -L "$enabled"
    test "$(readlink "$enabled")" = /etc/systemd/system/launchpad-windows-test.service
fi
# Host-supplied custom tools can leave .local root-owned. Create only missing
# state ancestors; never recursively change saved tools, auth or history.
for path in /home/builder /home/builder/.local /home/builder/.local/state /home/builder/.local/state/launchpad /home/builder/.local/state/launchpad/windows-tests; do
    test ! -L "$path"
    if [ -e "$path" ]; then test -d "$path"; fi
done
for path in /home/builder/.local /home/builder/.local/state /home/builder/.local/state/launchpad /home/builder/.local/state/launchpad/windows-tests; do
    if [ ! -e "$path" ]; then install -d -o 1000 -g 1000 -m 0700 "$path"; fi
done
test "$(stat -c %u /home/builder/.local/state/launchpad/windows-tests)" = 1000
install -d -o root -g root -m 0755 /usr/local/lib/launchpad
install -o root -g root -m 0750 windows-test-broker.py /usr/local/lib/launchpad/windows-test-broker.py
install -o root -g root -m 0644 windows-test-client.py /usr/local/lib/launchpad/windows-test-client.py
# A standalone root-owned entry keeps import paths fixed; no guest credentials,
# startup prompts, provider hooks or agent instruction changes.
printf '#!/bin/sh\nexec /usr/bin/python3 /usr/local/lib/launchpad/windows-test-client.py run "$@"\n' > launchpad-windows-test
install -o root -g root -m 0755 launchpad-windows-test /usr/local/bin/launchpad-windows-test
install -o root -g root -m 0644 launchpad-windows-test.service /etc/systemd/system/launchpad-windows-test.service
# Match only this new serial port; existing Fence/status/TUI devices keep their
# current ownership. The broker also checks the opened device before reading.
printf '%s\n' 'SUBSYSTEM=="virtio-ports", ATTR{name}=="launchpad-windows-test", OWNER="root", GROUP="root", MODE="0600"' > launchpad-windows-test.rules
install -o root -g root -m 0644 launchpad-windows-test.rules /etc/udev/rules.d/70-launchpad-windows-test.rules
python3 - <<'PY'
from pathlib import Path
path = Path('/etc/apparmor.d/usr.local.bin.grok')
text = path.read_text()
rule = '  /run/launchpad-windows-test/bridge.sock rw,\n'
if rule not in text:
    needle = '  network,\n'
    if text.count(needle) != 1:
        raise SystemExit('Unexpected policy input; preserve it for review.')
    path.write_text(text.replace(needle, needle + rule))
PY
apparmor_parser --skip-kernel-load --skip-cache /etc/apparmor.d/usr.local.bin.grok
systemctl enable launchpad-windows-test.service
# No daemon start or policy reload in maintenance. Ordinary boot owns these.
sync
printf 'WINDOWS-TEST-GUEST-INSTALLED\n'
