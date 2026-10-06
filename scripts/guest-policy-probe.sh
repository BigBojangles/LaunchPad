#!/usr/bin/env bash
# Policy snapshot/observer in a disposable owned guest only, never a scanner.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
mode=${1:?stage or collect required}
overlay=$(realpath -m -- "${2:?owned disposable overlay required}")
template=$(realpath -- "${3:?exact template required}")
expected=${4:?template SHA256 required}
report=$(realpath -- "${5:?existing private report directory required}")
coverage=${6:-observe}
case "$coverage" in observe|missing-profile|agent-tools|agent-terminal) ;; *) exit 2 ;; esac
case "$overlay" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*) ;; *) exit 2 ;; esac
test "$overlay" != "$template"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
if [ "$mode" = stage ]; then
    test ! -e "$overlay"
    qemu-img create -f qcow2 -F qcow2 -b "$template" "$overlay"
    # A newly created empty child changes only its backing path representation.
    qemu-img rebase -u -f qcow2 -F qcow2 -b "$(realpath --relative-to="$(dirname -- "$overlay")" "$template")" "$overlay"
    cat > "$report/launchpad-policy-snapshot.service" <<'UNIT'
[Unit]
Description=LaunchPad owned read-only guest policy snapshot
After=apparmor.service local-fs.target
Before=bl-proof.service launchpad-policy-observer.service
[Service]
Type=oneshot
ExecStart=/bin/sh /opt/launchpad-policy-probe/snapshot.sh
TimeoutStartSec=30
[Install]
WantedBy=multi-user.target
UNIT
    if [ "$coverage" = agent-tools ] || [ "$coverage" = agent-terminal ]; then
        test -f "$report/owned_targets.json"
        sed -i '/ExecStart=\/bin\/sh/i ExecStartPre=/bin/sh /opt/launchpad-policy-probe/egress.sh' "$report/launchpad-policy-snapshot.service"
    fi
    cat > "$report/launchpad-policy-observer.service" <<'UNIT'
[Unit]
Description=LaunchPad owned agent identity observer
After=launchpad-policy-snapshot.service
Before=bl-proof.service
[Service]
Type=simple
ExecStart=/usr/bin/python3 /opt/launchpad-policy-probe/observe.py
TimeoutStopSec=5
[Install]
WantedBy=multi-user.target
UNIT
    if [ "$coverage" = agent-tools ] || [ "$coverage" = agent-terminal ]; then
        sed -i 's@observe.py$@observe.py 360@' "$report/launchpad-policy-observer.service"
    fi
    if [ "$coverage" = agent-terminal ]; then
        test -f "$report/owned_terminal.json"
        sed -i 's@observe.py 360$@observe.py 360 terminal@' "$report/launchpad-policy-observer.service"
    fi
    guestfish --rw -a "$overlay" -i <<EOF
mkdir-p /opt/launchpad-policy-probe
mkdir-p /home/builder/other-project
write /home/builder/other-project/private.txt "owned inaccessible canary"
chown 1000 1000 /home/builder/other-project
chown 1000 1000 /home/builder/other-project/private.txt
chmod 0700 /home/builder/other-project
chmod 0600 /home/builder/other-project/private.txt
write /etc/launchpad-policy-canary "owned policy-only canary"
chmod 0666 /etc/launchpad-policy-canary
upload "$project/scripts/diagnose-guest-policy.sh" /opt/launchpad-policy-probe/snapshot.sh
upload "$project/scripts/observe-guest-policy.py" /opt/launchpad-policy-probe/observe.py
upload "$report/launchpad-policy-snapshot.service" /etc/systemd/system/launchpad-policy-snapshot.service
upload "$report/launchpad-policy-observer.service" /etc/systemd/system/launchpad-policy-observer.service
ln-s /etc/systemd/system/launchpad-policy-snapshot.service /etc/systemd/system/multi-user.target.wants/launchpad-policy-snapshot.service
ln-s /etc/systemd/system/launchpad-policy-observer.service /etc/systemd/system/multi-user.target.wants/launchpad-policy-observer.service
EOF
    if [ "$coverage" = agent-tools ] || [ "$coverage" = agent-terminal ]; then
        guestfish --rw -a "$overlay" -i <<EOF
upload "$report/owned_targets.json" /opt/launchpad-policy-probe/owned_targets.json
upload "$project/scripts/owned-agent-egress.sh" /opt/launchpad-policy-probe/egress.sh
EOF
    fi
    if [ "$coverage" = agent-terminal ]; then
        guestfish --rw -a "$overlay" -i <<EOF
upload "$report/owned_terminal.json" /opt/launchpad-policy-probe/owned_terminal.json
EOF
        if [ "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("fault", ""))' "$report/owned_terminal.json")" = missing-helper ]; then
            # Fault only in this new owned session, never in the candidate.
            guestfish --rw -a "$overlay" -i <<EOF
rm /usr/local/lib/launchpad/quiesce.py
EOF
        fi
    fi
    if [ "$coverage" = missing-profile ]; then
        # Deliberate absence fault ONLY on this new owned child. Other
        # AppArmor profiles and the selected candidate remain untouched.
        guestfish --rw -a "$overlay" -i <<EOF
rm /etc/apparmor.d/usr.local.bin.grok
EOF
    fi
elif [ "$mode" = collect ]; then
    test -f "$overlay"
    if [ "$coverage" = agent-tools ] || [ "$coverage" = agent-terminal ]; then
        extra=''
        if [ "$coverage" = agent-terminal ]; then
            extra="download /home/builder/in/project/agent-progress.txt \"$report/final-agent-progress.txt\""
        fi
        guestfish --ro -a "$overlay" -i <<EOF
copy-out /var/tmp/launchpad-policy-probe "$report"
copy-out /home/builder/in/project/fixture-output "$report"
$extra
EOF
    else
        guestfish --ro -a "$overlay" -i <<EOF
copy-out /var/tmp/launchpad-policy-probe "$report"
EOF
    fi
else
    exit 2
fi
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
