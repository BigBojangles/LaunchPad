#!/bin/sh
# Trusted host-supplied maintenance only, while PID 1 is a root shell and the
# normal agent service cannot be running. Never install this as an agent tool.
set -eu
test "$(id -u)" = 0
test "$(cat /proc/1/comm)" = sh
cd /run/launchpad-maintenance
sha256sum -c SHA256SUMS
# Refuse partial/mixed procps state before any security package changes. Preserve
# a coherent newer installation; never force the kit's older versions onto it.
procps_version=$(dpkg-query -W -f='${Version}' procps 2>/dev/null || true)
libproc_version=$(dpkg-query -W -f='${Version}' libproc2-0 2>/dev/null || true)
test "$procps_version" = "$libproc_version"
procps_install=true
if [ -n "$procps_version" ] && dpkg --compare-versions "$procps_version" ge '2:4.0.2-3'; then
    test -x /usr/bin/ps
    test -e /usr/lib/x86_64-linux-gnu/libproc2.so.0
    procps_install=false
fi
if [ -d security-packages ]; then
    test ! -e /opt/launchpad-security-inputs
    test ! -L /opt/launchpad-security-inputs
    install -d -o root -g root -m 0700 /opt/launchpad-security-inputs
    cp security-packages/* /opt/launchpad-security-inputs/
    /bin/sh /opt/launchpad-security-inputs/apply.sh maintenance
fi
perl -c bl-proof.sh
perl -c launchpad-agent
# Install the exact dependency packages, keeping dpkg/security inventories true.
if [ "$procps_install" = true ]; then
    dpkg -i libproc2-0_4.0.2-3_amd64.deb procps_4.0.2-3_amd64.deb
else
    printf 'PRESERVE-PROCPS:%s\n' "$procps_version"
fi
install -o root -g root -m 0750 bl-proof.sh /usr/local/bin/bl-proof.sh
install -o root -g root -m 0755 launchpad-agent /usr/local/bin/launchpad-agent
install -o root -g root -m 0644 usr.local.bin.grok /etc/apparmor.d/usr.local.bin.grok
install -o root -g root -m 0644 bl-proof.service /etc/systemd/system/bl-proof.service
install -d -o root -g root -m 0755 /usr/local/lib/launchpad
install -o root -g root -m 0750 quiesce.py /usr/local/lib/launchpad/quiesce.py
# The maintenance kernel is not the running product kernel. Compile only;
# ordinary boot loads the policy before any bundled agent can be selected.
apparmor_parser --skip-kernel-load --skip-cache /etc/apparmor.d/usr.local.bin.grok
perl -MDigest::SHA -MJSON::PP -MFile::Temp -e 'print "MAINTENANCE-MODULES-OK\n"'
ps --version
dpkg-query -W procps libproc2-0
sha256sum /usr/local/bin/bl-proof.sh
# Seed only an absent protected baseline from confirmed host import hashes.
# Existing guest baseline state survives an upgrade unchanged.
test ! -L /var/lib/launchpad
test ! -L /var/lib/launchpad/import.json
if [ ! -e /var/lib/launchpad/import.json ]; then
    perl -MJSON::PP -e 'local $/; my $s=<>; my $a=decode_json($s); die "Invalid baseline" unless ref($a) eq "ARRAY"; for (@$a) { die "Invalid baseline entry" unless ref($_) eq "ARRAY" && @$_==5 && $_->[4] =~ /^[0-9a-f]{64}$/; }' baseline.json
    install -d -o root -g root -m 0700 /var/lib/launchpad
    install -o root -g root -m 0600 baseline.json /var/lib/launchpad/import.json
fi
# Project, auth, history, home and network policy remain unchanged.
sync
printf 'MAINTENANCE-APPLY-OK\n'
