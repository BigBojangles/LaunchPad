#!/usr/bin/env bash
# Apply the verified serial-marker relay only to the audited development template.
# Existing session disks are never opened or replaced by this operation.
set -euo pipefail
template=$(realpath -- "${1:?audited template required}")
repair=$(realpath -- "${2:?verified repair required}")
prepare=$(realpath -- "${3:?existing preparation script required}")
report=${4:?private evidence directory required}
project=$(realpath -- "$(dirname -- "$0")/..")
runtime=$(realpath -- "$project/../build-launch-qemu")
test "$template" = "$runtime/images/debian-12-builder.qcow2"
test "$prepare" = "$runtime/images/prepare/bl-proof.sh"
mkdir -p "$report"
report=$(realpath -- "$report")
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
test "$(sha256sum "$template" | cut -d' ' -f1)" = '4a003c3e7a42038b9f8168ae325ec441e79a0dc852914d7360870e3351b02048'
test "$(sha256sum "$prepare" | cut -d' ' -f1)" = '9b2143016627be60435394dc17bb5cbcf7a79343b53ca5de8dd59311ff7c4d61'
test "$(sha256sum "$repair" | cut -d' ' -f1)" = 'abc5d2e8708ab68ea13eb287799bfc451f87ccf5feee561425f4fdfbda6a27ff'
test ! -e "$report/bl-proof-before.sh"
perl -c "$repair"
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
base="$runtime/images/debian-12-nocloud-amd64-20260601-2496.qcow2"
test "$(sha256sum "$base" | cut -d' ' -f1)" = '71fcc93f10366f208750147ba841cc211ad2b098c274fbf43950e39eee3cd468'
sha256sum "$template" "$base" > "$report/images-before.sha256"
qemu-img check "$template" > "$report/qcow-before.txt"
guestfish --ro -a "$template" -i <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-before.sh"
EOF
test "$(sha256sum "$report/bl-proof-before.sh" | cut -d' ' -f1)" = '9b2143016627be60435394dc17bb5cbcf7a79343b53ca5de8dd59311ff7c4d61'
guestfish --rw -a "$template" -i <<EOF
upload "$repair" /usr/local/bin/bl-proof.sh
chmod 0755 /usr/local/bin/bl-proof.sh
EOF
guestfish --ro -a "$template" -i <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-after.sh"
EOF
cmp "$repair" "$report/bl-proof-after.sh"
qemu-img check "$template" > "$report/qcow-after.txt"
# Change the existing rebuild input only after the live guest contents verify.
cp -- "$repair" "$prepare"
cmp "$repair" "$prepare"
sha256sum "$template" "$base" > "$report/images-after.sha256"
test "$(tail -n 1 "$report/images-before.sha256")" = "$(tail -n 1 "$report/images-after.sha256")"
printf 'Applied verified startup relay; session overlays and original base unchanged.\n'
cat "$report/images-after.sha256"
