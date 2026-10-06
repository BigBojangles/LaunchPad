#!/usr/bin/env bash
# Read-only package evidence for vendor applicability review, not an agent audit.
set -euo pipefail
image=$(realpath -- "${1:?candidate image required}")
report=${2:?private generated output required}
project=$(realpath -- "$(dirname -- "$0")/..")
mkdir -p "$report"
report=$(realpath -- "$report")
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/*) ;; *) exit 2 ;; esac
export LIBGUESTFS_BACKEND=direct
if [ -f '/mnt/c/Program Files/WSL/tools/kernel' ]; then
    export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
    export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
fi
sha256sum "$image" > "$report/candidate-before.sha256"
guestfish --ro -a "$image" -i > "$report/package-evidence.txt" <<EOF
file /usr/bin/perl
cat /usr/lib/x86_64-linux-gnu/perl/5.36/Config_heavy.pl
cat /etc/os-release
EOF
sha256sum "$image" > "$report/candidate-after.sha256"
cmp "$report/candidate-before.sha256" "$report/candidate-after.sha256"
python3 - "$report" <<'PY'
import json, pathlib, re, sys
root = pathlib.Path(sys.argv[1])
raw = (root / 'package-evidence.txt').read_text()
fields = {}
for field in ('archname', 'ptrsize', 'sizesize', 'ssizetype', 'use64bitint'):
    match = re.search(r'^' + field + r"='([^']*)'", raw, re.M)
    fields[field] = match.group(1) if match else None
result = {
    'candidateSha256': (root / 'candidate-before.sha256').read_text().split()[0],
    'candidateUnchanged': True,
    'readOnlyInspection': True,
    'perlElfDescription': raw.splitlines()[0] if raw else None,
    'perlConfig': fields,
    'limitations': 'Package-file inspection only; does not execute Perl or cover bundled alternative interpreters.'
}
(root / 'package-evidence.json').write_text(json.dumps(result, indent=2))
print(json.dumps(result, indent=2))
PY
