#!/usr/bin/env bash
# Inspect selected non-secret guest inputs without executing or modifying the image.
set -euo pipefail
image=${1:?candidate image required}
report=${2:?generated report directory required}
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
mkdir -p "$report"
guestfish --ro -a "$image" -i > "$report/guest-inputs.txt" <<EOF
download /usr/local/bin/bl-proof.sh "$report/bl-proof-from-image.sh"
download /etc/udev/rules.d/99-bl-fence.rules "$report/fence-rules-from-image.txt"
stat /usr/local/bin/grok
stat /usr/local/bin/codex
stat /usr/local/bin/claude
readlink /usr/local/bin/codex
readlink /usr/local/bin/claude
cat /etc/systemd/system/bl-proof.service
EOF
sha256sum "$report/bl-proof-from-image.sh"
printf 'Read-only guest inputs saved: %s\n' "$report"
