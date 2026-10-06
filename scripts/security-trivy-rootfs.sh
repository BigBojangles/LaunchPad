#!/usr/bin/env bash
# Exact candidate rootfs vulnerability scan through a read-only FUSE view.
set -euo pipefail
image=$(realpath -- "${1:?candidate image required}")
archive=$(realpath -- "${2:?pinned Trivy archive required}")
report=${3:?generated report directory required}
cache=${4:?generated database cache required}
project=$(realpath -- "$(dirname -- "$0")/..")
mkdir -p "$report" "$cache"
report=$(realpath -- "$report")
cache=$(realpath -- "$cache")
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
case "$cache" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
test "$(sha256sum "$archive" | cut -d' ' -f1)" = 'c6e65abddb348e25f10549df887045629cf28cc72453cd1c63acb717316b3f3f'
binary_dir="$cache/trivy-0.75.0"
mkdir -p "$binary_dir"
tar -xzf "$archive" -C "$binary_dir" trivy
trivy="$binary_dir/trivy"
chmod 0755 "$trivy"
sha256sum "$trivy" > "$report/trivy-binary.sha256"
sha256sum "$image" > "$report/candidate-before.sha256"
"$trivy" --version > "$report/tool-version-before.txt"
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
mountpoint=$(mktemp -d /tmp/launchpad-rootfs.XXXXXXXX)
mounted=0
cleanup() {
    if [ "$mounted" = 1 ]; then
        guestunmount "$mountpoint" || return
    fi
    # Only the empty temporary mount directory; never recursively remove data.
    rmdir "$mountpoint" || true
}
trap cleanup EXIT
guestmount --ro -a "$image" -i "$mountpoint"
mounted=1
set +e
"$trivy" rootfs --cache-dir "$cache/database" --scanners vuln --list-all-pkgs \
    --format json --timeout 20m --no-progress --output "$report/trivy-rootfs-private.json" \
    --skip-dirs "$mountpoint/proc" --skip-dirs "$mountpoint/sys" --skip-dirs "$mountpoint/dev" \
    "$mountpoint" > "$report/trivy-stdout.txt" 2> "$report/trivy-stderr.txt"
scan_exit=$?
set -e
printf '%s\n' "$scan_exit" > "$report/trivy-exit.txt"
"$trivy" --cache-dir "$cache/database" --version > "$report/tool-version-after.txt"
if [ -f "$cache/database/db/metadata.json" ]; then
    cp "$cache/database/db/metadata.json" "$report/database-metadata.json"
    sha256sum "$cache/database/db/trivy.db" > "$report/database.sha256"
fi
if [ -f "$cache/database/java-db/metadata.json" ]; then
    cp "$cache/database/java-db/metadata.json" "$report/java-database-metadata.json"
    sha256sum "$cache/database/java-db/trivy-java.db" > "$report/java-database.sha256"
fi
sha256sum "$image" > "$report/candidate-after.sha256"
cmp "$report/candidate-before.sha256" "$report/candidate-after.sha256"
python3 - "$report" <<'PY'
import collections
import json
import pathlib
import sys
root = pathlib.Path(sys.argv[1])
exit_code = int((root / 'trivy-exit.txt').read_text().strip())
scan_path = root / 'trivy-rootfs-private.json'
scan = json.loads(scan_path.read_text()) if scan_path.exists() else {}
severity = collections.Counter()
targets = []
for result in scan.get('Results', []):
    targets.append({'target': result.get('Target'), 'class': result.get('Class'), 'type': result.get('Type'), 'packageCount': len(result.get('Packages', []))})
    for vulnerability in result.get('Vulnerabilities', []):
        severity[vulnerability.get('Severity', 'UNKNOWN')] += 1
metadata_path = root / 'database-metadata.json'
java_metadata_path = root / 'java-database-metadata.json'
summary = {
    'schemaVersion': 1,
    'exitCode': exit_code,
    'candidateSha256': (root / 'candidate-before.sha256').read_text().split()[0],
    'candidateUnchanged': True,
    'readOnlyRootfsMount': True,
    'scannerIdentity': {'uid': __import__('os').getuid(), 'context': 'host-side WSL rootfs scanner, not unprivileged agent'},
    'toolVersion': '0.75.0',
    'database': json.loads(metadata_path.read_text()) if metadata_path.exists() else None,
    'databaseSha256': (root / 'database.sha256').read_text().split()[0] if (root / 'database.sha256').exists() else None,
    'javaDatabase': json.loads(java_metadata_path.read_text()) if java_metadata_path.exists() else None,
    'javaDatabaseSha256': (root / 'java-database.sha256').read_text().split()[0] if (root / 'java-database.sha256').exists() else None,
    'severityCounts': dict(severity),
    'targets': targets,
    'exclusions': ['/proc', '/sys', '/dev virtual-device trees', 'secret/misconfiguration/license scanners (this is the required vulnerability scan)', 'agent binary dependencies without supported package metadata need separate coverage review'],
    'findingsTriaged': False,
    'verdict': 'BLOCKED' if exit_code else 'UNTRIAGED',
}
(root / 'trivy-coverage.json').write_text(json.dumps(summary, indent=2))
print('Trivy exit:', exit_code)
print('Private coverage report:', root / 'trivy-coverage.json')
PY
exit "$scan_exit"
