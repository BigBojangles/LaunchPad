#!/usr/bin/env bash
# Actual newer vendor npm in an explicitly owned fixture, never a product image.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
fixture=$(realpath -- "${1:?new owned maintenance fixture required}")
archive=$(realpath -- "${2:?verified newer vendor archive required}")
expected=${3:?archive SHA256 required}
case "$fixture" in "$project"/tests/LaunchPad.Tests/TestResults/guest/security-maintenance-newer-npm-*) ;; *) exit 2 ;; esac
test "$(dirname -- "$fixture")" = "$project/tests/LaunchPad.Tests/TestResults/guest"
case "$archive" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-package-inputs-*/npm-newer-owned-12.2.0.tgz) ;; *) exit 2 ;; esac
test ! -e "$fixture/newer-npm-fixture-private.json"
test -f "$fixture/portable-original.qcow2" && test -f "$fixture/session.qcow2"
test ! -L "$fixture/session.qcow2" && test ! -L "$archive"
test "$(sha256sum -- "$archive" | cut -d' ' -f1)" = "$expected"
python3 - "$archive" <<'PY'
import json, pathlib, sys, tarfile
with tarfile.open(sys.argv[1]) as archive:
    names=set()
    for member in archive:
        name=pathlib.PurePosixPath(member.name)
        assert not name.is_absolute() and '..' not in name.parts and name.parts[0]=='package'
        assert member.isfile() or member.isdir()
        assert member.name not in names
        names.add(member.name)
    value=json.load(archive.extractfile('package/package.json'))
    assert value['name']=='npm' and value['version']=='12.2.0'
PY
sha256sum -- "$fixture/portable-original.qcow2" > "$fixture/view-before-newer-install.sha256"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
virt-customize --no-network -a "$fixture/session.qcow2" \
    --upload "$archive:/opt/launchpad-owned-newer-npm.tgz" \
    --run-command 'set -eu; test "$(readlink -f /usr/local/lib/node_modules/npm)" = /usr/local/lib/node_modules/npm; test ! -e /usr/local/lib/node_modules/.launchpad-owned-npm-before; mv /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/.launchpad-owned-npm-before; mkdir /usr/local/lib/node_modules/npm; tar -xzf /opt/launchpad-owned-newer-npm.tgz --strip-components=1 -C /usr/local/lib/node_modules/npm; test "$(npm --version)" = 12.2.0; test "$(readlink -f /usr/local/lib/node_modules/.launchpad-owned-npm-before)" = /usr/local/lib/node_modules/.launchpad-owned-npm-before; rm -rf -- /usr/local/lib/node_modules/.launchpad-owned-npm-before; rm -- /opt/launchpad-owned-newer-npm.tgz; sync' \
    > "$fixture/newer-install-private.log" 2>&1
guestfish --ro -a "$fixture/session.qcow2" -i download /usr/local/lib/node_modules/npm/package.json "$fixture/npm-newer-installed.json"
qemu-img check "$fixture/session.qcow2" > "$fixture/newer-qcow-check.txt"
sha256sum -- "$fixture/portable-original.qcow2" > "$fixture/view-after-newer-install.sha256"
cmp "$fixture/view-before-newer-install.sha256" "$fixture/view-after-newer-install.sha256"
python3 - "$fixture" "$archive" "$expected" <<'PY'
import hashlib, json, pathlib, sys
root=pathlib.Path(sys.argv[1])
value=json.loads((root/'npm-newer-installed.json').read_text())
assert value['name']=='npm' and value['version']=='12.2.0'
with (root/'session.qcow2').open('rb') as stream: identity=hashlib.file_digest(stream,'sha256').hexdigest()
(root/'newer-npm-fixture-private.json').write_text(json.dumps(dict(schema=1, actualVendorNpm='12.2.0', archive=sys.argv[2], archiveSha256=sys.argv[3], fixtureSha256=identity, scope='Owned disposable child only; newer vendor npm executes successfully, not a shipping npm major change or preservation verdict.'),indent=2)+'\n')
print('Actual newer npm fixture installed: '+identity)
PY
