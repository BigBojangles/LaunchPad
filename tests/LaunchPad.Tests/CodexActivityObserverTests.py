"""Passive-observer boundary and history checks, without an agent/model service."""
import importlib.util
import base64
import collections
import hashlib
import io
import json
import pathlib
import queue
import socket
import struct
import tempfile
import threading
import time
import types
import unittest
from unittest import mock
import uuid

spec = importlib.util.spec_from_file_location('codex_activity', pathlib.Path(__file__).resolve().parents[2] / 'scripts/codex-activity-observer.py')
observer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(observer)
PROJECT = str((pathlib.Path.cwd() / 'owned-observer-project').resolve())


class Proxy:
    def __init__(self, turns=None, flags=None, kind='active', matches=None):
        self.turns = iter(turns or [('turn-1', 'inProgress'), ('turn-1', 'inProgress')])
        self.flags, self.kind = flags or [], kind
        self.matches = matches if matches is not None else [dict(id='thread-1', cwd=PROJECT)]
        self.calls = []

    def read(self, method, params=None):
        self.calls.append((method, params))
        if method == 'thread/loaded/list':
            return dict(data=[row['id'] for row in self.matches])
        if method == 'thread/list':
            return dict(data=self.matches)
        if method == 'thread/read':
            return dict(thread=dict(id='thread-1', cwd=PROJECT, status=dict(type=self.kind, activeFlags=self.flags)))
        if method == 'thread/turns/list':
            value = next(self.turns)
            return dict(data=[] if value is None else [dict(id=value[0], status=value[1])])
        raise AssertionError(method)


class PassiveObserverTests(unittest.TestCase):
    def test_mutating_methods_are_rejected_before_transport(self):
        proxy = observer.ReadOnlyProxy.__new__(observer.ReadOnlyProxy)
        proxy._request = lambda *_: self.fail('Mutating transport request was sent')
        for method in ('turn/start', 'thread/resume', 'thread/shellCommand', 'turn/interrupt', 'item/tool/requestUserInput'):
            with self.assertRaises(ValueError):
                proxy.read(method)

    @staticmethod
    def socket_client():
        client = observer.ReadOnlyUnixSocket.__new__(observer.ReadOnlyUnixSocket)
        client.socket, peer = socket.socketpair()
        client.socket.settimeout(1)
        peer.settimeout(1)
        client.closed = threading.Event()
        client.buffer = bytearray()
        client.calls = collections.deque(maxlen=64)
        client.next_id = 0
        return client, peer

    @staticmethod
    def frame(opcode, payload, final=True):
        first = opcode | (0x80 if final else 0)
        size = len(payload)
        length = bytes([size]) if size < 126 else b'\x7e' + struct.pack('!H', size) if size < 65536 else b'\x7f' + struct.pack('!Q', size)
        return bytes([first]) + length + payload

    @staticmethod
    def client_frame(peer):
        def take(count):
            data = bytearray()
            while len(data) < count:
                received = peer.recv(count - len(data))
                if not received:
                    raise AssertionError('Client connection closed early')
                data.extend(received)
            return bytes(data)
        first, second = take(2)
        if not second & 0x80:
            raise AssertionError('Client WebSocket frame was not masked')
        size = second & 127
        if size == 126:
            size = struct.unpack('!H', take(2))[0]
        elif size == 127:
            size = struct.unpack('!Q', take(8))[0]
        mask = take(4)
        payload = take(size)
        return first & 15, bytes(value ^ mask[index % 4] for index, value in enumerate(payload))

    def test_unix_transport_ignores_approval_and_handles_fragmented_reply_with_ping(self):
        client, peer = self.socket_client()
        try:
            approval = json.dumps(dict(id=1, method='item/commandExecution/requestApproval', params=dict(command='private'))).encode()
            reply = json.dumps(dict(id=1, result=dict(data=['thread-1']))).encode()
            peer.sendall(self.frame(1, approval) + self.frame(1, reply[:12], False)
                + self.frame(9, b'alive') + self.frame(0, reply[12:]))
            self.assertEqual(client.read('thread/loaded/list'), dict(data=['thread-1']))
            opcode, sent = self.client_frame(peer)
            self.assertEqual(opcode, 1)
            self.assertEqual(json.loads(sent)['method'], 'thread/loaded/list')
            self.assertEqual(self.client_frame(peer), (10, b'alive'))
            peer.settimeout(0.01)
            with self.assertRaises(socket.timeout):
                peer.recv(1)  # No approval reply was sent.
            with self.assertRaises(ValueError):
                client.read('turn/start')
        finally:
            client.close()
            peer.close()

    def test_unix_transport_bounds_declared_sizes_and_rejects_invalid_frames(self):
        for frame in (b'\x81\x7f' + struct.pack('!Q', observer.MAX_MESSAGE + 1),
                      b'\x81\x80', b'\x09\x00', b'\x80\x00', b'\xc1\x00'):
            client, peer = self.socket_client()
            try:
                peer.sendall(frame)
                with self.assertRaises(observer.ObservationUnavailable):
                    client._text(time.monotonic() + 1)
            finally:
                client.close()
                peer.close()

    def test_unix_transport_supports_extended_lengths_and_detects_eof(self):
        for size in (130, 65536):
            client, peer = self.socket_client()
            try:
                # Buffer a complete server frame to avoid a socketpair capacity wait.
                client.buffer.extend(self.frame(1, b'x' * size))
                self.assertEqual(len(client._text(time.monotonic() + 1)), size)
                peer.close()
                with self.assertRaisesRegex(observer.ObservationUnavailable, 'socket-ended'):
                    client._text(time.monotonic() + 1)
            finally:
                client.close()
                peer.close()

    def test_unix_http_upgrade_and_initialization_use_the_existing_listener(self):
        with tempfile.TemporaryDirectory(prefix='lp-codex-observer-') as directory:
            path = str(pathlib.Path(directory) / 'owned.sock')
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                listener.bind(path)
                listener.listen(1)
                captured, errors = [], []
                def server():
                    try:
                        with listener.accept()[0] as peer:
                            peer.settimeout(2)
                            request = bytearray()
                            while b'\r\n\r\n' not in request:
                                request.extend(peer.recv(4096))
                            key = next(line.split(': ', 1)[1] for line in request.decode().split('\r\n') if line.startswith('Sec-WebSocket-Key:'))
                            accept = base64.b64encode(hashlib.sha1((key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()
                            peer.sendall(('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + accept + '\r\n\r\n').encode())
                            captured.append(json.loads(self.client_frame(peer)[1]))
                            peer.sendall(self.frame(1, json.dumps(dict(id=1, result={})).encode()))
                            captured.append(json.loads(self.client_frame(peer)[1]))
                            captured.append(json.loads(self.client_frame(peer)[1]))
                            peer.sendall(self.frame(1, json.dumps(dict(id=2, result=dict(data=[]))).encode()))
                    except Exception as error:
                        errors.append(error)
                worker = threading.Thread(target=server, daemon=True)
                worker.start()
                client = observer.ReadOnlyUnixSocket(path)
                try:
                    self.assertEqual(client.read('thread/loaded/list'), dict(data=[]))
                finally:
                    client.close()
                    worker.join(3)
                self.assertFalse(worker.is_alive())
                self.assertEqual(errors, [])
                self.assertEqual([value['method'] for value in captured], ['initialize', 'initialized', 'thread/loaded/list'])

    def test_invalid_handshake_is_unavailable_and_trickling_peer_has_total_deadline(self):
        for response in (b'HTTP/1.1 101 Switching Protocols\r\nBroken\r\n\r\n',
                         b'HTTP/1.1 101 Switching Protocols\r\nHeader: \xff\r\n\r\n',
                         b'HTTP/1.1 101 Switching Protocols\r\nSec-WebSocket-Accept: invalid\r\n\r\n', None):
            with tempfile.TemporaryDirectory(prefix='lp-codex-handshake-') as directory:
                path = str(pathlib.Path(directory) / 'owned.sock')
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                    listener.bind(path)
                    listener.listen(1)
                    def server():
                        try:
                            with listener.accept()[0] as peer:
                                peer.settimeout(1)
                                peer.recv(4096)
                                if response is not None:
                                    peer.sendall(response)
                                else:
                                    for _ in range(50):
                                        peer.sendall(b'H')
                                        time.sleep(0.02)
                        except OSError:
                            pass  # Client closes this owned connection on rejection.
                    worker = threading.Thread(target=server, daemon=True)
                    worker.start()
                    started = time.monotonic()
                    with mock.patch.object(observer, 'HANDSHAKE_SECONDS', 0.1):
                        with self.assertRaises(observer.ObservationUnavailable):
                            observer.ReadOnlyUnixSocket(path)
                    elapsed = time.monotonic() - started
                    worker.join(2)
                    self.assertFalse(worker.is_alive())
                    self.assertLess(elapsed, 0.8)

    def test_unsolicited_approvals_are_discarded_without_replies(self):
        proxy = observer.ReadOnlyProxy.__new__(observer.ReadOnlyProxy)
        proxy.closed = threading.Event()
        proxy.messages = queue.Queue(maxsize=64)
        data = [dict(id='approval-1', method='item/commandExecution/requestApproval', params=dict(command='private command')),
                dict(method='item/agentMessage/delta', params=dict(delta='private message')),
                dict(id=1, result=dict(data=[]))]
        proxy.process = types.SimpleNamespace(stdout=io.BytesIO(('\n'.join(json.dumps(value) for value in data) + '\n').encode()))
        proxy._read()
        self.assertEqual(proxy.messages.get_nowait(), dict(id=1, result=dict(data=[])))
        self.assertTrue(proxy.messages.empty())

    def test_unique_loaded_cli_thread_and_metadata_only_history_queries(self):
        proxy = Proxy()
        value = observer.metadata(proxy, PROJECT)
        self.assertEqual(value, dict(threadId='thread-1', runId='turn-1', state='Working', runActive=True, outcome='inProgress'))
        listing = next(params for method, params in proxy.calls if method == 'thread/list')
        self.assertTrue(listing['useStateDbOnly'])
        self.assertEqual(listing['sourceKinds'], ['cli'])
        for method, params in proxy.calls:
            if method == 'thread/turns/list':
                self.assertEqual(params['itemsView'], 'notLoaded')
                self.assertEqual(params['limit'], 1)
            elif method == 'thread/read':
                self.assertFalse(params['includeTurns'])

    def test_ambiguous_threads_and_explicit_unloaded_identity_are_unavailable(self):
        proxy = Proxy(matches=[dict(id='thread-1', cwd=PROJECT), dict(id='thread-2', cwd=PROJECT)])
        with self.assertRaisesRegex(observer.ObservationUnavailable, 'ambiguous'):
            observer.metadata(proxy, PROJECT)
        with self.assertRaisesRegex(observer.ObservationUnavailable, 'main-cli-thread-not-loaded'):
            observer.metadata(Proxy(), PROJECT, 'other-thread')

    def test_same_active_status_cannot_hide_a_changed_turn(self):
        with self.assertRaisesRegex(observer.ObservationUnavailable, 'turn-changed'):
            observer.metadata(Proxy(turns=[('turn-1', 'inProgress'), ('turn-2', 'inProgress')]), PROJECT)

    def test_an_older_loaded_thread_on_another_page_is_not_missed(self):
        proxy = Proxy()
        original = proxy.read
        def read(method, params=None):
            if method == 'thread/loaded/list':
                return dict(data=['thread-1', 'thread-2'])
            if method == 'thread/list':
                if params.get('cursor') == 'older':
                    return dict(data=[dict(id='thread-2', cwd=PROJECT)], nextCursor=None)
                return dict(data=[dict(id='thread-1', cwd=PROJECT)], nextCursor='older')
            return original(method, params)
        proxy.read = read
        with self.assertRaisesRegex(observer.ObservationUnavailable, 'ambiguous'):
            observer.metadata(proxy, PROJECT)

    def test_unexhausted_or_repeated_pagination_cannot_claim_uniqueness(self):
        for repeated in (True, False):
            proxy = Proxy()
            original = proxy.read
            pages = []
            def read(method, params=None):
                if method == 'thread/list':
                    pages.append(params)
                    return dict(data=[dict(id='thread-1', cwd=PROJECT)],
                        nextCursor='same' if repeated else 'page-' + str(len(pages)))
                return original(method, params)
            proxy.read = read
            with self.assertRaises(observer.ObservationUnavailable):
                observer.metadata(proxy, PROJECT)
            self.assertLessEqual(len(pages), 10)

    def test_waiting_categories_do_not_create_fake_question_ids(self):
        for flag in ('waitingOnApproval', 'waitingOnUserInput', 'unknownFlag'):
            with self.assertRaisesRegex(observer.ObservationUnavailable, 'pending-request-identity-unavailable'):
                observer.metadata(Proxy(flags=[flag]), PROJECT)

    def test_idle_and_inprogress_or_notloaded_state_are_unavailable(self):
        for kind in ('idle', 'notLoaded', 'systemError'):
            with self.assertRaises(observer.ObservationUnavailable):
                observer.metadata(Proxy(kind=kind), PROJECT)

    def test_existing_completion_is_baseline_and_only_matching_observed_run_emits_outcome(self):
        values = []
        producer = observer.Producer(uuid.uuid4().hex, lambda prefix, value: values.append((prefix, value)))
        producer.publish(dict(threadId='thread-1', runId='old-turn', state='Idle', runActive=False, outcome='completed'))
        self.assertEqual([prefix for prefix, _ in values], ['LP-STATE '])
        producer.publish(dict(threadId='thread-1', runId='turn-1', state='Working', runActive=True, outcome='inProgress', transcript='private text'))
        producer.publish(dict(threadId='thread-1', runId='turn-1', state='Idle', runActive=False, outcome='completed'))
        producer.publish(dict(threadId='thread-1', runId='turn-1', state='Idle', runActive=False, outcome='completed'))
        events = [value for prefix, value in values if prefix == 'LP-EVENT ']
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]['kind'], 'RunFinished')
        self.assertNotIn('private text', json.dumps(values))
        self.assertEqual([value['sequence'] for _, value in values], list(range(1, len(values) + 1)))

    def test_terminal_failures_and_interruption_are_not_success(self):
        for outcome, expected in [('failed', 'RunFailed'), ('interrupted', 'Interrupted')]:
            values = []
            producer = observer.Producer(uuid.uuid4().hex, lambda prefix, value: values.append((prefix, value)))
            producer.publish(dict(threadId='thread-1', runId='turn-1', state='Working', runActive=True, outcome='inProgress'))
            producer.publish(dict(threadId='thread-1', runId='turn-1', state='Idle', runActive=False, outcome=outcome))
            self.assertEqual(next(value['kind'] for prefix, value in values if prefix == 'LP-EVENT '), expected)

    def test_another_completed_run_does_not_finish_the_previously_observed_run(self):
        values = []
        producer = observer.Producer(uuid.uuid4().hex, lambda prefix, value: values.append((prefix, value)))
        producer.publish(dict(threadId='thread-1', runId='turn-1', state='Working', runActive=True, outcome='inProgress'))
        producer.publish(dict(threadId='thread-1', runId='turn-2', state='Idle', runActive=False, outcome='completed'))
        self.assertFalse(any(prefix == 'LP-EVENT ' for prefix, _ in values))


if __name__ == '__main__':
    unittest.main()
