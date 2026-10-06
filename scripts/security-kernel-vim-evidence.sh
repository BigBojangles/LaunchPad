#!/usr/bin/env bash
# Exact-candidate, read-only files for grouped applicability review; no guest boot.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
candidate=$(realpath -- "${1:?candidate required}")
expected=${2:?candidate SHA256 required}
report=$(realpath -m -- "${3:?new private evidence directory required}")
case "$candidate" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-packages-*/template.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/security/*/review/*/rootfs) ;; *) exit 2 ;; esac
[[ "$expected" =~ ^[a-f0-9]{64}$ ]]
test ! -e "$report"
test "$(sha256sum -- "$candidate" | cut -d' ' -f1)" = "$expected"
mkdir -p -- "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
# The appliance kernel above is a collector prerequisite, never candidate evidence.
guestfish --ro -a "$candidate" -i > "$report/collection-private.txt" <<EOF
download /var/lib/dpkg/status "$report/package-status.txt"
download /boot/config-6.1.0-53-amd64 "$report/kernel-config.txt"
download /boot/grub/grub.cfg "$report/grub-config.txt"
download /var/lib/dpkg/info/linux-image-6.1.0-53-amd64.list "$report/kernel-files.txt"
download /var/lib/dpkg/info/linux-libc-dev:amd64.list "$report/header-files.txt"
download /var/lib/dpkg/info/vim.list "$report/vim-files.txt"
download /var/lib/dpkg/info/vim-tiny.list "$report/vim-tiny-files.txt"
download /var/lib/dpkg/info/vim-common.list "$report/vim-common-files.txt"
download /var/lib/dpkg/info/vim-runtime.list "$report/vim-runtime-files.txt"
checksum sha256 /boot/vmlinuz-6.1.0-53-amd64
checksum sha256 /usr/bin/vim.basic
checksum sha256 /usr/bin/vim.tiny
EOF
test "$(sha256sum -- "$candidate" | cut -d' ' -f1)" = "$expected"
python3 - "$report" "$expected" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
result = dict(candidateSha256=sys.argv[2], candidateUnchanged=True,
    readOnly=True, candidateBooted=False,
    files={p.name: hashlib.sha256(p.read_bytes()).hexdigest()
           for p in sorted(root.iterdir()) if p.is_file()},
    limitations='Installed package/config/file evidence only. Existing exact-candidate runtime witness must separately establish boot identity. Does not prove per-CVE trigger reachability, patch authenticity, native agent dependencies, or selected production image acceptance.')
(root/'evidence-private.json').write_text(json.dumps(result, indent=2)+'\n')
print('Read-only kernel/Vim package and configuration evidence complete.')
PY
