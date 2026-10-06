#!/usr/bin/env bash
# Download a coherent vendor fix set; verify Debian signatures and npm integrity.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
template=$(realpath -- "${1:?selected template required}")
expected=${2:?template hash required}
report=$(realpath -m -- "${3:?private package inputs directory required}")
case "$template" in "$(dirname -- "$project")"/build-launch-qemu/images/*.qcow2) ;; *) exit 2 ;; esac
case "$report" in "$project"/tests/LaunchPad.Tests/TestResults/migration/security-package-inputs-*) ;; *) exit 2 ;; esac
test ! -f "$report/security-packages.json"
test "$(sha256sum -- "$template" | cut -d' ' -f1)" = "$expected"
mkdir -p "$report/packages"
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$template" -i <<EOF
download /usr/share/keyrings/debian-archive-keyring.gpg "$report/debian-archive-keyring.gpg"
download /var/lib/dpkg/status "$report/guest-packages-before.txt"
download /usr/local/lib/node_modules/npm/package.json "$report/npm-before.json"
EOF
origin=https://deb.debian.org/debian-security
curl --fail --location --silent --show-error --max-time 120 "$origin/dists/bookworm-security/InRelease" -o "$report/InRelease"
gpgv --keyring "$report/debian-archive-keyring.gpg" --output "$report/Release-verified.txt" "$report/InRelease" > "$report/release-signature.txt" 2>&1
curl --fail --location --silent --show-error --max-time 120 "$origin/dists/bookworm-security/main/binary-amd64/Packages.xz" -o "$report/Packages.xz"
python3 - "$report" "$expected" "$project/scripts/guest-security-packages.json" <<'PY'
import base64, datetime, email.utils, hashlib, json, lzma, pathlib, subprocess, sys, tarfile
root = pathlib.Path(sys.argv[1])
lock = json.loads(pathlib.Path(sys.argv[3]).read_text())
assert lock['schema'] == 1
release = (root / 'Release-verified.txt').read_text()
valid_until = [line.split(': ',1)[1] for line in release.splitlines() if line.startswith('Valid-Until: ')]
assert len(valid_until) == 1 and email.utils.parsedate_to_datetime(valid_until[0]) > datetime.datetime.now(datetime.timezone.utc), 'Expired signed repository metadata'
sha_section = release.split('\nSHA256:\n', 1)[1].split('\nSHA512:', 1)[0]
match = [line.split() for line in sha_section.splitlines() if line.split() and line.split()[-1] == 'main/binary-amd64/Packages.xz']
assert len(match) == 1
data = (root / 'Packages.xz').read_bytes()
assert hashlib.sha256(data).hexdigest() == match[0][0] and len(data) == int(match[0][1]), 'Unsigned package index bytes'
index = lzma.decompress(data).decode()
packages = []
for name in ['perl-base', 'perl-modules-5.36', 'libperl5.36', 'perl']:
    matches = []
    for paragraph in index.split('\n\n'):
        fields = dict(line.split(': ', 1) for line in paragraph.splitlines() if ': ' in line and not line.startswith(' '))
        if fields.get('Package') == name and fields.get('Version') == '5.36.0-7+deb12u4': matches.append(fields)
    assert len(matches) == 1, ('Pinned vendor package unavailable', name)
    row = matches[0]
    assert row['SHA256'] == lock['debian']['packages'][name] and row['Version'] == lock['debian']['version'], 'Vendor bytes differ from reviewed pin'
    assert row['Filename'].startswith('pool/updates/main/p/perl/') and '..' not in row['Filename']
    url = 'https://deb.debian.org/debian-security/' + row['Filename']
    path = root / 'packages' / pathlib.PurePosixPath(row['Filename']).name
    subprocess.run(['curl', '--fail', '--location', '--silent', '--show-error', '--max-time', '120', url, '-o', str(path)], check=True)
    blob = path.read_bytes()
    assert hashlib.sha256(blob).hexdigest() == row['SHA256'] and len(blob) == int(row['Size'])
    actual = subprocess.check_output(['dpkg-deb', '-f', str(path), 'Package', 'Version', 'Architecture'], text=True)
    assert actual.splitlines() == ['Package: ' + name, 'Version: ' + row['Version'], 'Architecture: ' + row['Architecture']], actual
    assert row['Architecture'] in ['amd64', 'all']
    packages.append(dict(name=name, version=row['Version'], architecture=row['Architecture'], file=path.name, sha256=row['SHA256'], url=url))
metadata_path = root / 'npm-11.20.0-metadata.json'
if not metadata_path.exists():
    subprocess.run(['curl','--fail','--silent','--show-error','https://registry.npmjs.org/npm/11.20.0','-o',str(metadata_path)],check=True)
metadata = json.loads(metadata_path.read_text(encoding='utf-8-sig'))
assert metadata['name'] == 'npm' and metadata['version'] == '11.20.0'
assert metadata['version'] == lock['npm']['version'] and metadata['dist']['integrity'] == lock['npm']['integrity']
url = metadata['dist']['tarball']
assert url == 'https://registry.npmjs.org/npm/-/npm-11.20.0.tgz'
archive = root / 'packages/npm-11.20.0.tgz'
subprocess.run(['curl','--fail','--location','--silent','--show-error','--max-time','120',url,'-o',str(archive)],check=True)
blob = archive.read_bytes()
assert metadata['dist']['integrity'] == 'sha512-' + base64.b64encode(hashlib.sha512(blob).digest()).decode()
assert hashlib.sha256(blob).hexdigest() == lock['npm']['sha256']
versions = {}
with tarfile.open(archive) as tar:
    for member in tar:
        path = pathlib.PurePosixPath(member.name)
        assert not path.is_absolute() and '..' not in path.parts and path.parts[0] == 'package', 'Unsafe vendor archive member'
        if member.isfile() and member.name.endswith('/package.json'):
            value = json.load(tar.extractfile(member))
            if value.get('name') in ['npm','brace-expansion','ip-address','tar','undici','http-cache-semantics']:
                versions[member.name] = dict(name=value['name'], version=value['version'])
assert versions['package/package.json']['version'] == '11.20.0'
before = json.loads((root/'npm-before.json').read_text())
assert before['version'].split('.')[0] == '11', ('Do not silently change the npm major', before['version'])
manifest = dict(schema=1, baseTemplateSha256=sys.argv[2], debian=dict(releaseSha256=hashlib.sha256((root/'InRelease').read_bytes()).hexdigest(),
    indexSha256=hashlib.sha256(data).hexdigest(), keyringSha256=hashlib.sha256((root/'debian-archive-keyring.gpg').read_bytes()).hexdigest(),
    origin='https://deb.debian.org/debian-security', suite='bookworm-security', packages=packages),
    npm=dict(version='11.20.0', beforeVersion=before['version'], file=archive.name, url=url, integrity=metadata['dist']['integrity'],
        sha256=hashlib.sha256(blob).hexdigest(), bundledReviewVersions=versions))
(root/'security-packages.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps(dict(perlVersion='5.36.0-7+deb12u4', npmBefore=before['version'], npmAfter='11.20.0', dependencies=versions),indent=2))
PY
test "$(sha256sum -- "$template" | cut -d' ' -f1)" = "$expected"
sha256sum -- "$template" "$report/security-packages.json" > "$report/input-identities.sha256"
