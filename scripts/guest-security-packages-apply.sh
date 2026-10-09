#!/bin/sh
# Trusted image-build operation only; never expose it to an agent/session.
set -eu
test "$(id -u)" = 0
mode=${1:-build}
case "$mode" in build) ;; maintenance) test "$(cat /proc/1/comm)" = sh ;; *) exit 2 ;; esac
inputs=/opt/launchpad-security-inputs
test "$(readlink -f "$inputs")" = "$inputs"
cd "$inputs"
python3 - <<'PY'
import hashlib, json, pathlib, re
root = pathlib.Path('.')
manifest = json.loads((root/'security-packages.json').read_text())
assert manifest['schema'] == 1
rows = manifest['debian']['packages'] + [manifest['npm']]
assert len(rows) == 5
rows += manifest['npm'].get('patches', [])
for row in rows:
    assert re.fullmatch(r'[a-zA-Z0-9+_.~-]+', row['file'])
    path = root / row['file']
    assert path.is_file() and not path.is_symlink()
    assert hashlib.sha256(path.read_bytes()).hexdigest() == row['sha256']
if manifest['npm'].get('patches'):
    helper = root/'npm-security-patches.py'
    assert helper.is_file() and not helper.is_symlink()
    assert hashlib.sha256(helper.read_bytes()).hexdigest() == manifest['npm']['patchHelperSha256']
assert manifest['npm']['version'] == '11.20.0'
assert {r['name'] for r in manifest['debian']['packages']} == {'perl-base','perl-modules-5.36','libperl5.36','perl'}
assert all(r['version'] == '5.36.0-7+deb12u4' for r in manifest['debian']['packages'])
PY
# Preflight both package families before changes. Preserved VMs may contain a
# newer coherent vendor toolset; maintenance must never downgrade it.
perl_lower=0
perl_ready=0
perl_family_version=
for package in perl-base perl-modules-5.36 libperl5.36 perl; do
    current=$(dpkg-query -W -f='${Version}' "$package")
    if [ -z "$perl_family_version" ]; then perl_family_version=$current; else test "$current" = "$perl_family_version"; fi
    if dpkg --compare-versions "$current" lt '5.36.0-7+deb12u4'; then perl_lower=$((perl_lower + 1));
    else perl_ready=$((perl_ready + 1)); fi
done
test "$perl_lower" = 4 || test "$perl_ready" = 4
test ! -L /usr/local/lib/node_modules
test ! -L /usr/local/lib/node_modules/npm
test -d /usr/local/lib/node_modules/npm
node - <<'JS'
const root='/usr/local/lib/node_modules/npm';
const pkg=require(root+'/package.json');
const semver=require(root+'/node_modules/semver');
if (!semver.valid(pkg.version) || semver.lt(pkg.version,'11.0.0')) process.exit(1);
JS
npm_current=$(node -p 'require("/usr/local/lib/node_modules/npm/package.json").version')
npm_install=$(node -p 'require("/usr/local/lib/node_modules/npm/node_modules/semver").lt(require("/usr/local/lib/node_modules/npm/package.json").version,"11.20.0")')
if [ -f npm-security-patches.py ] && [ "$npm_install" = false ]; then
    if [ "$mode" = build ]; then python3 npm-security-patches.py preflight-build;
    else python3 npm-security-patches.py preflight; fi
fi
if [ "$mode" = build ]; then
    test "$perl_lower" = 4
    case "$npm_current" in 11.*) ;; *) exit 1 ;; esac
    test "$npm_install" = true
fi
if [ "$perl_lower" = 4 ]; then
    dpkg -i ./perl-base_5.36.0-7+deb12u4_amd64.deb ./perl-modules-5.36_5.36.0-7+deb12u4_all.deb ./libperl5.36_5.36.0-7+deb12u4_amd64.deb ./perl_5.36.0-7+deb12u4_amd64.deb
else
    printf 'PRESERVE-NEWER-PERL-PACKAGES\n'
fi
test ! -L /usr/local/lib/node_modules
test ! -L /usr/local/lib/node_modules/npm
test -d /usr/local/lib/node_modules/npm
if [ "$npm_install" = true ]; then
    test ! -e /usr/local/lib/node_modules/.launchpad-npm-before
    mv /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/.launchpad-npm-before
    mkdir /usr/local/lib/node_modules/npm
    tar -xzf npm-11.20.0.tgz --strip-components=1 -C /usr/local/lib/node_modules/npm
node - <<'JS'
const root='/usr/local/lib/node_modules/npm';
const pkg=require(root+'/package.json');
const semver=require(root+'/node_modules/semver');
if(pkg.version!=='11.20.0'||!semver.satisfies(process.versions.node,pkg.engines.node))process.exit(1);
console.log('NODE-ENGINE-OK:'+process.version);
JS
test "$(npm --version)" = 11.20.0
test "$(readlink -f /usr/local/lib/node_modules/.launchpad-npm-before)" = /usr/local/lib/node_modules/.launchpad-npm-before
# This is the old vendor package in the NEW template only. Its original full
# package archive is retained privately by the stage script; no guest home or
# project path is involved, and no saved VM is being edited.
rm -rf -- /usr/local/lib/node_modules/.launchpad-npm-before
else
    printf 'PRESERVE-NEWER-NPM:%s\n' "$npm_current"
fi
if [ -f npm-security-patches.py ]; then python3 npm-security-patches.py "$mode"; fi
install -d -o root -g root -m 0755 /usr/local/share/launchpad
install -o root -g root -m 0644 security-packages.json /usr/local/share/launchpad/security-packages.json
if [ "$mode" = build ]; then
    perl -c /usr/local/bin/launchpad-session
    perl -c /usr/local/bin/launchpad-agent
    apparmor_parser --skip-kernel-load --skip-cache /etc/apparmor.d/usr.local.bin.grok
fi
# Maintenance validates the replacement startup/policy in its outer wrapper;
# an older preserved VM may have the very policy defect that kit repairs.
perl -MDigest::SHA -MJSON::PP -MFile::Temp -e 'print "SECURITY-PACKAGE-MODULES-OK\n"'
dpkg-query -W perl-base perl-modules-5.36 libperl5.36 perl
node --version
npm --version
cd /
test "$(readlink -f "$inputs")" = /opt/launchpad-security-inputs
rm -rf -- /opt/launchpad-security-inputs
sync
printf 'SECURITY-PACKAGE-APPLY-OK\n'
