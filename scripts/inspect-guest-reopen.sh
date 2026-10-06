#!/usr/bin/env bash
# Read only an owned diagnostic overlay; never boot or repair it here.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
disk=$(realpath -- "${1:?owned session disk required}")
report=$(realpath -m -- "${2:?new private evidence directory required}")
candidate=$(realpath -- "${3:?verified candidate required}")
expected=${4:?candidate SHA256 required}
case "$disk" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/reopen-inspection*) ;; *) exit 2 ;; esac
case "$candidate" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*/template.qcow2) ;; *) exit 2 ;; esac
test ! -e "$report"
mkdir -p "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
sha256sum "$disk" > "$report/before.sha256"
# Map a Windows absolute backing name in a small inspection copy only. Its
# original backing must resolve to the exact supplied, hash-verified candidate.
view="$report/inspection.qcow2"
python3 - "$disk" "$candidate" "$expected" "$view" <<'PY'
import hashlib, json, pathlib, shutil, subprocess, sys
disk, candidate, expected, view = sys.argv[1:]
with open(candidate, 'rb') as stream:
    assert hashlib.file_digest(stream, 'sha256').hexdigest() == expected
info = json.loads(subprocess.check_output(['qemu-img', 'info', '--output=json', disk]))
backing = info['backing-filename'].replace('\\', '/')
if len(backing) > 2 and backing[1:3] == ':/':
    backing = '/mnt/' + backing[0].lower() + backing[2:]
else:
    backing = str((pathlib.Path(disk).parent / backing).resolve())
assert backing.lower() == candidate.lower(), 'Unexpected backing; do not inspect with a substituted template.'
shutil.copyfile(disk, view)
subprocess.run(['qemu-img', 'rebase', '-u', '-f', 'qcow2', '-F', 'qcow2', '-b', candidate, view], check=True)
PY
guestfish --ro -a "$view" -i <<EOF
download /home/builder/in/project/guest.txt "$report/guest.txt"
download /home/builder/in/project/host.txt "$report/host.txt"
download /home/builder/in/project/.git/config "$report/git-config.txt"
download /home/builder/.config/launchpad-probe/session.txt "$report/history.txt"
download /var/lib/launchpad/import.json "$report/guest-baseline.json"
EOF
sha256sum "$disk" > "$report/after.sha256"
cmp "$report/before.sha256" "$report/after.sha256"
printf 'Read-only preserved-state inspection complete.\n'
