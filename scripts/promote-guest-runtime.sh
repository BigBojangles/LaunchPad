#!/usr/bin/env bash
# Promote one reviewed delta; keep all old backing images and sessions intact.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
runtime=$(realpath -- "$project/../build-launch-qemu")
candidate=$(realpath -- "${1:?reviewed candidate required}")
expected=${2:?candidate SHA256 required}
version=${3:?version identifier required}
report=$(realpath -m -- "${4:?new private report directory required}")
case "$candidate" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*/template.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/*) ;; *) exit 2 ;; esac
[[ "$version" =~ ^[a-z0-9-]{1,64}$ ]]
test ! -e "$report"
test "$(sha256sum "$candidate" | cut -d' ' -f1)" = "$expected"
main="$runtime/images/debian-12-builder.qcow2"
base="$runtime/images/debian-12-nocloud-amd64-20260601-2496.qcow2"
mainhash=c7f1ea5e78a6efc729528d522f7745a45b1d6c3f253f6747ff1eb33e07ce8cd5
basehash=71fcc93f10366f208750147ba841cc211ad2b098c274fbf43950e39eee3cd468
test "$(sha256sum "$main" | cut -d' ' -f1)" = "$mainhash"
test "$(sha256sum "$base" | cut -d' ' -f1)" = "$basehash"
image="$runtime/images/debian-12-builder-$version.qcow2"
test ! -e "$image"
mkdir -p "$report"
sha256sum "$main" "$base" "$candidate" > "$report/before.sha256"
# Safe rebase copies all differences from the entire old backing chain.
# convert with a new backing alone assumes unchanged source backing content
# and can omit differences held in an intermediate dependency layer.
qemu-img create -f qcow2 -F qcow2 -b "$candidate" "$image"
qemu-img rebase -f qcow2 -F qcow2 -b "$main" "$image"
qemu-img rebase -u -f qcow2 -F qcow2 -b "$(basename -- "$main")" "$image"
qemu-img check "$image" > "$report/qcow-check.txt"
qemu-img compare -f qcow2 -F qcow2 "$candidate" "$image" > "$report/guest-data-compare.txt"
sha256sum "$main" "$base" "$candidate" > "$report/after.sha256"
cmp "$report/before.sha256" "$report/after.sha256"
python3 - "$image" "$main" "$base" "$version" "$report/runtime.json" <<'PY'
import hashlib, json, pathlib, sys
image, main, base, version, output = sys.argv[1:]
def record(file):
    with open(file, 'rb') as stream:
        return {'file': pathlib.Path(file).name, 'sha256': hashlib.file_digest(stream, 'sha256').hexdigest()}
manifest={'schema':1,'version':version,'image':record(image),'dependencies':[record(main),record(base)],
          'capabilities':['agent-choice','safe-import','safe-merge','content-return']}
pathlib.Path(output).write_text(json.dumps(manifest,indent=2)+'\n')
PY
sha256sum "$image" > "$report/promoted-image.sha256"
printf 'Versioned image staged and guest-data-equivalent; activation is a separate atomic manifest switch.\n'
