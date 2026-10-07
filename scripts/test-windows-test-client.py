#!/usr/bin/env python3
"""Owned Linux codec checks; no VM boot, Windows process, host apply or network."""
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import subprocess
import sys
import uuid

source = pathlib.Path(__file__).with_name('windows-test-client.py')
spec = importlib.util.spec_from_file_location('windows_test_client', source)
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)
root = pathlib.Path(sys.argv[1])
root.mkdir(parents=True, exist_ok=True)
if len(sys.argv) > 2 and sys.argv[2] == 'verify-response':
    identity = json.loads((root / 'python-request-identity.json').read_text())
    destination = root / ('linux-dotnet-result-' + uuid.uuid4().hex)
    packet = (root / 'dotnet-response.bin').read_bytes()
    result = subprocess.run([sys.executable, str(source), 'unpack', '--generation', identity['generation'],
                             '--request-id', identity['requestId'], '--output', str(destination)],
                            input=packet, capture_output=True, timeout=10)
    assert result.returncode == 0, result.stderr.decode()
    assert (destination / 'artifacts' / 'result.txt').read_text() == 'Windows codec artifact'
    assert (destination / 'logs' / 'stdout.txt').read_text() == 'Windows codec log'
    accepted = json.loads((destination / 'result.json').read_text())
    assert accepted['exitCode'] == 7 and accepted['outcome'] == 'finished'
    report = dict(passed=True, responseSha256=hashlib.sha256(packet).hexdigest(), resultDirectory=str(destination),
                  clientSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                  scope='Real Linux pack -> Windows codec/injected executor -> Linux unpack; no VM transport or managed Windows launch')
    (root / 'codec-interop-proof-private.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))
    sys.exit(0)
fixture = root / ('linux-client-' + uuid.uuid4().hex)
fixture.mkdir()
project = fixture / 'project'
project.mkdir()
(project / 'source.txt').write_text('Unicode source: naïve project\n', encoding='utf-8')
generation, request_id = uuid.uuid4().hex, uuid.uuid4().hex
checks = []


def check(name, function):
    function()
    checks.append(name)


def paths():
    assert client.safe_path('folder/source.txt')
    for name in ('../escape', 'C:/private', 'directory/NUL.txt', '.launchpad-test/command.bin', 'folder\\escape'):
        assert not client.safe_path(name), name


def collision():
    for entries in ([dict(path='FILE', size=0, sha256='a' * 64), dict(path='file', size=0, sha256='a' * 64)],
                    [dict(path='folder', size=0, sha256='a' * 64), dict(path='folder/file', size=0, sha256='a' * 64)]):
        try:
            client.validate_files(entries, 20000, client.MAX_SNAPSHOT)
        except ValueError:
            continue
        raise AssertionError('Collision accepted')


def links():
    # Native Linux filesystem for the symlink fixture; keep its diagnostic path.
    import tempfile
    with tempfile.TemporaryDirectory(prefix='launchpad-client-links-') as temporary:
        directory = pathlib.Path(temporary)
        (directory / 'project').mkdir()
        (directory / 'outside').write_text('owned fixture')
        (directory / 'project' / 'linked').symlink_to(directory / 'outside')
        try:
            client.snapshot(directory / 'project')
        except ValueError:
            return
        raise AssertionError('Linked input accepted')


def packed_snapshot():
    result = subprocess.run([sys.executable, str(source), 'pack', '--project', str(project), '--generation', generation,
                             '--request-id', request_id, '--tool', 'dotnet', '--artifact', 'result.txt', '--', 'build'],
                            capture_output=True, timeout=10)
    assert result.returncode == 0, result.stderr.decode()
    wire = io.BytesIO(result.stdout)
    line = wire.readline()
    metadata = json.loads(wire.read(int(line.decode().split()[2])))
    assert metadata['arguments'] == ['build'] and metadata['generation'] == generation
    assert metadata['requestId'] == request_id and metadata['artifacts'] == ['result.txt']
    payload = wire.read(metadata['files'][0]['size'])
    assert payload == (project / 'source.txt').read_bytes()
    assert hashlib.sha256(payload).hexdigest() == metadata['files'][0]['sha256']
    assert wire.read() == client.END
    (root / 'python-request.bin').write_bytes(result.stdout)
    (root / 'python-request-identity.json').write_text(json.dumps(dict(generation=generation, requestId=request_id)), encoding='utf-8')


def response_roundtrip():
    data = b'owned selected artifact'
    response = dict(version=1, generation=generation, requestId=request_id, outcome='finished', exitCode=7,
                    error=None, logsTruncated=False, files=[dict(path='artifacts/result.txt', size=len(data), sha256=hashlib.sha256(data).hexdigest())])
    metadata = json.dumps(response).encode()
    packet = ('LP-WINDOWS-RESULT 1 %d\n' % len(metadata)).encode() + metadata + data + client.END
    destination = fixture / 'received'
    command = [sys.executable, str(source), 'unpack', '--generation', generation, '--request-id', request_id, '--output', str(destination)]
    result = subprocess.run(command, input=packet, capture_output=True, timeout=10)
    assert result.returncode == 0, result.stderr.decode()
    assert (destination / 'artifacts' / 'result.txt').read_bytes() == data
    assert json.loads((destination / 'result.json').read_text())['exitCode'] == 7
    refused = subprocess.run(command, input=packet, capture_output=True, timeout=10)
    assert refused.returncode != 0
    assert (destination / 'artifacts' / 'result.txt').read_bytes() == data


def wrong_result():
    response = dict(version=1, generation=generation, requestId=uuid.uuid4().hex, outcome='finished', exitCode=0, files=[])
    metadata = json.dumps(response).encode()
    packet = ('LP-WINDOWS-RESULT 1 %d\n' % len(metadata)).encode() + metadata + client.END
    destination = fixture / 'wrong-result'
    result = subprocess.run([sys.executable, str(source), 'unpack', '--generation', generation, '--request-id', request_id,
                             '--output', str(destination)], input=packet, capture_output=True, timeout=10)
    assert result.returncode != 0 and not destination.exists()


def pinned_ancestors():
    calls = []
    original = os.open
    def observed(path, flags, *args, **kwargs):
        calls.append((str(path), flags))
        return original(path, flags, *args, **kwargs)
    os.open = observed
    try:
        with client.opened(project / 'source.txt') as stream:
            assert stream.read() == (project / 'source.txt').read_bytes()
    finally:
        os.open = original
    assert len(calls) > 1
    assert all(flags & os.O_PATH and flags & os.O_DIRECTORY and flags & os.O_NOFOLLOW for _, flags in calls[:-1])
    assert not calls[-1][1] & os.O_PATH
    assert calls[-1][1] & os.O_NOFOLLOW and calls[-1][1] & os.O_NONBLOCK


def ordinary_files_only():
    import tempfile
    with tempfile.TemporaryDirectory(prefix='launchpad-client-opened-') as temporary:
        directory = pathlib.Path(temporary)
        (directory / 'real').mkdir()
        (directory / 'real' / 'source').write_text('owned source')
        (directory / 'parent-link').symlink_to(directory / 'real', target_is_directory=True)
        (directory / 'file-link').symlink_to(directory / 'real' / 'source')
        os.link(directory / 'real' / 'source', directory / 'hardlink')
        os.mkfifo(directory / 'fifo')
        for path in (directory / 'parent-link' / 'source', directory / 'file-link', directory / 'hardlink', directory / 'fifo'):
            try:
                with client.opened(path):
                    pass
            except (OSError, ValueError):
                continue
            raise AssertionError('Nonordinary/linked file accepted: ' + str(path))


def execute_only_parent():
    # Real DAC check in an owned native Linux fixture. This is not AppArmor or
    # the actual guest/account. Root only creates the fixture and drops child
    # credentials; no user account or system configuration is changed.
    import tempfile
    import signal
    assert os.geteuid() == 0, 'Explicit root fixture setup required; child runs as UID65534.'
    with tempfile.TemporaryDirectory(prefix='launchpad-client-search-only-') as temporary:
        directory = pathlib.Path(temporary); directory.chmod(0o711)
        parent = directory / 'search-only'; parent.mkdir(); parent.chmod(0o111)
        sample = parent / 'source'; sample.write_bytes(b'owned readable file'); sample.chmod(0o444)
        read_end, write_end = os.pipe()
        pid = os.fork()
        if pid == 0:
            os.close(read_end)
            try:
                signal.alarm(5)
                os.setgroups([]); os.setgid(65534); os.setuid(65534)
                denied = False
                try:
                    fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
                except PermissionError:
                    denied = True
                else:
                    os.close(fd)
                assert denied
                with client.opened(sample) as stream:
                    assert stream.read() == b'owned readable file'
                os.write(write_end, json.dumps(dict(uid=os.geteuid(), directoryReadDenied=denied, openedRead=True)).encode())
                os._exit(0)
            except BaseException as error:
                os.write(write_end, json.dumps(dict(error=type(error).__name__)).encode())
                os._exit(1)
        os.close(write_end)
        try:
            result = os.read(read_end, 4096)
            _, status = os.waitpid(pid, 0)
            assert os.waitstatus_to_exitcode(status) == 0, result.decode()
            receipt = json.loads(result)
            assert receipt == dict(uid=65534, directoryReadDenied=True, openedRead=True)
            (root / 'search-only-parent-proof-private.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
        finally:
            os.close(read_end)
            parent.chmod(0o700)


for name, function in [('safe paths', paths), ('case and directory collisions', collision), ('linked input refusal', links),
                       ('actual CLI snapshot pack', packed_snapshot), ('CLI result and overwrite refusal', response_roundtrip),
                       ('wrong result binding refusal', wrong_result), ('pinned directory references', pinned_ancestors),
                       ('ancestor/final links, hardlinks and FIFO refusal', ordinary_files_only),
                       ('nonroot readable file below execute-only ancestor', execute_only_parent)]:
    check(name, function)
report = dict(checks=checks, passed=len(checks), clientSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
              fixture=str(fixture), python=sys.version, scope='Linux codec only; no actual guest/host transport or Windows execution')
(root / 'linux-client-proof-private.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report))
