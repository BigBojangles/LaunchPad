#!/usr/bin/env python3
"""One native Codex notify plumbing check; no production installation or model service."""
import datetime
import hashlib
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import threading
import time
import uuid

sys.dont_write_bytecode = True

REPO = pathlib.Path(__file__).resolve().parent.parent
PHASE = REPO / 'tests/LaunchPad.Tests/TestResults/migration/codex-notify-20261007'
PIN = '9e7c59c05cc1ce5677b1f94e835b2ac038ca3be14504e78d558eacdb0ea3f55d'


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def owned_root(value):
    root = pathlib.Path(value).resolve()
    if root.parent != PHASE.resolve() or len(root.name) != 32:
        raise ValueError('Expected a separate phase-owned fixture')
    uuid.UUID(hex=root.name)
    return root


def capture(root, payload):
    """Discard prompts/messages immediately; this is not a trusted product ingress."""
    if len(payload.encode('utf-8')) > 65536:
        return 1
    value = json.loads(payload)
    if not isinstance(value, dict):
        return 1
    fields = {key: value.get(key) for key in ('type', 'thread-id', 'turn-id')}
    if any(not isinstance(item, str) or not item or len(item) > 128
           or any(ord(char) < 32 for char in item) for item in fields.values()):
        return 1
    cwd = value.get('cwd')
    fields.update(cwdMatchesOwnedProject=isinstance(cwd, str)
                  and pathlib.Path(cwd).resolve() == (root / 'project').resolve(),
                  expectedFixtureMessage=value.get('last-assistant-message') == 'Owned fixture complete.',
                  capturedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    with (root / ('callback-' + uuid.uuid4().hex + '-private.json')).open('x', encoding='utf-8') as stream:
        json.dump(fields, stream, indent=2)
    return 0


def run(program, python_launcher):
    if os.name != 'nt' or digest(program) != PIN:
        raise ValueError('Exact pinned native Windows executable required')
    PHASE.mkdir(parents=True, exist_ok=True)
    root = owned_root(PHASE / uuid.uuid4().hex)
    root.mkdir()
    home, project = root / 'home', root / 'project'
    home.mkdir()
    project.mkdir()
    environment = dict(os.environ)
    for key in ('OPENAI_API_KEY', 'CODEX_API_KEY', 'ANTHROPIC_API_KEY', 'GROK_API_KEY', 'CHATGPT_TOKEN'):
        environment.pop(key, None)
    environment.update(CODEX_HOME=str(home), DO_NOT_TRACK='1', DISABLE_TELEMETRY='1')
    module_spec = importlib.util.spec_from_file_location('owned_model_fixture', REPO / 'scripts/owned-model-fixture.py')
    module = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(module)

    class CompletionHandler(module.Handler):
        def do_POST(self):
            if self.path != '/v1/responses':
                self.server.record(rejectedPath=True)
                self.send_error(404)
                return
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length < 4 * 1024 * 1024:
                self.send_error(400)
                return
            body = json.loads(self.rfile.read(length))
            self.server.calls += 1
            self.server.record(path=self.path, call=self.server.calls)
            # Do not save body, tool descriptions, user context or provider headers.
            self.response_stream(body, None)

    server = module.Fixture('codex', root)
    server.RequestHandlerClass = CompletionHandler
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = 'http://127.0.0.1:' + str(server.server_address[1])
    environment.update(LP_FIXTURE_KEY='launchpad-owned-fixture-not-a-secret',
                       HTTP_PROXY=base, HTTPS_PROXY=base, NO_PROXY='127.0.0.1,localhost',
                       http_proxy=base, https_proxy=base, no_proxy='127.0.0.1,localhost')
    notify = [str(python_launcher), '-3', str(pathlib.Path(__file__).resolve()), '--capture', str(root)]
    configuration = ('model = "gpt-5-codex"\nmodel_provider = "owned"\n'
                     'project_doc_max_bytes = 0\n'
                     'notify = ' + json.dumps(notify) + '\n'
                     '[analytics]\nenabled = false\n'
                     '[otel]\nmetrics_exporter = "none"\ntrace_exporter = "none"\n'
                     '[model_providers.owned]\nname = "Owned loopback fixture"\n'
                     'base_url = "' + base + '/v1"\nenv_key = "LP_FIXTURE_KEY"\n'
                     'wire_api = "responses"\nrequires_openai_auth = false\n'
                     'supports_websockets = false\nrequest_max_retries = 0\nstream_max_retries = 0\n')
    config_path = home / 'config.toml'
    config_path.write_text(configuration, encoding='utf-8')
    config_before = digest(config_path)
    command = [str(program), '--no-daemon', 'exec', '--skip-git-repo-check', '--sandbox', 'read-only',
               '--ephemeral', '--json', 'Return a short completion. Do not use any tools.']
    receipt = dict(phase='D70', root=str(root), program=str(program), programSha256=PIN,
                   command=command, scope='Single headless native notify plumbing only; no TUI/root-child/attention/delivery proof')
    (root / 'launch-private.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    process = None
    failure = None
    output, errors = '', ''
    try:
        process = subprocess.Popen(command, cwd=project, env=environment, text=True, encoding='utf-8',
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        receipt['pid'] = process.pid
        (root / 'launch-private.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
        try:
            output, errors = process.communicate(timeout=45)
        except subprocess.TimeoutExpired:
            # This headless invocation has no tool requests or daemon; stop only our process.
            process.kill()
            output, errors = process.communicate(timeout=5)
            raise RuntimeError('Owned single-turn deadline expired')
        if process.returncode != 0:
            raise RuntimeError('Native CLI exited nonzero')
        deadline = time.monotonic() + 5
        while not list(root.glob('callback-*-private.json')) and time.monotonic() < deadline:
            time.sleep(.05)
        rows = [json.loads(path.read_text(encoding='utf-8')) for path in root.glob('callback-*-private.json')]
        events = [json.loads(line) for line in output.splitlines() if line.startswith('{')]
        started = [row['thread_id'] for row in events if row.get('type') == 'thread.started']
        if server.calls != 1 or len(rows) != 1 or len(started) != 1:
            raise RuntimeError('Expected one model call, one root thread and one callback')
        callback = rows[0]
        if callback['type'] != 'agent-turn-complete' or callback['thread-id'] != started[0] \
                or not callback['cwdMatchesOwnedProject'] or not callback['expectedFixtureMessage']:
            raise RuntimeError('Completion metadata does not match the owned turn')
    except Exception as error:
        failure = type(error).__name__ + ': ' + str(error)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        server.shutdown()
        server.server_close()
        # CLI JSON includes assistant text. Keep only fixed event categories and
        # identifiers, with raw output confined to assertion memory.
        event_types = []
        thread_ids = []
        malformed_lines = 0
        for line in output.splitlines():
            if not line.startswith('{'):
                continue
            try:
                row = json.loads(line)
                category = row.get('type')
                if category in ('thread.started', 'turn.started', 'turn.completed', 'turn.failed',
                                'item.started', 'item.updated', 'item.completed', 'error'):
                    event_types.append(category)
                thread = row.get('thread_id')
                if isinstance(thread, str) and 0 < len(thread) <= 128 and not any(ord(c) < 32 for c in thread):
                    thread_ids.append(thread)
            except (ValueError, AttributeError):
                malformed_lines += 1
        diagnostics = dict(eventTypes=event_types[:64], threadIds=thread_ids[:16],
                           malformedJsonLines=malformed_lines, stdoutCharacters=len(output),
                           stderrCharacters=len(errors), stderrSha256=hashlib.sha256(errors.encode()).hexdigest(),
                           unsupportedArgument='unexpected argument' in errors.lower(),
                           configError='error loading config' in errors.lower(),
                           authError='authentication' in errors.lower() or 'not logged in' in errors.lower())
        (root / 'cli-metadata-private.json').write_text(json.dumps(diagnostics, indent=2), encoding='utf-8')
        result = dict(receipt, exitCode=None if process is None else process.returncode,
                      passed=failure is None, failure=failure, controlledModelCalls=server.calls,
                      callbackCount=len(list(root.glob('callback-*-private.json'))),
                      configSha256Before=config_before, configSha256After=digest(config_path),
                      programSha256After=digest(program), ownedAuthCreated=(home / 'auth.json').exists(),
                      scriptSha256=digest(pathlib.Path(__file__).resolve()),
                      sharedWireFixtureSha256=digest(REPO / 'scripts/owned-model-fixture.py'))
        if result['configSha256After'] != config_before or result['programSha256After'] != PIN or result['ownedAuthCreated']:
            result.update(passed=False, failure='Configuration/binary/auth preservation assertion failed')
        (root / 'result-private.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
        print(json.dumps(dict(root=str(root), passed=result['passed'], exitCode=result['exitCode'],
                              failure=result['failure'], modelCalls=server.calls, callbacks=result['callbackCount'])))
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == '--capture':
        sys.exit(capture(owned_root(sys.argv[2]), sys.argv[3]))
    if len(sys.argv) == 3:
        sys.exit(run(pathlib.Path(sys.argv[1]).resolve(), pathlib.Path(sys.argv[2]).resolve()))
    raise SystemExit('Expected pinned executable and Python launcher, or owned capture arguments')
