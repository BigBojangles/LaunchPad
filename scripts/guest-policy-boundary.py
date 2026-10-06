#!/usr/bin/env python3
"""Owned-fixture permission checks, no real data/device reads or host apply."""
import errno
import fcntl
import json
import os
import pathlib
import subprocess

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root
label = pathlib.Path('/proc/self/attr/current').read_text().strip()
assert label == 'launchpad-agent (enforce)'
assert os.getuid() == os.geteuid() == 1000
status = dict(line.split(':', 1) for line in pathlib.Path('/proc/self/status').read_text().splitlines() if ':' in line)
assert status['NoNewPrivs'].strip() == '1'
assert int(status['CapEff'].strip(), 16) == 0
results = []
for name in ['allowed-policy-write.txt', '.codex/policy-fixture.txt', '.claude/policy-fixture.txt']:
    destination = root / name if not name.startswith('.') else pathlib.Path('/home/builder') / name
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text('owned permission probe\n')
    assert destination.read_text() == 'owned permission probe\n'
    results.append(dict(path=str(destination), operation='write/read', allowed=True))
for agent in ['.codex', '.claude']:
    lock = pathlib.Path('/home/builder') / agent / 'owned-policy.lock'
    with lock.open('w') as stream:
        fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        stream.write('owned lock probe\n')
    lock.unlink()
directory_lock = pathlib.Path('/home/builder/.claude.json.lock')
directory_lock.mkdir()
(directory_lock / 'owned-probe').write_text('owned directory lock\n')
(directory_lock / 'owned-probe').unlink()
directory_lock.rmdir()
results.append(dict(operation='agent file/directory locks', allowed=True))
for path, operation in [('/etc/launchpad-policy-canary', 'write'),
                        ('/home/builder/other-project/private.txt', 'read'),
                        ('/home/builder/other-project/private.txt', 'write'),
                        ('/dev/hvc0', 'open'), ('/dev/vda', 'open'),
                        ('/dev/virtio-ports/fence', 'open'), ('/dev/ttyS0', 'open')]:
    try:
        # No truncation, creation, read or write. An unexpected open is closed
        # immediately and fails the check without touching the device/file.
        fd = os.open(path, os.O_RDONLY if operation == 'read' else os.O_WRONLY)
    except OSError as error:
        assert error.errno in (errno.EACCES, errno.EPERM), (path, error)
        results.append(dict(path=path, operation=operation, denied=True, errno=error.errno))
    else:
        os.close(fd)
        raise AssertionError('Unexpected access: ' + path)
child = subprocess.check_output(['/usr/bin/python3', '-c',
    "import pathlib; print(pathlib.Path('/proc/self/attr/current').read_text().strip())"], text=True).strip()
assert child == label
print('POLICY-BOUNDARY:' + json.dumps(dict(label=label, uid=os.getuid(), childLabel=child, results=results), separators=(',', ':')))
print('POLICY-BOUNDARY-DONE')
