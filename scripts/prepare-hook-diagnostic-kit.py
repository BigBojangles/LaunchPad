#!/usr/bin/env python3
"""Create an additive diagnostic kit from exact installed maintenance bytes."""
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import tarfile

archive, expected, report = sys.argv[1:]
archive, report = pathlib.Path(archive), pathlib.Path(report)
assert hashlib.sha256(archive.read_bytes()).hexdigest() == expected.lower()
assert not report.exists()
report.mkdir()
payload = report / 'payload'
payload.mkdir()
with tarfile.open(archive) as source:
    for member in source.getmembers():
        path = pathlib.PurePosixPath(member.name)
        assert not path.is_absolute() and '..' not in path.parts
        assert member.isfile() or member.isdir(), 'No archive links/devices'
    source.extractall(payload, filter='data')
scripts = pathlib.Path(__file__).resolve().parent
original = payload / 'bl-proof.sh'
original_hash = hashlib.sha256(original.read_bytes()).hexdigest()
candidate = report / 'bl-proof.sh'
subprocess.run([sys.executable, str(scripts / 'stage-hook-diagnostics.py'), str(original),
                original_hash, str(candidate)], check=True)
subprocess.run(['perl', '-c', str(candidate)], check=True)
shutil.copyfile(candidate, original)
shutil.copyfile(scripts / 'guest-hook-diagnostic.py', payload / 'guest-hook-diagnostic.py')
apply = payload / 'apply.sh'
text = apply.read_text()
needle = 'install -o root -g root -m 0750 quiesce.py /usr/local/lib/launchpad/quiesce.py'
assert text.count(needle) == 1
text = text.replace(needle, needle + '\ninstall -o root -g root -m 0644 guest-hook-diagnostic.py /usr/local/lib/launchpad/guest-hook-diagnostic.py')
apply.write_text(text, newline='\n')
hashes = []
for file in sorted(payload.rglob('*')):
    if file.is_file() and file.name != 'SHA256SUMS':
        hashes.append(hashlib.sha256(file.read_bytes()).hexdigest() + '  ' + file.relative_to(payload).as_posix())
(payload / 'SHA256SUMS').write_text('\n'.join(hashes) + '\n')
with tarfile.open(report / 'upgrade.tar.gz', 'w:gz') as target:
    for file in sorted(payload.rglob('*')):
        if file.is_file():
            target.add(file, arcname=file.relative_to(payload).as_posix())
(report / 'kit-proof-private.json').write_text(json.dumps(dict(
    sourceArchiveSha256=expected.lower(), originalHelperSha256=original_hash,
    helperSha256=hashlib.sha256(original.read_bytes()).hexdigest(),
    payloadSha256=hashlib.sha256((report / 'upgrade.tar.gz').read_bytes()).hexdigest(),
    templateChanged=False, activated=False, liveCaptureVerified=False), indent=2) + '\n')
