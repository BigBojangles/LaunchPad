#!/usr/bin/env python3
"""Run owned CLI interface/tool fixtures; never read credentials or real data."""
import argparse
import json
import os
import pathlib
import signal
import subprocess
import threading
import time

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root
assert os.getuid() == os.geteuid() == 1000
parser = argparse.ArgumentParser()
parser.add_argument('mode', choices=['interface', 'probe'])
parser.add_argument('--agent', choices=['grok', 'codex', 'claude'])
args = parser.parse_args()
output = root / 'fixture-output'
output.mkdir(exist_ok=True)
results = []
control_path = root / 'owned_control.json'
control = json.loads(control_path.read_text()) if control_path.exists() else None
if control:
    assert args.mode == 'probe' and args.agent == control['agent']
    assert (root / 'project-identity.txt').read_text() == control['fixtureId']

def release_owned_child():
    ready = output / (args.agent + '-ready.json')
    started = time.monotonic()
    while not ready.exists():
        if time.monotonic() - started > 50:
            return
        time.sleep(0.025)
    witness = json.loads(ready.read_text())
    assert witness['fixtureId'] == control['fixtureId']
    print('AGENT-TOOL-CHILD-READY:' + args.agent, flush=True)
    assert input() == 'RELEASE', 'Owned concurrency release required'
    (output / (args.agent + '-release.json')).write_text(json.dumps(control))

if control:
    threading.Thread(target=release_owned_child, daemon=True).start()
environment = dict(os.environ)
environment.update(CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC='1', DISABLE_TELEMETRY='1',
                   DO_NOT_TRACK='1', TERM='xterm-256color')

def confined(command, name, timeout=50):
    full = ['/usr/bin/aa-exec', '-p', 'launchpad-agent', '--', '/usr/bin/setpriv', '--no-new-privs', '--', *command]
    path = output / (name + '.log')
    with path.open('w') as stream:
        process = subprocess.Popen(full, stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
                                   env=environment, start_new_session=True)
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            # This fixture owns this newly created group. Preserve its output.
            os.killpg(process.pid, signal.SIGTERM)
            try:
                code = process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                code = process.wait(timeout=5)
            return dict(name=name, pid=process.pid, exit=code, timedOut=True)
    return dict(name=name, pid=process.pid, exit=code, timedOut=False)

try:
    if args.mode == 'interface':
        for agent in ['grok', 'codex', 'claude']:
            results.append(confined([agent, '--help'], agent + '-help'))
        results.append(confined(['codex', 'exec', '--help'], 'codex-exec-help'))
    else:
        from owned_model_fixture import exercise
        for agent in ([args.agent] if args.agent else ['grok', 'codex', 'claude']):
            results.append(exercise(agent, confined, environment, output))
except Exception as error:
    results.append(dict(error=type(error).__name__ + ': ' + str(error)))
(output / 'results.json').write_text(json.dumps(dict(mode=args.mode, results=results), indent=2) + '\n')
if any(row.get('exit') != 0 or row.get('timedOut') or (args.mode == 'probe' and not row.get('toolProof')) for row in results):
    print('AGENT-TOOLS-FAILED', flush=True)
print('AGENT-TOOLS-DONE', flush=True)
# Keep the controlled custom session available until the host requests shutdown.
try:
    input()
except EOFError:
    pass
