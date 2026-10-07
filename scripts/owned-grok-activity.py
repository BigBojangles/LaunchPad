#!/usr/bin/env python3
"""Pinned interactive Grok + passive hooks and an owned loopback model."""
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import termios
import time
import tomllib
import uuid

from owned_model_fixture import Fixture, configure

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root and os.getuid() == os.geteuid() == 1000
assert os.isatty(0) and os.tcgetpgrp(0) == os.getpgrp()
assert termios.tcgetattr(0)[3] & termios.ISIG
output = root / 'fixture-output'
output.mkdir(exist_ok=True)
(output / 'grok-hooks').mkdir(exist_ok=True)
session = str(uuid.uuid4())
nonce = uuid.uuid4().hex
environment = dict(os.environ)
environment.update(TERM='xterm-256color', DISABLE_TELEMETRY='1', DO_NOT_TRACK='1',
                   LP_OWNED_GROK_NONCE=nonce)
background = len(sys.argv) == 2 and sys.argv[1] == '--background'
if background:
    (root / 'owned-grok-background.json').write_text('{}')
server = Fixture('grok', output, grok_background=background)
configure('grok', environment, output, server.server_address[1])
config = pathlib.Path('/home/builder/.grok/config.toml')
hooks_dir = config.parent / 'hooks'
hooks_dir.mkdir(exist_ok=True)
hook = hooks_dir / 'launchpad-owned-activity.json'
sentinel = hooks_dir / 'launchpad-owned-existing.json'
assert not hook.exists() and not sentinel.exists()
assert not list(hooks_dir.glob('*.json')), 'Baseline must contain no personal hooks'
baseline = config.read_bytes()
baseline_process = subprocess.Popen(['/usr/bin/aa-exec', '-p', 'launchpad-agent', '--',
    '/usr/bin/setpriv', '--no-new-privs', '--', 'grok', '--no-alt-screen', '--model', 'launchpad-fixture'], env=environment)
try:
    baseline_deadline = time.monotonic() + 10
    while time.monotonic() < baseline_deadline:
        if baseline_process.poll() is not None:
            raise AssertionError('Ordinary no-hook TUI baseline exited before startup marker')
        if tomllib.loads(config.read_text()).get('marketplace', {}).get('default_skills_installs_purged') is True:
            break
        time.sleep(0.1)
    after_baseline = config.read_bytes()
    expected_baseline = tomllib.loads(baseline.decode())
    expected_baseline['marketplace'] = dict(default_skills_installs_purged=True)
    assert tomllib.loads(after_baseline.decode()) == expected_baseline, 'Unexpected no-hook startup configuration change'
finally:
    if baseline_process.poll() is None:
        baseline_process.terminate()
        try:
            baseline_process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            baseline_process.kill()
            baseline_process.wait(timeout=3)
(output / 'grok-no-hook-config-before.toml').write_bytes(baseline)
(output / 'grok-no-hook-config-after.toml').write_bytes(after_baseline)
(output / 'grok-no-hook-baseline.json').write_text(json.dumps(dict(pid=baseline_process.pid,
    exited=baseline_process.poll() is not None, beforeSha256=hashlib.sha256(baseline).hexdigest(),
    afterSha256=hashlib.sha256(after_baseline).hexdigest(), marker='marketplace.default_skills_installs_purged',
    limitations='Ordinary owned no-hook TUI startup only; no prompt or agent run. Only this child was ended.')))
events = ['SessionStart', 'SessionEnd', 'UserPromptSubmit', 'PostToolUse',
          'PostToolUseFailure', 'Stop', 'StopFailure', 'Notification',
          'SubagentStart', 'SubagentStop']
hook.write_text(json.dumps(dict(hooks={event: [dict(hooks=[dict(type='command',
    command='python3 /home/builder/in/project/owned-grok-hook.py ' + nonce, timeout=2)])] for event in events})))
# Separate existing passive hook proves our additional file does not replace it.
sentinel.write_text(json.dumps(dict(hooks={'Stop': [dict(hooks=[dict(type='command',
    command='printf sentinel >> /home/builder/in/project/fixture-output/grok-existing-hook.txt', timeout=2)])]})))
before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in [config, hook, sentinel]}
with (output / 'grok-hook-inspect.json').open('w') as log:
    subprocess.run(['grok', 'inspect', '--json'], stdout=log, stderr=subprocess.STDOUT,
                   env=environment, timeout=10, check=True)
assert json.loads((output / 'grok-hook-inspect.json').read_text())['grokVersion'] == '1.0.46'
main_pid = os.getpid()
model_pid = os.fork()
if model_pid == 0:
    os.setsid()
    with open('/dev/null', 'rb') as source, (output / 'grok-model-server.log').open('w') as sink:
        os.dup2(source.fileno(), 0)
        os.dup2(sink.fileno(), 1)
        os.dup2(sink.fileno(), 2)
        server.serve_forever()
    os._exit(0)
server.server_close()
observer_pid = os.fork()
if observer_pid == 0:
    marker = os.dup(1)
    os.setsid()
    with open('/dev/null', 'rb') as source, (output / 'grok-hook-observer.log').open('w') as sink:
        os.dup2(source.fileno(), 0)
        os.dup2(sink.fileno(), 1)
        os.dup2(sink.fileno(), 2)
        failure = None
        rows = []
        sent_next = False
        completed = False
        deadline = time.monotonic() + 65
        try:
            while time.monotonic() < deadline:
                wire_path = output / 'grok-wire.json'
                if background and wire_path.exists():
                    try:
                        wire_so_far = json.loads(wire_path.read_text())
                    except ValueError:
                        wire_so_far = []
                    errors = [row['error'] for row in wire_so_far if 'error' in row]
                    assert not errors, 'Owned background model fixture failed: ' + '; '.join(errors)
                rows = []
                for path in (output / 'grok-hooks').glob('*.json'):
                    try:
                        rows.append(json.loads(path.read_text()))
                    except (OSError, ValueError):
                        continue
                rows.sort(key=lambda row: row['capturedNs'])
                owned = [row for row in rows if row['nonce'] == nonce and row['identity'].get('sessionId') == session]
                stops = [row for row in owned if row['hookEventName'] == 'Stop']
                work_finished = output / 'grok-activity-work-finished.json'
                wake_stops = [row for row in stops if row['identity'].get('promptId', '').startswith('task-completed-')]
                if stops and not sent_next and (not background or work_finished.exists() and wake_stops):
                    sent_next = True
                    os.write(marker, b'GROK-ACTIVITY-NEXT\n')
                existing = output / 'grok-existing-hook.txt'
                submits = [row for row in owned if row['hookEventName'] == 'UserPromptSubmit'
                           and not row['identity'].get('promptId', '').startswith('task-completed-')]
                final_prompt = submits[-1]['identity'].get('promptId') if len(submits) == 2 else None
                final_stops = [row for row in stops if row['identity'].get('promptId') == final_prompt]
                if len(submits) == 2 and final_stops and existing.exists() and len(existing.read_text()) >= len('sentinelsentinel'):
                    completed = True
                    break
                time.sleep(0.1)
            assert completed, 'Two actual main-run Stop callbacks were not captured'
            submits = [row for row in owned if row['hookEventName'] == 'UserPromptSubmit'
                       and not row['identity'].get('promptId', '').startswith('task-completed-')]
            assert len(submits) == 2, 'Actual repeated submit identity is incomplete'
            assert {row['identity'].get('promptId') for row in submits}.issubset({row['identity'].get('promptId') for row in stops}), 'Submit/Stop prompt identities disagree'
            assert len({row['identity'].get('promptId') for row in submits}) == 2, 'Repeated runs reused a prompt ID'
            assert all(row['cwdMatchesOwnedProject'] for row in owned), 'Hook project changed'
            assert all(row['policy'] == 'launchpad-agent (enforce)' for row in owned), 'Hook escaped agent policy'
            assert (output / 'grok-existing-hook.txt').read_text().count('sentinel') >= 2, 'Existing hook did not run twice'
            if background:
                first_stop = next(row for row in stops if row['identity'].get('promptId') == submits[0]['identity']['promptId'])
                assert first_stop['stopMetadata'].get('backgroundTasksCount', 0) > 0, 'No actual Stop with background tasks'
                finished = json.loads(work_finished.read_text())
                assert first_stop['capturedNs'] < finished['finishedNs'], 'Background child already ended at first Stop'
                task_ids = [entry['id'] for entry in first_stop['adapterInput']['backgroundTasks']]
                assert len(task_ids) == 1
                assert len(wake_stops) == 1 and wake_stops[0]['identity']['promptId'] == 'task-completed-' + task_ids[0], 'Generated wakeup did not match the observed background task ID'
                assert wake_stops[0]['stopMetadata']['backgroundTasksCount'] == 0
            else:
                assert len(stops) == 2
            work = json.loads((output / 'grok-activity-work.json').read_text())
            assert work['policy'] == 'launchpad-agent (enforce)' and work['uid'] == 1000
            wire = json.loads((output / 'grok-wire.json').read_text())
            if not background:
                assert any(row.get('hasToolResult') for row in wire), 'Actual tool result was not consumed'
            assert before == {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in [config, hook, sentinel]}, 'Configuration changed'
        except Exception as error:
            failure = type(error).__name__ + ': ' + str(error)
        (output / 'grok-hook-proof.json').write_text(json.dumps(dict(interactive=True, mainPid=main_pid,
            sessionId=session, nonce=nonce, background=background, completed=completed, sentNext=sent_next, callbackCount=len(rows),
            configurationHashes=before, failure=failure,
            limitations='Exact interactive root callbacks only. No production template activation, native adapter, subagent/attention/failed-run/sign-in or physical UI proof. Fixture-only always-approve and isolated loopback model.'), indent=2))
        (output / 'results.json').write_text(json.dumps(dict(mode='grok-activity', results=[dict(
            name='grok-hooks', exit=0 if failure is None else 1, timedOut=False)])))
        os.write(marker, (('AGENT-TOOLS-FAILED\n' if failure else '') + 'AGENT-TOOLS-DONE\n').encode())
        os.close(marker)
    os._exit(0)
(output / 'grok-interactive-launch.json').write_text(json.dumps(dict(pid=main_pid,
    foregroundGroup=os.tcgetpgrp(0), modelPid=model_pid, observerPid=observer_pid)))
os.execvpe('/usr/bin/aa-exec', ['/usr/bin/aa-exec', '-p', 'launchpad-agent', '--',
    '/usr/bin/setpriv', '--no-new-privs', '--', 'grok', '--no-alt-screen',
    '--session-id', session, '--model', 'launchpad-fixture', '--always-approve',
    '--disable-web-search', 'Run exactly python3 owned-agent-boundary.py grok through your shell tool, then finish. This is an owned activity fixture.'], environment)
