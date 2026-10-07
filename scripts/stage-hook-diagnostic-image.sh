#!/usr/bin/env bash
# Offline disposable child only; preserve every original backing image/session.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
template=$(realpath -- "${1:?template required}")
expected=${2:?template SHA256 required}
kit=$(realpath -- "${3:?prepared private kit required}")
case "$kit" in "$project"/tests/LaunchPad.Tests/TestResults/migration/hook-diagnostic-kit-*) ;; *) exit 2 ;; esac
case "$template" in "$project"/../build-launch-qemu/images/*) ;; *)
    # realpath removes the project's ../ component.
    case "$template" in "$(realpath "$project/../build-launch-qemu/images")"/*) ;; *) exit 2 ;; esac ;;
esac
candidate="$kit/template.qcow2"
test ! -e "$candidate"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
qemu-img create -f qcow2 -F qcow2 -b "$template" "$candidate"
guestfish --rw -a "$candidate" -i <<EOF
upload "$kit/payload/bl-proof.sh" /usr/local/bin/bl-proof.sh
chmod 0750 /usr/local/bin/bl-proof.sh
chown 0 0 /usr/local/bin/bl-proof.sh
upload "$kit/payload/guest-hook-diagnostic.py" /usr/local/lib/launchpad/guest-hook-diagnostic.py
chmod 0644 /usr/local/lib/launchpad/guest-hook-diagnostic.py
chown 0 0 /usr/local/lib/launchpad/guest-hook-diagnostic.py
EOF
guestfish --ro -a "$candidate" -i <<EOF
download /usr/local/bin/bl-proof.sh "$kit/helper-from-image.sh"
download /usr/local/lib/launchpad/guest-hook-diagnostic.py "$kit/capture-from-image.py"
EOF
cmp "$kit/payload/bl-proof.sh" "$kit/helper-from-image.sh"
cmp "$kit/payload/guest-hook-diagnostic.py" "$kit/capture-from-image.py"
qemu-img check "$candidate" > "$kit/image-check.log"
test "$(sha256sum "$template" | cut -d' ' -f1)" = "$expected"
sha256sum "$candidate" > "$kit/image.sha256"
printf 'Diagnostic child staged; selected template and sessions unchanged.\n'
