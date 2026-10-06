#!/usr/bin/env bash
# Read-only package/build evidence for scanner applicability review.
set -euo pipefail
template=$(realpath -- "${1:?template required}")
expected=${2:?template hash required}
report=${3:?private report directory required}
project=$(realpath -- "$(dirname -- "$0")/..")
report=$(realpath -m -- "$report")
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/security/*/review) ;; *) exit 2 ;; esac
case "$template" in "$(dirname -- "$project")"/build-launch-qemu/images/*.qcow2|"$project"/tests/LaunchPad.Tests/TestResults/migration/*/template.qcow2) ;; *) exit 2 ;; esac
mkdir -p -- "$report"
test "$(sha256sum -- "$template" | cut -d' ' -f1)" = "$expected"
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
guestfish --ro -a "$template" -i > "$report/rootfs-collection.txt" <<EOF
download /var/lib/dpkg/info/zlib1g:amd64.list "$report/zlib-runtime-files.txt"
download /var/lib/dpkg/info/zlib1g-dev:amd64.list "$report/zlib-dev-files.txt"
download /usr/bin/perl "$report/perl-build-evidence"
tar-out /etc/apt/sources.list.d "$report/apt-sources-private.tar"
download /etc/apt/mirrors/debian-security.list "$report/security-repository-mirrors.txt"
EOF
file "$report/perl-build-evidence" > "$report/perl-build-type.txt"
sha256sum -- "$report/perl-build-evidence" > "$report/perl-build.sha256"
test "$(sha256sum -- "$template" | cut -d' ' -f1)" = "$expected"
printf '%s\n' "$expected" > "$report/template-before-after.sha256"
