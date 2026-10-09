#!/bin/sh
# Image construction only. Never invoke this on a saved user session or maintenance upgrade.
set -eu
test "$(id -u)" = 0
test "$(cat /proc/1/comm)" = sh
test -f /run/launchpad-image-build/owned-template
test ! -e /home/builder/in
test ! -e /var/lib/launchpad/import.json
for state in .grok .codex .claude .ssh; do
    test ! -e "/home/builder/$state"
done
# Template-only: replace the inherited password hash with a locked marker.
# Maintenance must not change accounts in existing user sessions.
usermod -p '*' builder
cd /run/launchpad-image-build/payload
sha256sum -c SHA256SUMS
perl -c guest-session-supervisor.pl
install -o root -g root -m 0750 guest-session-supervisor.pl /usr/local/bin/launchpad-session
install -o root -g root -m 0644 launchpad-session.service /etc/systemd/system/launchpad-session.service
mkdir -p /etc/systemd/system/multi-user.target.wants
ln -sfn /etc/systemd/system/launchpad-session.service /etc/systemd/system/multi-user.target.wants/launchpad-session.service
rm -f /usr/local/bin/bl-proof.sh /etc/systemd/system/bl-proof.service /etc/systemd/system/multi-user.target.wants/bl-proof.service
# Release bridge is disabled in both guest image construction and host launch.
rm -f /etc/systemd/system/launchpad-windows-test.service /etc/systemd/system/multi-user.target.wants/launchpad-windows-test.service
rm -f /usr/local/bin/launchpad-windows-test /usr/local/lib/launchpad/windows-test-broker.py /usr/local/lib/launchpad/windows-test-client.py /etc/udev/rules.d/70-launchpad-windows-test.rules /etc/udev/rules.d/99-launchpad-windows-test.rules
rm -f /tmp/install-coding.sh /tmp/agent-inspect.txt /tmp/agent-fix-verify.txt /tmp/builder.log
rm -rf /tmp/node-compile-cache /home/builder/.npm /.npm /.codex
rm -rf /var/cache/apt/archives/* /var/lib/apt/lists/*
mkdir -p /var/cache/apt/archives/partial /var/lib/apt/lists/partial
chown _apt:root /var/cache/apt/archives/partial /var/lib/apt/lists/partial
chmod 0700 /var/cache/apt/archives/partial /var/lib/apt/lists/partial
rm -f /var/log/apt/* /var/log/dpkg.log /var/log/alternatives.log
rm -f /etc/ca-certificates.conf.dpkg-old /etc/passwd- /etc/group- /etc/shadow- /etc/gshadow- /etc/subuid- /etc/subgid-
# /run is tmpfs during construction: remove the underlying disk's stale state separately.
mkdir -p /run/launchpad-image-build/disk-root
mount --bind / /run/launchpad-image-build/disk-root
rm -f /run/launchpad-image-build/disk-root/run/adduser /run/launchpad-image-build/disk-root/run/reboot-required /run/launchpad-image-build/disk-root/run/reboot-required.pkgs
rm -f /run/launchpad-image-build/disk-root/run/blkid/blkid.tab /run/launchpad-image-build/disk-root/run/blkid/blkid.tab.old
umount /run/launchpad-image-build/disk-root
# An empty machine-id generates a fresh identity at startup; dbus shares it.
rm -f /var/lib/dbus/machine-id
ln -s /etc/machine-id /var/lib/dbus/machine-id
: > /etc/machine-id
chmod 0444 /etc/machine-id
sync
fstrim -v /
sync
printf '\nLAUNCHPAD-IMAGE-CLEAN-OK\n'
