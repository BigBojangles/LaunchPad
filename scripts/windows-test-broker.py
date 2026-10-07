#!/usr/bin/env python3
"""Root-owned virtio/Unix-socket bridge. Does not execute code or open supplied paths.

Install alongside the root-owned client module; builder talks only to the Unix
socket. Raw virtio devices and the host connection key are never passed to it.
"""
import hashlib
import hmac
import importlib.util
import json
import os
import pathlib
import select
import socket
import stat
import struct
import sys
import time

DEVICE = '/dev/virtio-ports/launchpad-windows-test'
DIRECTORY = pathlib.Path('/run/launchpad-windows-test')
SOCKET = DIRECTORY / 'bridge.sock'


def sign(key, generation, direction, metadata=b''):
    value = ('LaunchPad.WindowsTest.v1\n' + generation + '\n' + direction + '\n').encode('ascii')
    return hmac.new(key, value + hashlib.sha256(metadata).digest(), hashlib.sha256).hexdigest()


class Wire:
    def __init__(self, stream, timeout=30):
        self.stream = stream
        self.timeout = timeout
        self.fd = None
        if hasattr(stream, 'fileno'):
            try:
                self.fd = stream.fileno()
            except OSError:
                pass  # In-memory codec fixtures have no operating-system fd.
            if self.fd is not None:
                os.set_blocking(self.fd, False)

    def read(self, count):
        data = bytearray()
        deadline = time.monotonic() + self.timeout
        while len(data) < count:
            if self.fd is not None and not select.select([self.fd], [], [], max(0, deadline - time.monotonic()))[0]:
                raise TimeoutError('Windows test channel stalled.')
            try:
                chunk = os.read(self.fd, count - len(data)) if self.fd is not None else self.stream.read(count - len(data))
            except BlockingIOError:
                continue
            if not chunk:
                raise EOFError('Windows test channel closed.')
            data.extend(chunk)
            deadline = time.monotonic() + self.timeout
        return bytes(data)

    def line(self, limit=192):
        value = bytearray()
        while len(value) < limit:
            byte = self.read(1)
            if byte == b'\n':
                return bytes(value)
            value.extend(byte)
        raise ValueError('Windows test header too large.')

    def write(self, value):
        left = memoryview(value)
        deadline = time.monotonic() + self.timeout
        while left:
            if self.fd is not None and not select.select([], [self.fd], [], max(0, deadline - time.monotonic()))[1]:
                raise TimeoutError('Windows test channel stalled.')
            try:
                count = os.write(self.fd, left) if self.fd is not None else self.stream.write(left)
            except BlockingIOError:
                continue
            if not count:
                raise EOFError('Windows test channel closed.')
            left = left[count:]
            deadline = time.monotonic() + self.timeout


class LocalRequestError(ValueError):
    """Rejected before any host proof was sent; keep the current connection."""


def metadata(wire, prefix, maximum):
    line = wire.line()
    if not line.startswith(prefix) or not line[len(prefix):].isdigit():
        raise ValueError('Invalid Windows test frame.')
    size = int(line[len(prefix):])
    if not 2 <= size <= maximum:
        raise ValueError('Windows test metadata exceeds its limit.')
    value = wire.read(size)
    return line + b'\n', value, json.loads(value)


def payload(source, destination, files, end):
    for file in files:
        left = file['size']
        digest = hashlib.sha256()
        while left:
            chunk = source.read(min(65536, left))
            digest.update(chunk)
            destination.write(chunk)
            left -= len(chunk)
        if digest.hexdigest().lower() != file['sha256'].lower():
            raise ValueError('Windows test payload hash mismatch.')
    if source.read(len(end)) != end:
        raise ValueError('Windows test completion marker is missing.')
    destination.write(end)


class Broker:
    def __init__(self, codec, expected_uid=1000):
        self.codec, self.expected_uid = codec, expected_uid

    def handshake(self, device):
        parts = device.line().decode('ascii').split(' ')
        if len(parts) != 4 or parts[:2] != ['LP-WINDOWS-HOST', '1']:
            raise ValueError('Invalid host bridge handshake.')
        generation = self.codec.identifier(parts[2])
        key = bytes.fromhex(parts[3])
        if len(key) != 32:
            raise ValueError('Invalid host bridge key.')
        device.write(('LP-WINDOWS-GUEST 1 ' + sign(key, generation, 'ready') + '\n').encode('ascii'))
        return generation, key

    def forward(self, client, device, generation, key):
        local = Wire(client.makefile('rwb', buffering=0))
        started = False
        try:
            _, uid, _ = struct.unpack('3i', client.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
            if uid != self.expected_uid:
                raise PermissionError('Only the coding account may request Windows tests.')
            if local.line() != b'LP-WINDOWS-OPEN 1':
                raise ValueError('Invalid local Windows test handshake.')
            local.write(('LP-WINDOWS-SESSION 1 ' + generation + '\n').encode('ascii'))
            # The client hashes/freezes the snapshot after obtaining generation.
            # This is preparation, not a started Windows process.
            local.timeout = 600
            header, raw, request = metadata(local, b'LP-WINDOWS-TEST 1 ', self.codec.MAX_META)
            local.timeout = 30
            if not isinstance(request, dict):
                raise ValueError('Invalid Windows test request metadata.')
            if request.get('generation') != generation:
                raise ValueError('This saved request belongs to another VM session.')
            self.codec.identifier(request.get('requestId', ''))
            duration = request.get('timeoutSeconds')
            if type(duration) is not int or not 1 <= duration <= 7200:
                raise ValueError('Invalid Windows test duration.')
            self.codec.validate_files(request.get('files'), 20000, self.codec.MAX_SNAPSHOT)
            started = True
            device.write(('LP-WINDOWS-PROOF 1 ' + sign(key, generation, 'request', raw) + '\n').encode('ascii'))
            device.write(header)
            device.write(raw)
            payload(local, device, request['files'], self.codec.END)
            device.timeout = duration + 90
            proof = device.line()
            device.timeout = 30
            header, raw, response = metadata(device, b'LP-WINDOWS-RESULT 1 ', self.codec.MAX_META)
            if not isinstance(response, dict):
                raise ValueError('Invalid Windows test result metadata.')
            expected = ('LP-WINDOWS-RESULT-PROOF 1 ' + sign(key, generation, 'response', raw)).encode('ascii')
            if not hmac.compare_digest(proof, expected) or response.get('generation') != generation or response.get('requestId') != request['requestId']:
                raise ValueError('Windows test result authentication failed.')
            self.codec.validate_files(response.get('files'), 130, self.codec.MAX_RESULT)
            # Continue draining a completed result after the local client goes
            # away. Its next exact-ID request can retrieve the frozen host copy.
            connected = True
            for chunk in (header, raw):
                try:
                    local.write(chunk)
                except (OSError, EOFError):
                    connected = False

            class Reply:
                def write(self, value):
                    nonlocal connected
                    if connected:
                        try:
                            local.write(value)
                        except (OSError, EOFError):
                            connected = False

            payload(device, Reply(), response['files'], self.codec.END)
        except (OSError, EOFError, ValueError, KeyError, TypeError, RecursionError) as error:
            if not started:
                raise LocalRequestError(str(error)) from error
            raise
        finally:
            local.stream.close()


def load_codec():
    path = pathlib.Path(__file__).with_name('windows-test-client.py')
    info = path.stat()
    if info.st_uid != 0 or info.st_mode & 0o022 or not stat.S_ISREG(info.st_mode):
        raise PermissionError('The Windows test codec must be root-owned and not writable by the coding account.')
    spec = importlib.util.spec_from_file_location('launchpad_windows_test_client', path)
    codec = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(codec)
    return codec


def main():
    if os.getuid() != 0:
        raise PermissionError('Only the trusted guest service may open the bridge device.')
    codec = load_codec()
    if not DIRECTORY.exists():
        DIRECTORY.mkdir(mode=0o755)
    info = DIRECTORY.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
        raise PermissionError('Unsafe Windows test socket directory.')
    import fcntl
    lock = os.open(DIRECTORY / 'owner.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if SOCKET.exists():
        info = SOCKET.lstat()
        if not stat.S_ISSOCK(info.st_mode):
            raise ValueError('Preserve the unexpected Windows test socket path.')
        SOCKET.unlink()
    server = socket.socket(socket.AF_UNIX)
    server.bind(str(SOCKET))
    os.chmod(SOCKET, 0o600)
    os.chown(SOCKET, 1000, 1000)
    server.listen(4)
    broker = Broker(codec)
    try:
        while True:
            # Host keys live only in this root service's memory. A new host
            # connection delivers another key; requests/results are not logged.
            try:
                with open(DEVICE, 'r+b', buffering=0) as stream:
                    info = os.fstat(stream.fileno())
                    if not stat.S_ISCHR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077:
                        raise PermissionError('The Windows test virtio port must be root-only.')
                    device = Wire(stream, timeout=75)
                    generation, key = broker.handshake(device)
                    # No short idle expiry: long agent runs are normal.
                    while True:
                        readable = select.select([server, stream], [], [], 30)[0]
                        if stream in readable:
                            raise EOFError('Host changed while waiting for a test.')
                        if server in readable:
                            client, _ = server.accept()
                            with client:
                                try:
                                    broker.forward(client, device, generation, key)
                                except LocalRequestError:
                                    continue
                            break
            except (OSError, EOFError, ValueError, KeyError, TypeError, RecursionError):
                # A partial transfer is never promoted to success or replayed.
                # Host timeouts/closed connections retain its failed ledger.
                time.sleep(0.25)
    finally:
        server.close()
        os.close(lock)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, PermissionError) as error:
        print('Windows test broker startup failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
