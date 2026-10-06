#!/bin/sh
# Read-only policy diagnosis, supplied only to a disposable test overlay.
set -u
umask 077
report=/var/tmp/launchpad-policy-probe
mkdir -p "$report"
id > "$report/identity.txt"
test "$(id -u)" = 0 || exit 1
uname -a > "$report/kernel.txt"
cat /proc/cmdline > "$report/kernel-command.txt"
cat /sys/module/apparmor/parameters/enabled > "$report/enabled.txt" 2> "$report/enabled-error.txt" || true
cat /sys/kernel/security/apparmor/profiles > "$report/loaded-profiles.txt" 2> "$report/loaded-error.txt" || true
systemctl show apparmor.service -p ActiveState -p SubState -p Result > "$report/service-state.txt"
journalctl -b -u apparmor.service --no-pager > "$report/service-log.txt"
if test -f /etc/apparmor.d/usr.local.bin.grok; then
    cp /etc/apparmor.d/usr.local.bin.grok "$report/grok-profile.txt"
    # Preprocess only; never load, replace or disable a kernel policy.
    apparmor_parser --preprocess /etc/apparmor.d/usr.local.bin.grok > "$report/parser-expanded.txt" 2> "$report/parser-error.txt"
    printf '%s\n' "$?" > "$report/parser-exit.txt"
else
    printf 'missing\n' > "$report/parser-exit.txt"
    : > "$report/parser-expanded.txt"
    printf 'Grok policy file missing\n' > "$report/parser-error.txt"
    : > "$report/grok-profile.txt"
fi
for program in grok codex claude aa-exec; do
    printf '%s:' "$program" >> "$report/executables.txt"
    command -v "$program" >> "$report/executables.txt" 2>&1 || true
done
sync
printf 'LP-POLICY-SNAPSHOT-DONE\n' > /dev/ttyS0
