#!/usr/bin/env python3
"""Actual CLI shell tool: flush an owned edit, remain active for terminal death."""
import json
import os
import pathlib
import signal
import sys
import time

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root
control = json.loads((root / 'owned_terminal.json').read_text())
agent = sys.argv[1]
assert agent == control['agent'] and agent in ['grok', 'codex', 'claude']
assert (root / 'nonce.txt').read_text() == control['nonce']

def identity(pid):
    proc = pathlib.Path('/proc') / str(pid)
    status = dict(line.split(':', 1) for line in (proc / 'status').read_text().splitlines() if ':' in line)
    stat = (proc / 'stat').read_text()
    fields = stat[stat.rfind(')') + 2:].split()
    return dict(pid=pid, startTicks=int(fields[19]), parentPid=int(status['PPid']),
                executable=(proc / 'exe').readlink().as_posix(), processGroup=int(fields[2]),
                label=(proc / 'attr/current').read_text().strip(), uid=[int(value) for value in status['Uid'].split()],
                capabilities=status['CapEff'].strip(), noNewPrivileges=status['NoNewPrivs'].strip())

ancestry = []
pid = os.getpid()
for _ in range(12):
    row = identity(pid)
    ancestry.append(row)
    assert row['uid'] == [1000] * 4 and row['label'] == 'launchpad-agent (enforce)'
    assert row['capabilities'] == '0000000000000000' and row['noNewPrivileges'] == '1'
    if row['executable'].endswith('/' + agent):
        break
    pid = row['parentPid']
else:
    raise AssertionError('Actual native agent ancestor absent')
content = agent + ':' + control['fixtureId'] + ':partial\n'
for name, text in [('guest-only.txt', 'guest-' + control['nonce']), ('agent-progress.txt', content)]:
    with (root / name).open('w') as stream:
        stream.write(text)
        stream.flush()
        os.fsync(stream.fileno())
output = root / 'fixture-output'
if control.get('fault') == 'ignore-term':
    detached = os.fork()
    if detached == 0:
        os.setsid()
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        with open('/dev/null', 'r+b') as null:
            for descriptor in (0, 1, 2):
                os.dup2(null.fileno(), descriptor)
            time.sleep(120)
        os._exit(0)
ready = dict(agent=agent, fixtureId=control['fixtureId'], nonce=control['nonce'], ancestry=ancestry,
             milestone='Owned project edits flushed; actual native tool child remains active.')
temporary = output / 'agent-terminal-ready.tmp'
temporary.write_text(json.dumps(ready))
temporary.replace(output / 'agent-terminal-ready.json')

def interrupted(signum, _):
    # A well-behaved writer may flush its final edit on termination. A return
    # taken before this shutdown write must fail the stronger preservation test.
    with (root / 'agent-progress.txt').open('w') as stream:
        stream.write(agent + ':' + control['fixtureId'] + ':stopped\n')
        stream.flush()
        os.fsync(stream.fileno())
    (output / 'tool-interrupted.json').write_text(json.dumps(dict(signal=signum, fixtureId=control['fixtureId'],
        observedMonotonic=time.monotonic())))
    sys.exit(128 + signum)

signal.signal(signal.SIGINT, interrupted)
signal.signal(signal.SIGTERM, interrupted)
signal.signal(signal.SIGHUP, interrupted)
started = time.monotonic()
while time.monotonic() - started < 120:
    time.sleep(0.1)
raise AssertionError('Host did not interrupt the owned terminal in time')
