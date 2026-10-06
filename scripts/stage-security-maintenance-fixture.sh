#!/usr/bin/env bash
# Add dummy persistence witnesses to a NEW owned child; original is read-only.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
original=$(realpath -- "${1:?owned original fixture required}")
expected=${2:?original SHA256 required}
report=$(realpath -m -- "${3:?new owned fixture directory required}")
backing=$(realpath -- "${4:?exact original backing template required}")
backing_sha=${5:?backing SHA256 required}
case "$original" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/guest/security-maintenance-*) ;; *) exit 2 ;; esac
case "$backing" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*/template.qcow2) ;; *) exit 2 ;; esac
test ! -e "$report"
test "$(sha256sum -- "$original" | cut -d' ' -f1)" = "$expected"
mkdir -p "$report"
cp -- "$(dirname -- "$original")/sent.manifest" "$report/sent.manifest"
printf 'owned-history-survives-security-maintenance\n' > "$report/owned-history.txt"
printf '#!/bin/sh\nprintf "UPGRADE-OWNED-TOOL:retained\\n"\n' > "$report/owned-tool"
# Older owned fixtures have a Windows absolute backing name. Translate that
# name on a private copy only after matching its exact path and image hash.
python3 - "$original" "$backing" "$backing_sha" "$report/portable-original.qcow2" <<'PY'
import hashlib, json, os, pathlib, shutil, subprocess, sys
original, backing, expected, view=sys.argv[1:]
with open(backing,'rb') as stream: assert hashlib.file_digest(stream,'sha256').hexdigest()==expected
info=json.loads(subprocess.check_output(['qemu-img','info','--output=json',original]))
name=info['backing-filename'].replace('\\','/')
if len(name)>2 and name[1:3]==':/': name='/mnt/'+name[0].lower()+name[2:]
else: name=str((pathlib.Path(original).parent/name).resolve())
assert name.lower()==backing.lower(), 'Unexpected backing; never substitute another template'
shutil.copyfile(original,view)
relative=os.path.relpath(backing,pathlib.Path(view).parent)
subprocess.run(['qemu-img','rebase','-u','-f','qcow2','-F','qcow2','-b',relative,view],check=True)
PY
qemu-img create -f qcow2 -F qcow2 -b portable-original.qcow2 "$report/session.qcow2"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --rw -a "$report/session.qcow2" -i <<EOF
mkdir-p /home/builder/.config/launchpad-owned-maintenance
mkdir-p /home/builder/.local/bin
upload "$report/owned-history.txt" /home/builder/.config/launchpad-owned-maintenance/history.txt
upload "$report/owned-tool" /home/builder/.local/bin/launchpad-owned-maintenance-tool
chown 1000 1000 /home/builder/.config/launchpad-owned-maintenance
chown 1000 1000 /home/builder/.config/launchpad-owned-maintenance/history.txt
chown 1000 1000 /home/builder/.local/bin/launchpad-owned-maintenance-tool
chmod 0600 /home/builder/.config/launchpad-owned-maintenance/history.txt
chmod 0755 /home/builder/.local/bin/launchpad-owned-maintenance-tool
EOF
qemu-img check "$report/session.qcow2" > "$report/qcow-check.txt"
test "$(sha256sum -- "$original" | cut -d' ' -f1)" = "$expected"
sha256sum -- "$original" "$report/session.qcow2" > "$report/original-and-fixture.sha256"
printf 'Linux fixture staged. Before Windows boot, finalize its private view address with finalize-security-maintenance-fixture.ps1 and the same exact original/backing hashes.\n'
