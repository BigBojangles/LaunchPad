#!/bin/sh
# Privileged system-audit setup in a disposable diagnostic guest only.
# This script never grants the production agent sudo or changes its restrictions.
set -u
report=/var/tmp/launchpad-lynis-audit
mkdir -p "$report"
chmod 0700 "$report"
id > "$report/identity.txt"
if [ "$(id -u)" != 0 ]; then exit 1; fi
# bl-proof is a long-running oneshot waiting for its host handoff. Ordering the
# audit before it samples the machine BEFORE its network ExecStartPre. Instead
# observe the running service's real firewall initialization, without adding or
# weakening rules in the diagnostic overlay.
attempt=0
while :; do
    if iptables-save > "$report/netfilter-private.txt" 2> "$report/netfilter-unavailable.txt" \
       && grep -F -- '-A OUTPUT -d 10.0.2.2/32 -j REJECT' "$report/netfilter-private.txt" >/dev/null \
       && [ "$(systemctl show bl-proof.service -p MainPID --value)" -gt 0 ]; then
        printf 'production bl-proof process and owned host-gateway rule observed\n' > "$report/runtime-ready.txt"
        break
    fi
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 60 ]; then
        printf 'production runtime initialization not observed\n' > "$report/runtime-ready.txt"
        printf 'LP-LYNIS-FAILED:runtime-not-ready\n' > /dev/ttyS0
        exit 1
    fi
    sleep 1
done
uname -a > "$report/runtime-kernel.txt"
cat /sys/kernel/security/apparmor/profiles > "$report/apparmor-profiles-private.txt" 2> "$report/apparmor-unavailable.txt" || true
cd /opt/launchpad-lynis-audit || exit 1
if ! printf '%s  lynis.tar.gz\n' 'b315c848323572500225312de7e9a3bf3b0c462f5b2d6f4ff56fe6ae521ad169' | sha256sum -c - > "$report/pin-check.txt"; then exit 1; fi
mkdir -p source
tar -xzf lynis.tar.gz --strip-components=1 -C source || exit 1
cd source || exit 1
chmod 0755 lynis
# No upload or remote-audit options. Logs remain in this disposable guest until
# the host reads them offline after this diagnostic VM has stopped.
./lynis audit system --quick --no-colors --log-file "$report/lynis.log" --report-file "$report/lynis.dat" > "$report/console-private.txt" 2>&1
result=$?
printf '%s\n' "$result" > "$report/exit.txt"
# The diagnostic host stops QEMU after this marker. Flush guest page cache first
# so a tool-complete marker cannot outrun durable report files on the overlay.
sync
printf 'LP-LYNIS-FINISHED:%s\n' "$result" > /dev/ttyS0
exit "$result"
