#!/usr/bin/env python3
"""Owned Linux socket fixtures; no VM, Windows process, provider or host apply."""
import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import socket
import tempfile
import threading
import time
import uuid
from unittest.mock import patch


def load(name):
    path = pathlib.Path(__file__).with_name(name + '.py')
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


broker, codec = load('windows-test-broker'), load('windows-test-client')
checks = []
generation = uuid.uuid4().hex
key = bytes(range(32))  # Fixture key only.


def check(name, function):
    function()
    checks.append(name)


def write_stall():
    left, right = socket.socketpair()
    with left, right, left.makefile('rwb', buffering=0) as stream:
        wire = broker.Wire(stream, timeout=0.1)
        started = time.monotonic()
        try:
            wire.write(b'x' * (4 * 1024 * 1024))
        except TimeoutError:
            assert time.monotonic() - started < 2
            return
        raise AssertionError('Stalled broker write never expired')


def rejected_before_proof():
    # No host bytes should be sent on an invalid local handshake or wrong UID.
    for expected_uid in (os.getuid(), os.getuid() + 1):
        local, remote = socket.socketpair()
        remote.sendall(b'INVALID\n')
        with local, remote:
            try:
                broker.Broker(codec, expected_uid).forward(local, broker.Wire(io.BytesIO()), generation, key)
            except broker.LocalRequestError:
                continue
            raise AssertionError('Invalid local request was accepted')


def live_cli():
    # Actual Linux Unix sockets and SO_PEERCRED; Windows/device side is injected.
    assert os.getuid() == 0, 'Run this owned fixture as root for genuine client peer checks.'
    with tempfile.TemporaryDirectory(prefix='launchpad-broker-') as directory:
        root = pathlib.Path(directory)
        project, home = root / 'project', root / 'home'
        project.mkdir()
        home.mkdir()
        (project / 'source.txt').write_text('original snapshot')
        endpoint = root / 'bridge.sock'
        errors, requests = [], []
        listener = socket.socket(socket.AF_UNIX)
        listener.bind(str(endpoint))
        listener.listen(4)

        def serve_once(tamper=False, session=generation):
            device, host = socket.socketpair()
            device_stream = device.makefile('rwb', buffering=0)
            host_stream = host.makefile('rwb', buffering=0)

            def host_work():
                try:
                    wire = broker.Wire(host_stream, 3)
                    wire.write(('LP-WINDOWS-HOST 1 ' + session + ' ' + key.hex() + '\n').encode())
                    assert wire.line() == ('LP-WINDOWS-GUEST 1 ' + broker.sign(key, session, 'ready')).encode()
                    proof = wire.line()
                    _, raw, request = broker.metadata(wire, b'LP-WINDOWS-TEST 1 ', codec.MAX_META)
                    assert proof == ('LP-WINDOWS-PROOF 1 ' + broker.sign(key, session, 'request', raw)).encode()
                    body = io.BytesIO()
                    broker.payload(wire, body, request['files'], codec.END)
                    requests.append((raw, body.getvalue()))
                    data = b'owned test artifact'
                    response = dict(version=1, generation=session, requestId=request['requestId'], outcome='finished',
                                    exitCode=0, error=None, logsTruncated=False,
                                    files=[dict(path='artifacts/result.txt', size=len(data), sha256=hashlib.sha256(data).hexdigest())])
                    metadata = json.dumps(response).encode()
                    signature = '0' * 64 if tamper else broker.sign(key, session, 'response', metadata)
                    wire.write(('LP-WINDOWS-RESULT-PROOF 1 ' + signature + '\n').encode())
                    wire.write(('LP-WINDOWS-RESULT 1 %d\n' % len(metadata)).encode() + metadata + data + codec.END)
                except BaseException as error:
                    errors.append(error)
                finally:
                    host_stream.close()
                    host.close()

            def broker_work():
                try:
                    service = broker.Broker(codec, expected_uid=os.getuid())
                    wire = broker.Wire(device_stream, 3)
                    actual_generation, actual_key = service.handshake(wire)
                    # An invalid local client must not consume the host key.
                    while True:
                        local, _ = listener.accept()
                        with local:
                            try:
                                service.forward(local, wire, actual_generation, actual_key)
                            except broker.LocalRequestError:
                                continue
                        break
                except BaseException as error:
                    errors.append(error)
                finally:
                    device_stream.close()
                    device.close()

            threads = [threading.Thread(target=host_work, daemon=True), threading.Thread(target=broker_work, daemon=True)]
            for thread in threads:
                thread.start()
            return threads

        def finish(threads, allow_auth_failure=False):
            for thread in threads:
                thread.join(5)
                assert not thread.is_alive(), 'Owned bridge fixture did not finish'
            if allow_auth_failure:
                assert len(errors) == 1 and 'authentication' in str(errors[0]), errors
                errors.clear()
            else:
                assert not errors, errors

        args = argparse.Namespace(socket=str(endpoint), project=str(project), resume=None, output=str(root / 'result1'),
                                  tool='dotnet', request_id=None, program=None, cwd='.', timeout=30,
                                  interactive=False, artifact=['result.txt'], arguments=['build'])
        try:
            with patch.object(pathlib.Path, 'home', return_value=home), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                threads = serve_once()
                with socket.socket(socket.AF_UNIX) as bad:
                    bad.connect(str(endpoint))
                    bad.sendall(b'INVALID\n')
                    assert bad.recv(1) == b''
                assert codec.run(args) == 0
                finish(threads)
                assert (root / 'result1/artifacts/result.txt').read_bytes() == b'owned test artifact'
                bundle = next((home / '.local/state/launchpad/windows-tests').glob('*.request'))
                frozen = bundle.read_bytes()
                (project / 'source.txt').write_text('changed after original request')
                args.resume, args.output = str(bundle), str(root / 'result2')
                threads = serve_once()
                assert codec.run(args) == 0
                finish(threads)
                assert requests[0] == requests[1] and bundle.read_bytes() == frozen
                args.resume, args.request_id, args.output = None, None, str(root / 'tampered-result')
                threads = serve_once(tamper=True)
                try:
                    codec.run(args)
                except (ValueError, OSError):
                    pass
                else:
                    raise AssertionError('Tampered host response was accepted')
                finish(threads, allow_auth_failure=True)
                assert not (root / 'tampered-result').exists()
        finally:
            listener.close()


check('nonblocking write deadline', write_stall)
check('wrong UID / invalid handshake reject before proof', rejected_before_proof)
check('Unix broker CLI roundtrip / same-key invalid-client recovery / exact resume / tampered-response refusal', live_cli)
import sys
output = pathlib.Path(sys.argv[1])
output.mkdir(parents=True, exist_ok=True)
report = dict(passed=len(checks), checks=checks, uid=os.getuid(),
              brokerSha256=hashlib.sha256(pathlib.Path(broker.__file__).read_bytes()).hexdigest(),
              clientSha256=hashlib.sha256(pathlib.Path(codec.__file__).read_bytes()).hexdigest(),
              scope='Real Linux Unix sockets and client CLI; injected host/virtio and UID fixture; no VM, Windows restricted execution or installed guest proof')
(output / 'linux-broker-proof-private.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report))
