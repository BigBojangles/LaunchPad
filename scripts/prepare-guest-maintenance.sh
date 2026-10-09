#!/usr/bin/env bash
# Extract an offline upgrade kit from reviewed runtime inputs; no session writes.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
candidate=$(realpath -- "${1:?reviewed candidate required}")
expected=${2:?candidate SHA256 required}
report=$(realpath -m -- "${3:?new private output directory required}")
version=${4:-diagnostic-unactivated}
security_inputs=${5:-}
[[ "$version" =~ ^[a-z0-9-]{1,64}$ ]]
case "$candidate" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*/template.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*) ;; *) exit 2 ;; esac
test ! -e "$report"
test "$(sha256sum "$candidate" | cut -d' ' -f1)" = "$expected"
mkdir -p "$report/payload" "$report/packages"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$candidate" -i <<EOF
download /boot/vmlinuz-6.1.0-53-amd64 "$report/kernel"
download /boot/initrd.img-6.1.0-53-amd64 "$report/initrd"
download /usr/local/bin/launchpad-session "$report/payload/launchpad-session"
download /usr/local/bin/launchpad-agent "$report/payload/launchpad-agent"
download /etc/apparmor.d/usr.local.bin.grok "$report/payload/usr.local.bin.grok"
download /etc/systemd/system/launchpad-session.service "$report/payload/launchpad-session.service"
download /usr/local/lib/launchpad/quiesce.py "$report/payload/quiesce.py"
download /usr/bin/ps "$report/verified-ps"
download /usr/lib/x86_64-linux-gnu/libproc2.so.0 "$report/verified-libproc2"
EOF
security_present=$(guestfish --ro -a "$candidate" -i is-file /usr/local/share/launchpad/security-packages.json)
if [ "$security_present" = true ]; then
    test -n "$security_inputs"
    security_inputs=$(realpath -- "$security_inputs")
    case "$security_inputs" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-package-inputs-*) ;; *) exit 2 ;; esac
    guestfish --ro -a "$candidate" -i download /usr/local/share/launchpad/security-packages.json "$report/security-packages.json"
    cmp "$report/security-packages.json" "$security_inputs/security-packages.json"
    mkdir "$report/payload/security-packages"
    cp -- "$security_inputs/security-packages.json" "$report/payload/security-packages/"
    cp -- "$project/scripts/guest-security-packages-apply.sh" "$report/payload/security-packages/apply.sh"
    while IFS= read -r name; do
        cp -- "$security_inputs/packages/$name" "$report/payload/security-packages/"
    done < <(python3 -c 'import json,sys;m=json.load(open(sys.argv[1]));print("\n".join(r["file"] for r in m["debian"]["packages"]+[m["npm"]]+m["npm"].get("patches",[])))' "$security_inputs/security-packages.json")
    if python3 -c 'import json,sys;sys.exit(not bool(json.load(open(sys.argv[1]))["npm"].get("patches")))' "$security_inputs/security-packages.json"; then
        test "$(sha256sum "$project/scripts/npm-security-patches.py" | cut -d' ' -f1)" = "$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["npm"]["patchHelperSha256"])' "$security_inputs/security-packages.json")"
        cp -- "$project/scripts/npm-security-patches.py" "$report/payload/security-packages/"
    fi
elif [ -n "$security_inputs" ]; then
    printf 'Security package inputs provided for a candidate with no receipt.\n' >&2
    exit 2
fi
for package in procps libproc2-0; do
    deb="$report/payload/${package}_4.0.2-3_amd64.deb"
    curl --fail --location --silent --show-error "https://deb.debian.org/debian/pool/main/p/procps/${package}_4.0.2-3_amd64.deb" -o "$deb"
    if [ "$package" = procps ]; then pinned=d9d0e75779cb79af869181f17b93c5c263a2b89cac6a0193c436160a4483ddc1;
    else pinned=e82ba5d01929eafb8b9954606a3c38b0332a987c2b3432388b4ee7365e54deae; fi
    test "$(sha256sum "$deb" | cut -d' ' -f1)" = "$pinned"
    test "$(dpkg-deb -f "$deb" Version)" = '2:4.0.2-3'
    test "$(dpkg-deb -f "$deb" Architecture)" = amd64
    dpkg-deb -x "$deb" "$report/packages/$package"
done
psfile=$(find "$report/packages/procps" -type f -name ps -print -quit)
libfile=$(find "$report/packages/libproc2-0" -type f -name 'libproc2.so.*' -print -quit)
test -n "$psfile" && test -n "$libfile"
cmp "$report/verified-ps" "$psfile"
cmp "$report/verified-libproc2" "$libfile"
cp -- "$project/scripts/guest-maintenance-apply.sh" "$report/payload/apply.sh"
( cd "$report/payload"; sha256sum launchpad-session launchpad-agent usr.local.bin.grok launchpad-session.service quiesce.py procps_4.0.2-3_amd64.deb libproc2-0_4.0.2-3_amd64.deb > SHA256SUMS )
extra_payload=()
if [ -d "$report/payload/security-packages" ]; then
    ( cd "$report/payload"; sha256sum security-packages/* >> SHA256SUMS )
    extra_payload=(security-packages)
fi
tar --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner -C "$report/payload" -czf "$report/upgrade.tar.gz" apply.sh SHA256SUMS launchpad-session launchpad-agent usr.local.bin.grok launchpad-session.service quiesce.py procps_4.0.2-3_amd64.deb libproc2-0_4.0.2-3_amd64.deb "${extra_payload[@]}"
sha256sum "$report/kernel" "$report/initrd" "$report/upgrade.tar.gz" "$report/payload/"*.deb > "$report/identity.sha256"

# A private runtime layout exercises the production manifest reader before
# activation. These are small maintenance inputs, not another app/template.
mkdir "$report/images"
cp -- "$report/kernel" "$report/initrd" "$report/upgrade.tar.gz" "$report/images/"
python3 - "$report" "$version" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
def artifact(name):
    return dict(file=name, sha256=hashlib.sha256((root / 'images' / name).read_bytes()).hexdigest())
manifest = dict(schema=1, version=sys.argv[2], kernel=artifact('kernel'), initrd=artifact('initrd'),
                payload=artifact('upgrade.tar.gz'), guestScriptSha256=hashlib.sha256((root / 'payload/launchpad-session').read_bytes()).hexdigest())
(root / 'images/maintenance.json').write_text(json.dumps(manifest, indent=2) + '\n')
PY
test "$(sha256sum "$candidate" | cut -d' ' -f1)" = "$expected"
printf 'Offline maintenance inputs staged; reviewed runtime and all sessions unchanged.\n'
