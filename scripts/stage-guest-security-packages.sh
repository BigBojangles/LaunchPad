#!/usr/bin/env bash
# Stage verified vendor packages into a new candidate only; no activation.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
template=$(realpath -- "${1:?selected template required}")
expected=${2:?selected template SHA256 required}
inputs=$(realpath -- "${3:?verified package inputs required}")
report=$(realpath -m -- "${4:?new private candidate directory required}")
case "$template" in "$(dirname -- "$project")"/build-launch-qemu/images/*.qcow2) ;; *) exit 2 ;; esac
case "$inputs" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-package-inputs-*) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-packages-*) ;; *) exit 2 ;; esac
test ! -e "$report"
test "$(sha256sum -- "$template" | cut -d' ' -f1)" = "$expected"
python3 - "$inputs" "$expected" "$project/scripts/guest-security-packages.json" <<'PY'
import hashlib, json, pathlib, re, sys
root=pathlib.Path(sys.argv[1]); manifest=json.loads((root/'security-packages.json').read_text())
lock=json.loads(pathlib.Path(sys.argv[3]).read_text())
assert manifest['schema']==1 and manifest['baseTemplateSha256']==sys.argv[2]
for row in manifest['debian']['packages']+[manifest['npm']]:
    assert re.fullmatch(r'[a-zA-Z0-9+_.~-]+',row['file'])
    assert hashlib.sha256((root/'packages'/row['file']).read_bytes()).hexdigest()==row['sha256']
for row in manifest['debian']['packages']:
    assert row['sha256']==lock['debian']['packages'][row['name']] and row['version']==lock['debian']['version']
assert manifest['npm']['sha256']==lock['npm']['sha256'] and manifest['npm']['integrity']==lock['npm']['integrity']
patches=manifest['npm'].get('patches',[])
if patches:
    patch_lock=json.loads((pathlib.Path(sys.argv[3]).parent/'npm-security-patches.json').read_text())
    assert manifest['npm']['patchLockSha256']==hashlib.sha256((pathlib.Path(sys.argv[3]).parent/'npm-security-patches.json').read_bytes()).hexdigest()
    assert manifest['npm']['patchHelperSha256']==hashlib.sha256((pathlib.Path(sys.argv[3]).parent/'npm-security-patches.py').read_bytes()).hexdigest()
    assert len(patches)==len(patch_lock['packages'])==2
    for row,pin in zip(patches,patch_lock['packages']):
        for key in ('name','version','baseVersion','sha256','integrity'): assert row[key]==pin[key]
        assert re.fullmatch(r'[a-zA-Z0-9+_.~-]+',row['file'])
        assert hashlib.sha256((root/'packages'/row['file']).read_bytes()).hexdigest()==row['sha256']
PY
mkdir -p "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$template" -i <<EOF
tar-out /usr/local/lib/node_modules/npm "$report/npm-original-private.tar"
download /usr/local/bin/bl-proof.sh "$report/bl-proof-before.sh"
download /usr/local/bin/launchpad-agent "$report/entry-before"
download /etc/apparmor.d/usr.local.bin.grok "$report/profile-before"
download /etc/systemd/system/bl-proof.service "$report/service-before"
download /usr/local/lib/launchpad/quiesce.py "$report/quiesce-before"
EOF
candidate="$report/template.qcow2"
qemu-img create -f qcow2 -F qcow2 -b "$(realpath --relative-to="$report" "$template")" "$candidate"
uploads=(--mkdir /opt/launchpad-security-inputs --upload "$inputs/security-packages.json:/opt/launchpad-security-inputs/security-packages.json"
    --upload "$project/scripts/guest-security-packages-apply.sh:/opt/launchpad-security-inputs/apply.sh")
while IFS= read -r name; do
    uploads+=(--upload "$inputs/packages/$name:/opt/launchpad-security-inputs/$name")
done < <(python3 -c 'import json,sys;m=json.load(open(sys.argv[1]));print("\n".join(r["file"] for r in m["debian"]["packages"]+[m["npm"]]+m["npm"].get("patches",[])))' "$inputs/security-packages.json")
if python3 -c 'import json,sys;sys.exit(not bool(json.load(open(sys.argv[1]))["npm"].get("patches")))' "$inputs/security-packages.json"; then
    uploads+=(--upload "$project/scripts/npm-security-patches.py:/opt/launchpad-security-inputs/npm-security-patches.py")
fi
virt-customize --no-network -a "$candidate" "${uploads[@]}" \
    --run-command '/bin/sh /opt/launchpad-security-inputs/apply.sh' > "$report/package-apply-private.log" 2>&1
guestfish --ro -a "$candidate" -i <<EOF
download /var/lib/dpkg/status "$report/guest-packages-after.txt"
download /usr/local/lib/node_modules/npm/package.json "$report/npm-installed.json"
download /usr/local/share/launchpad/security-packages.json "$report/package-receipt.json"
download /usr/local/bin/bl-proof.sh "$report/bl-proof-after.sh"
download /usr/local/bin/launchpad-agent "$report/entry-after"
download /etc/apparmor.d/usr.local.bin.grok "$report/profile-after"
download /etc/systemd/system/bl-proof.service "$report/service-after"
download /usr/local/lib/launchpad/quiesce.py "$report/quiesce-after"
EOF
for name in bl-proof entry profile service quiesce; do
    if [ "$name" = bl-proof ]; then cmp "$report/bl-proof-before.sh" "$report/bl-proof-after.sh";
    else cmp "$report/$name-before" "$report/$name-after"; fi
done
cmp "$inputs/security-packages.json" "$report/package-receipt.json"
qemu-img check "$candidate" > "$report/qcow-check.txt"
test "$(sha256sum -- "$template" | cut -d' ' -f1)" = "$expected"
sha256sum -- "$template" "$candidate" "$inputs/security-packages.json" "$project/scripts/guest-security-packages-apply.sh" > "$report/identities.sha256"
printf 'Vendor fix candidate staged without activation or saved-VM changes.\n'
