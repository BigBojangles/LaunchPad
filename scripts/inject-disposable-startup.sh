#!/usr/bin/env bash
# Trial repair ONLY in a newly created diagnostic overlay, never a main template.
set -euo pipefail
overlay=$(realpath -- "${1:?diagnostic overlay required}")
repair=$(realpath -- "${2:?staged repair required}")
template=$(realpath -- "${3:?backing template required}")
project=$(realpath -- "$(dirname -- "$0")/..")
case "$overlay" in
    "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;;
    *) printf 'Refusing to modify a disk outside disposable guest test output.\n' >&2; exit 2 ;;
esac
test "$overlay" != "$template"
test "$(sha256sum "$repair" | cut -d' ' -f1)" = 'abc5d2e8708ab68ea13eb287799bfc451f87ccf5feee561425f4fdfbda6a27ff'
perl -c "$repair"
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
# A relative portable backing path makes the same diagnostic overlay readable by
# both Windows QEMU and Linux libguestfs; -u changes overlay metadata only.
relative_backing=$(realpath --relative-to="$(dirname -- "$overlay")" "$template")
qemu-img rebase -u -f qcow2 -F qcow2 -b "$relative_backing" "$overlay"
guestfish --rw -a "$overlay" -i <<EOF
upload "$repair" /usr/local/bin/bl-proof.sh
chmod 0755 /usr/local/bin/bl-proof.sh
EOF
printf 'Startup repair installed only in disposable overlay.\n'
