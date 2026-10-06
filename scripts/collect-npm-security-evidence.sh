#!/usr/bin/env bash
# One readonly collection of the exact candidate's repaired package/usage bytes.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
candidate=$(realpath -- "${1:?private candidate required}")
expected=${2:?candidate SHA256 required}
report=$(realpath -m -- "${3:?new private evidence directory required}")
case "$candidate" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-packages-*/template.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-packages-*/evidence) ;; *) exit 2 ;; esac
test ! -e "$report"
test "$(sha256sum -- "$candidate" | cut -d' ' -f1)" = "$expected"
mkdir -p "$report"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$candidate" -i <<EOF
download /usr/local/share/launchpad/npm-security-patches.json "$report/npm-patch-receipt.json"
download /usr/local/share/launchpad/security-packages.json "$report/package-input-receipt.json"
download /usr/local/lib/node_modules/npm/node_modules/make-fetch-happen/lib/cache/policy.js "$report/npm-cache-policy.js"
download /usr/local/lib/node_modules/npm/node_modules/make-fetch-happen/package.json "$report/npm-cache-package.json"
download /usr/local/lib/node_modules/npm/node_modules/http-cache-semantics/package.json "$report/cache-semantics-package.json"
download /usr/local/lib/node_modules/npm/node_modules/http-cache-semantics/index.js "$report/cache-semantics-index.js"
download /usr/local/lib/node_modules/npm/node_modules/brace-expansion/package.json "$report/brace-expansion-package.json"
download /usr/local/lib/node_modules/npm/node_modules/undici/package.json "$report/undici-package.json"
download /var/lib/dpkg/info/zlib1g:amd64.list "$report/zlib1g-files.txt"
download /var/lib/dpkg/info/zlib1g-dev:amd64.list "$report/zlib1g-dev-files.txt"
download /var/lib/dpkg/status "$report/package-status.txt"
EOF
test "$(sha256sum -- "$candidate" | cut -d' ' -f1)" = "$expected"
python3 - "$report" "$expected" <<'PY'
import hashlib, json, pathlib, sys
root=pathlib.Path(sys.argv[1])
patches=json.loads((root/'npm-patch-receipt.json').read_text())
assert [(r['name'],r['version']) for r in patches['patches']]==[('brace-expansion','5.0.12'),('undici','6.28.1')]
assert json.loads((root/'brace-expansion-package.json').read_text())['version']=='5.0.12'
assert json.loads((root/'undici-package.json').read_text())['version']=='6.28.1'
assert 'shared: false' in (root/'npm-cache-policy.js').read_text()
for name in ('zlib1g','zlib1g-dev'): assert 'minizip' not in (root/(name+'-files.txt')).read_text().lower()
files={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.iterdir()) if p.is_file()}
result=dict(candidateSha256=sys.argv[2],readOnly=True,candidateUnchanged=True,files=files,patches=patches,
  npmCachePackage=json.loads((root/'npm-cache-package.json').read_text()),
  cacheSemanticsPackage=json.loads((root/'cache-semantics-package.json').read_text()),
  limitations='Read-only exact package/usage bytes, not security acceptance. shared:false source needs actual caller/control evidence and applies only to bundled npm; arbitrary user servers/custom library use and native binaries remain separate coverage.')
(root/'package-usage-evidence-private.json').write_text(json.dumps(result,indent=2)+'\n')
print('Exact npm repair/usage evidence collected without candidate writes.')
PY
