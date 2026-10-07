#!/usr/bin/env python3
"""Experimental Codex metadata reader; never starts/resumes/answers a turn.

Not wired into shipping launch. The pinned shared daemon's initialization changed
thread originator attribution in an owned test, so passive production acceptance
has failed. Preserve the narrow selection guard; do not broaden it to get a pass.
Unknown/ambiguous/unsupported state yields no fresh observation.
"""
import argparse
import base64
import collections
import datetime
import hashlib
import json
import pathlib
import queue
import re
import secrets
import socket
import struct
import subprocess
import threading
import time
import uuid

ALLOWED_READS = frozenset(('thread/loaded/list', 'thread/list', 'thread/read', 'thread/turns/list'))
MAX_MESSAGE = 512 * 1024
HANDSHAKE_SECONDS = 3
SAFE_ID = re.compile(r'[A-Za-z0-9_.:-]{1,128}\Z')


class ObservationUnavailable(Exception):
    """A fixed reason code, without provider response bodies or transcript text."""


class ReadOnlyProxy:
    def __init__(self, executable='codex', cwd=None, environment=None, diagnostics=None, socket_path=None):
        command = [executable, 'app-server', 'proxy']
        if socket_path is not None:
            command.extend(['--sock', str(socket_path)])
        self.process = subprocess.Popen(command, cwd=cwd, env=environment,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=diagnostics or subprocess.DEVNULL, bufsize=0)
        self.messages = queue.Queue(maxsize=64)
        self.calls = collections.deque(maxlen=64)
        self.next_id = 0
        self.closed = threading.Event()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        try:
            self._request('initialize', dict(clientInfo=dict(name='launchpad_activity_observer',
                title='LaunchPad activity observer', version='1'), capabilities=dict(experimentalApi=True)))
            self._write(dict(method='initialized', params={}))
        except Exception:
            self.close()
            raise

    def _write(self, value):
        if self.closed.is_set() or self.process.poll() is not None:
            raise ObservationUnavailable('proxy-ended')
        self.process.stdin.write((json.dumps(value, separators=(',', ':')) + '\n').encode())
        self.process.stdin.flush()

    def _read(self):
        try:
            while not self.closed.is_set():
                line = self.process.stdout.readline(MAX_MESSAGE + 1)
                if not line or len(line) > MAX_MESSAGE or not line.endswith(b'\n'):
                    break
                value = json.loads(line)
                if not isinstance(value, dict):
                    break
                # Discard unsolicited message bodies. An observer must never
                # answer a server request or expose transcripts through telemetry.
                if 'id' in value and 'method' not in value:
                    self.messages.put_nowait(value)
        except (OSError, ValueError, queue.Full):
            pass
        finally:
            self.closed.set()

    def _request(self, method, params):
        self.next_id += 1
        request_id = self.next_id
        self.calls.append(method)
        self._write(dict(id=request_id, method=method, params=params))
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            try:
                value = self.messages.get(timeout=min(0.1, max(0.01, deadline - time.monotonic())))
            except queue.Empty:
                if self.closed.is_set() or self.process.poll() is not None:
                    raise ObservationUnavailable('proxy-ended')
                continue
            if value.get('id') != request_id:
                continue
            if 'error' in value or not isinstance(value.get('result'), dict):
                raise ObservationUnavailable('unsupported-read:' + method)
            return value['result']
        raise ObservationUnavailable('read-timeout:' + method)

    def read(self, method, params=None):
        if method not in ALLOWED_READS:
            raise ValueError('Method is not an observational read')
        return self._request(method, params or {})

    def close(self):
        self.closed.set()
        try:
            self.process.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            self.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            # Own only this proxy child; never stop the shared daemon or TUI.
            self.process.terminate()
            try:
                self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=2)
        self.process.stdout.close()


class ReadOnlyUnixSocket(ReadOnlyProxy):
    """Bounded WebSocket client for the verified local daemon control socket.

    No listener/daemon is created. Only protocol ping responses and the
    same allowlisted metadata RPCs are sent; server approval requests are ignored.
    """
    def __init__(self, socket_path):
        self.socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.socket.settimeout(HANDSHAKE_SECONDS)
        self.closed = threading.Event()
        self.buffer = bytearray()
        self.calls = collections.deque(maxlen=64)
        self.next_id = 0
        try:
            deadline = time.monotonic() + HANDSHAKE_SECONDS
            self.socket.connect(str(socket_path))
            def handshake_timeout():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise ObservationUnavailable('websocket-handshake-timeout')
                self.socket.settimeout(remaining)
            key = base64.b64encode(secrets.token_bytes(16)).decode('ascii')
            handshake_timeout()
            self.socket.sendall(('GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\n'
                'Connection: Upgrade\r\nSec-WebSocket-Key: ' + key + '\r\n'
                'Sec-WebSocket-Version: 13\r\n\r\n').encode('ascii'))
            while b'\r\n\r\n' not in self.buffer:
                if len(self.buffer) >= 16384:
                    raise ObservationUnavailable('websocket-handshake-too-large')
                handshake_timeout()
                received = self.socket.recv(4096)
                if not received:
                    raise ObservationUnavailable('websocket-handshake-ended')
                self.buffer.extend(received)
            header, rest = bytes(self.buffer).split(b'\r\n\r\n', 1)
            self.buffer = bytearray(rest)
            lines = header.decode('ascii').split('\r\n')
            if len(header) > 16384 or not lines[0].startswith('HTTP/1.1 101 '):
                raise ObservationUnavailable('websocket-handshake-rejected')
            headers = {}
            for line in lines[1:]:
                name, value = line.split(':', 1)
                name = name.lower()
                if name in headers:
                    raise ObservationUnavailable('websocket-handshake-ambiguous')
                headers[name] = value.strip()
            expected = base64.b64encode(hashlib.sha1((key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode('ascii')).digest()).decode('ascii')
            if headers.get('sec-websocket-accept') != expected or headers.get('upgrade', '').lower() != 'websocket' \
                    or 'upgrade' not in [part.strip().lower() for part in headers.get('connection', '').split(',')] \
                    or 'sec-websocket-extensions' in headers or 'sec-websocket-protocol' in headers:
                raise ObservationUnavailable('websocket-handshake-unverified')
            self._request('initialize', dict(clientInfo=dict(name='launchpad_activity_observer',
                title='LaunchPad activity observer', version='1'), capabilities=dict(experimentalApi=True)))
            self._write(dict(method='initialized', params={}))
        except (UnicodeError, ValueError):
            self.close()
            raise ObservationUnavailable('websocket-handshake-invalid') from None
        except socket.timeout:
            self.close()
            raise ObservationUnavailable('websocket-handshake-timeout') from None
        except Exception:
            self.close()
            raise

    def _frame(self, opcode, payload):
        if len(payload) > MAX_MESSAGE or opcode >= 8 and len(payload) > 125:
            raise ObservationUnavailable('websocket-output-too-large')
        mask = secrets.token_bytes(4)
        size = len(payload)
        header = bytes([0x80 | opcode, 0x80 | size]) if size < 126 else \
            bytes([0x80 | opcode, 0x80 | 126]) + struct.pack('!H', size) if size < 65536 else \
            bytes([0x80 | opcode, 0x80 | 127]) + struct.pack('!Q', size)
        self.socket.sendall(header + mask + bytes(value ^ mask[index % 4] for index, value in enumerate(payload)))

    def _write(self, value):
        if self.closed.is_set():
            raise ObservationUnavailable('socket-ended')
        self._frame(1, json.dumps(value, separators=(',', ':')).encode('utf-8'))

    def _take(self, count, deadline):
        while len(self.buffer) < count:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise ObservationUnavailable('read-timeout')
            self.socket.settimeout(remaining)
            received = self.socket.recv(min(4096, count - len(self.buffer)))
            if not received:
                self.closed.set()
                raise ObservationUnavailable('socket-ended')
            self.buffer.extend(received)
        value = bytes(self.buffer[:count])
        del self.buffer[:count]
        return value

    def _text(self, deadline):
        message, fragmented = bytearray(), False
        for _ in range(1024):
            first, second = self._take(2, deadline)
            final, opcode, size = bool(first & 0x80), first & 15, second & 127
            if first & 0x70 or second & 0x80:
                raise ObservationUnavailable('unsupported-websocket-frame')
            if size == 126:
                size = struct.unpack('!H', self._take(2, deadline))[0]
                if size < 126:
                    raise ObservationUnavailable('invalid-websocket-length')
            elif size == 127:
                size = struct.unpack('!Q', self._take(8, deadline))[0]
                if size < 65536:
                    raise ObservationUnavailable('invalid-websocket-length')
            if opcode >= 8 and (not final or size > 125) or size > MAX_MESSAGE or opcode < 8 and len(message) + size > MAX_MESSAGE:
                raise ObservationUnavailable('websocket-message-too-large')
            payload = self._take(size, deadline)
            if opcode == 8:
                raise ObservationUnavailable('socket-ended')
            if opcode == 9:
                self._frame(10, payload)
                continue
            if opcode == 10:
                continue
            if opcode == 1 and not fragmented:
                fragmented = not final
            elif opcode == 0 and fragmented:
                pass
            else:
                raise ObservationUnavailable('unsupported-websocket-message')
            message.extend(payload)
            if final:
                return bytes(message).decode('utf-8')
        raise ObservationUnavailable('websocket-message-incomplete')

    def _request(self, method, params):
        self.next_id += 1
        request_id = self.next_id
        self.calls.append(method)
        self._write(dict(id=request_id, method=method, params=params))
        deadline = time.monotonic() + 3
        try:
            while time.monotonic() < deadline:
                value = json.loads(self._text(deadline))
                if not isinstance(value, dict):
                    raise ObservationUnavailable('unsupported-rpc-message')
                # No subscriptions, approval replies, transcript export or writes.
                if value.get('id') != request_id or 'method' in value:
                    continue
                if 'error' in value or not isinstance(value.get('result'), dict):
                    raise ObservationUnavailable('unsupported-read:' + method)
                return value['result']
        except (UnicodeError, ValueError):
            raise ObservationUnavailable('invalid-rpc-message') from None
        except socket.timeout:
            raise ObservationUnavailable('read-timeout:' + method) from None
        raise ObservationUnavailable('read-timeout:' + method)

    def close(self):
        # Own only this connection, never the shared daemon or interactive TUI.
        self.closed.set()
        self.socket.close()


def identifier(value):
    if not isinstance(value, str) or not SAFE_ID.fullmatch(value):
        raise ObservationUnavailable('invalid-identity')
    return value


def metadata(proxy, project, thread_id=None):
    project = str(pathlib.Path(project).resolve())
    loaded = proxy.read('thread/loaded/list').get('data')
    if not isinstance(loaded, list) or any(not isinstance(value, str) for value in loaded):
        raise ObservationUnavailable('unsupported-thread-list')
    matches, cursors, cursor = [], set(), None
    for page in range(10):
        params = dict(cwd=project, sourceKinds=['cli'], limit=10,
            sortKey='recency_at', useStateDbOnly=True)
        if cursor is not None:
            params['cursor'] = cursor
        response = proxy.read('thread/list', params)
        listed = response.get('data')
        if not isinstance(listed, list) or len(listed) > 10:
            raise ObservationUnavailable('unsupported-thread-list')
        matches.extend(row for row in listed if isinstance(row, dict) and row.get('id') in loaded
            and row.get('cwd') == project)
        if thread_id is not None and any(row.get('id') == thread_id for row in matches):
            break
        cursor = response.get('nextCursor')
        if cursor is None:
            break
        if not isinstance(cursor, str) or not cursor or len(cursor) > 512 or cursor in cursors:
            raise ObservationUnavailable('unsupported-thread-pagination')
        cursors.add(cursor)
    else:
        raise ObservationUnavailable('thread-pagination-incomplete')
    if thread_id is None:
        if len(matches) != 1:
            raise ObservationUnavailable('ambiguous-or-absent-main-thread')
        thread_id = identifier(matches[0].get('id'))
    thread_id = identifier(thread_id)
    if not any(row.get('id') == thread_id for row in matches):
        raise ObservationUnavailable('main-cli-thread-not-loaded')
    before = proxy.read('thread/read', dict(threadId=thread_id, includeTurns=False)).get('thread')
    turns = proxy.read('thread/turns/list', dict(threadId=thread_id, limit=1,
        sortDirection='desc', itemsView='notLoaded')).get('data')
    checked_turns = proxy.read('thread/turns/list', dict(threadId=thread_id, limit=1,
        sortDirection='desc', itemsView='notLoaded')).get('data')
    after = proxy.read('thread/read', dict(threadId=thread_id, includeTurns=False)).get('thread')
    if not isinstance(before, dict) or not isinstance(after, dict) or not isinstance(turns, list):
        raise ObservationUnavailable('unsupported-thread-state')
    if before.get('id') != thread_id or after.get('id') != thread_id or before.get('cwd') != project or after.get('cwd') != project:
        raise ObservationUnavailable('thread-identity-changed')
    def turn_key(rows):
        if not isinstance(rows, list) or len(rows) > 1 or rows and not isinstance(rows[0], dict):
            raise ObservationUnavailable('unsupported-turn-state')
        return (rows[0].get('id'), rows[0].get('status')) if rows else None
    if turn_key(turns) != turn_key(checked_turns):
        raise ObservationUnavailable('turn-changed-during-read')
    status = after.get('status')
    if before.get('status') != status or not isinstance(status, dict):
        raise ObservationUnavailable('state-changed-during-read')
    kind = status.get('type')
    if kind not in ('active', 'idle'):
        raise ObservationUnavailable('thread-not-current')
    flags = status.get('activeFlags', [])
    if not isinstance(flags, list) or flags:
        # No fabricated question identity from an aggregate waiting category.
        raise ObservationUnavailable('pending-request-identity-unavailable')
    turn = turns[0] if turns else None
    if turn is not None and not isinstance(turn, dict):
        raise ObservationUnavailable('unsupported-turn-state')
    run = identifier(turn.get('id')) if turn else None
    outcome = turn.get('status') if turn else None
    if (kind == 'active' and outcome != 'inProgress') or (kind == 'idle' and outcome == 'inProgress'):
        raise ObservationUnavailable('incoherent-thread-and-turn')
    if outcome is not None and outcome not in ('inProgress', 'completed', 'interrupted', 'failed'):
        raise ObservationUnavailable('unsupported-turn-outcome')
    return dict(threadId=thread_id, runId=run, state='Working' if kind == 'active' else 'Idle',
        runActive=kind == 'active', outcome=outcome)


class Producer:
    def __init__(self, generation, emit, sequence=0):
        if uuid.UUID(generation).hex != generation or not isinstance(sequence, int) or sequence < 0:
            raise ValueError('Invalid producer checkpoint')
        self.generation, self.emit, self.sequence = generation, emit, sequence
        self.previous = None

    def publish(self, value):
        thread = identifier(value['threadId'])
        run = value['runId']
        if run is not None:
            identifier(run)
        previous = self.previous
        # Only an explicitly terminal matching turn that was observed active
        # supplies an outcome. Existing history on first attach is only baseline.
        if previous and previous['threadId'] == thread and previous['runActive'] and previous['runId'] == run \
                and not value['runActive'] and value['outcome'] in ('completed', 'interrupted', 'failed'):
            kind = dict(completed='RunFinished', interrupted='Interrupted', failed='RunFailed')[value['outcome']]
            self.sequence += 1
            self.emit('LP-EVENT ', dict(version=1, generation=self.generation, sequence=self.sequence,
                eventId='codex-event-' + str(self.sequence), kind=kind, occurredUtc=self._now(),
                agentSessionId=thread, runId=run, mainRun=True))
        self.sequence += 1
        self.emit('LP-STATE ', dict(version=1, generation=self.generation, sequence=self.sequence,
            observationId='codex-state-' + str(self.sequence), occurredUtc=self._now(), agentSessionId=thread,
            state=value['state'], runId=run, questionId=None, runActive=value['runActive'], mainRun=True))
        self.previous = value

    @staticmethod
    def _now():
        return datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00', 'Z')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--generation', required=True)
    parser.add_argument('--project', required=True)
    parser.add_argument('--thread')
    parser.add_argument('--codex', default='codex')
    parser.add_argument('--socket', help='Verified local daemon socket; does not start a daemon')
    args = parser.parse_args()
    producer = Producer(args.generation, lambda prefix, value: print(prefix + json.dumps(value, separators=(',', ':')), flush=True))
    proxy = None
    thread = args.thread
    try:
        while True:
            try:
                if proxy is None:
                    proxy = ReadOnlyUnixSocket(args.socket) if args.socket else ReadOnlyProxy(args.codex, args.project)
                value = metadata(proxy, args.project, thread)
                thread = value['threadId']
                producer.publish(value)
            except (OSError, ObservationUnavailable):
                if proxy is not None:
                    proxy.close()
                    proxy = None
            time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        if proxy is not None:
            proxy.close()


if __name__ == '__main__':
    main()
