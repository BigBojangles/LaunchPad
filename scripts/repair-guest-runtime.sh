#!/usr/bin/env bash
# Install an observed application dependency, not audit tools or policy changes.
set -euo pipefail
mode=${1:?inspect or stage required}
expected=${2:?reviewed template SHA256 required}
report=${3:?new private evidence directory required}
project=$(realpath -- "$(dirname -- "$0")/..")
runtime=$(realpath -- "$project/../build-launch-qemu")
template="$runtime/images/debian-12-builder.qcow2"
base="$runtime/images/debian-12-nocloud-amd64-20260601-2496.qcow2"
case "$mode" in inspect|stage) ;; *) exit 2 ;; esac
[[ "$expected" =~ ^[0-9a-f]{64}$ ]]
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
test "$(sha256sum "$base" | cut -d' ' -f1)" = '71fcc93f10366f208750147ba841cc211ad2b098c274fbf43950e39eee3cd468'
report=$(realpath -m -- "$report")
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
test ! -e "$report"
mkdir -p "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
sha256sum "$template" "$base" > "$report/images-before.sha256"
qemu-img check "$template" > "$report/qcow-before.txt"
guestfish --ro -a "$template" -i <<EOF
download /var/lib/dpkg/status "$report/packages-before.txt"
is-file /usr/bin/ps
EOF
if [ "$mode" = inspect ]; then exit 0; fi
# Never modify a backing template used by existing project overlays. Prove the
# dependency in a small disposable candidate with a portable relative chain.
candidate="$report/template.qcow2"
qemu-img create -f qcow2 -F qcow2 -b "$(realpath --relative-to="$report" "$template")" "$candidate"
trap 'sha256sum "$template" "$base" "$candidate" > "$report/images-after.sha256"' EXIT
virt-customize -a "$candidate" --install procps \
    --run-command 'set -eu; command -v ps; ps --version; dpkg-query -W procps libproc2-0' \
    > "$report/install-procps-private.log" 2>&1
guestfish --ro -a "$candidate" -i <<EOF
download /var/lib/dpkg/status "$report/packages-after.txt"
is-file /usr/bin/ps
EOF
qemu-img check "$candidate" > "$report/qcow-after.txt"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
prepare="$runtime/images/prepare/customize.sh"
cp -- "$prepare" "$report/customize-before.sh"
python3 - "$prepare" <<'PY'
import pathlib, sys
path = pathlib.Path(sys.argv[1])
text = path.read_text(encoding='utf-8')
old = '--install git,curl,ca-certificates,python3-pip,pkg-config,libssl-dev,zlib1g-dev,xz-utils'
new = old + ',procps'
if text.count(old) != 1 or new in text:
    raise SystemExit('Unexpected guest rebuild recipe; do not rewrite it automatically.')
path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')
PY
cp -- "$prepare" "$report/customize-after.sh"
printf 'Required Codex process utility staged; main template and existing sessions unchanged.\n'
