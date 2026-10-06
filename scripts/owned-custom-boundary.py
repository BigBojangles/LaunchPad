#!/usr/bin/env python3
"""Actual custom-program probes against only owned files and host listeners."""
import errno
import json
import os
import pathlib
import socket
import subprocess
import sys
import urllib.error
import urllib.request


def identity():
    status = dict(line.split(':', 1) for line in pathlib.Path('/proc/self/status').read_text().splitlines() if ':' in line)
    return dict(uid=os.getuid(), euid=os.geteuid(), gid=os.getgid(),
                label=pathlib.Path('/proc/self/attr/current').read_text().strip(),
                noNewPrivileges=status['NoNewPrivs'].strip(), capabilities=status['CapEff'].strip())


if len(sys.argv) == 2 and sys.argv[1] == 'child':
    print(json.dumps(identity()))
    raise SystemExit(0)

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root
targets = json.loads((root / 'owned-targets.json').read_text())
report = dict(identity=identity(), child=None, allowed=[], devices=[], network=[], guestLoopback=[],
              external=None, failures=[], complete=False)
try:
    current = report['identity']
    assert current['uid'] == current['euid'] == 1000 and current['gid'] == 1000, current
    assert current['label'] == 'unconfined', current
    assert current['noNewPrivileges'] == '1' and int(current['capabilities'], 16) == 0, current
    child = json.loads(subprocess.check_output([sys.executable, __file__, 'child'], text=True, timeout=5))
    report['child'] = child
    assert child == current, child
    for path in [root / 'custom-owned-write.txt', pathlib.Path('/home/builder/custom-owned-state.txt')]:
        path.write_text(targets['fixtureId'])
        assert path.read_text() == targets['fixtureId']
        report['allowed'].append(dict(path=str(path), readWrite=True))
    for name in ['/dev/vda', '/dev/virtio-ports/fence', '/dev/virtio-ports/status', '/dev/ttyS0']:
        try:
            fd = os.open(name, os.O_RDWR) # Never create, truncate, read or write a device.
        except OSError as error:
            denied = error.errno in (errno.EACCES, errno.EPERM)
            report['devices'].append(dict(path=name, opened=False, errno=error.errno, denied=denied))
            if not denied:
                report['failures'].append('Unexpected device outcome: ' + name)
        else:
            os.close(fd)
            report['devices'].append(dict(path=name, opened=True, denied=False))
            report['failures'].append('Unexpected privileged device open: ' + name)
    for target in targets['network']:
        entry = dict(target=target, connected=False, errno=None, error=None)
        try:
            with socket.create_connection((target['address'], target['port']), timeout=2):
                entry['connected'] = True # Connect only; no service commands or data sent.
        except OSError as error:
            entry['errno'] = error.errno
            entry['error'] = type(error).__name__
        report['network'].append(entry)
        if entry['connected']:
            report['failures'].append('Owned host listener reachable: ' + target['name'])
    # Actual agent-owned development listeners remain inside this guest.
    for family, address in [(socket.AF_INET, '127.0.0.1'), (socket.AF_INET6, '::1')]:
        entry = dict(address=address, allowed=False)
        try:
            with socket.socket(family, socket.SOCK_STREAM) as listener:
                listener.settimeout(2)
                listener.bind((address, 0))
                listener.listen(1)
                with socket.socket(family, socket.SOCK_STREAM) as client:
                    client.settimeout(2)
                    client.connect((address, listener.getsockname()[1]))
                    with listener.accept()[0] as accepted:
                        accepted.settimeout(2)
                        token = targets['fixtureId'].encode('ascii')
                        client.sendall(token)
                        received = accepted.recv(128)
                        accepted.sendall(received)
                        entry['allowed'] = received == token and client.recv(128) == token
        except OSError as error:
            entry.update(errno=error.errno, error=type(error).__name__)
        report['guestLoopback'].append(entry)
        if not entry['allowed']:
            report['failures'].append('Guest development loopback unavailable: ' + address)
    # One ordinary HTTPS HEAD, with certificate verification, not a port scan.
    # DNS and public Internet are positive controls separate from host denial.
    external = dict(host='example.com', url='https://example.com/', dns=False, https=False)
    try:
        external['addresses'] = sorted(set(item[4][0] for item in socket.getaddrinfo(
            external['host'], 443, type=socket.SOCK_STREAM)))
        external['dns'] = bool(external['addresses'])
        request = urllib.request.Request(external['url'], method='HEAD')
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                external.update(https=True, status=response.status)
        except urllib.error.HTTPError as response:
            external.update(https=True, status=response.code)
            response.close()
    except (OSError, urllib.error.URLError) as error:
        external.update(error=type(error).__name__, message=str(error)[:512])
    report['external'] = external
    if not external['dns'] or not external['https']:
        report['failures'].append('Normal external DNS/HTTPS unavailable')
    report['guestHostMounts'] = [line for line in pathlib.Path('/proc/mounts').read_text().splitlines()
                               if line.split()[2] in ('9p', 'virtiofs', 'cifs', 'smb3')]
    if report['guestHostMounts']:
        report['failures'].append('Unexpected direct shared filesystem mount')
    report['complete'] = True
except Exception as error:
    report['failures'].append(type(error).__name__ + ': ' + str(error))
finally:
    (root / 'custom-boundary.json').write_text(json.dumps(report, indent=2) + '\n')
    print('CUSTOM-BOUNDARY-DONE', flush=True)
    input() # Preserve foreground ownership until host receives the report/export.
