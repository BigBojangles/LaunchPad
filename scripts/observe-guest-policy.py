#!/usr/bin/env python3
"""Observe only actual uid-1000 process identities/labels; no argv/environment."""
import json
import pathlib
import signal
import time
import sys
import subprocess
import os

root = pathlib.Path('/var/tmp/launchpad-policy-probe')
root.mkdir(mode=0o700, exist_ok=True)
stop = False
records = {}

def finish(*_):
    global stop
    stop = True

signal.signal(signal.SIGTERM, finish)
signal.signal(signal.SIGINT, finish)
started = time.monotonic()
duration = int(sys.argv[1]) if len(sys.argv) >= 2 else 45
assert 1 <= duration <= 360
network_recorded = False
terminal_witness = len(sys.argv) == 3 and sys.argv[2] == 'terminal'
terminal_recorded = False
terminal_ended = False
terminal_counter = 0
terminal_last = 0
terminal_rows = []
terminal_agent = ''

def terminal_marker(message):
    serial = os.open('/dev/ttyS0', os.O_WRONLY | os.O_NOCTTY)
    try:
        os.write(serial, (message + '\n').encode())
    finally:
        os.close(serial)

def capture_terminal_witness():
    global terminal_rows, terminal_agent
    expected = json.loads(pathlib.Path('/opt/launchpad-policy-probe/owned_terminal.json').read_text())
    project = pathlib.Path('/home/builder/in/project')
    ready = json.loads((project / 'fixture-output/agent-terminal-ready.json').read_text())
    assert all(ready[key] == expected[key] for key in ['agent', 'fixtureId', 'nonce'])
    independently_observed = []
    for supplied in ready['ancestry']:
        proc = pathlib.Path('/proc') / str(supplied['pid'])
        status = dict(line.split(':', 1) for line in (proc / 'status').read_text().splitlines() if ':' in line)
        stat = (proc / 'stat').read_text()
        fields = stat[stat.rfind(')') + 2:].split()
        actual = dict(pid=int(proc.name), startTicks=int(fields[19]), parentPid=int(status['PPid']),
                      executable=(proc / 'exe').readlink().as_posix(), processGroup=int(fields[2]),
                      label=(proc / 'attr/current').read_text().strip(), uid=[int(value) for value in status['Uid'].split()],
                      capabilities=status['CapEff'].strip(), noNewPrivileges=status['NoNewPrivs'].strip())
        assert actual == supplied
        assert actual['label'] == 'launchpad-agent (enforce)' and actual['uid'] == [1000] * 4
        assert actual['capabilities'] == '0000000000000000' and actual['noNewPrivileges'] == '1'
        independently_observed.append(actual)
    assert len(independently_observed) >= 2
    for child, parent in zip(independently_observed, independently_observed[1:]):
        assert child['parentPid'] == parent['pid']
    native = independently_observed[-1]
    launch = json.loads((project / 'fixture-output/terminal-launch.json').read_text())
    assert native['executable'].endswith('/' + expected['agent'])
    assert native['pid'] == launch['nativePid'] == launch['foregroundGroup'] == native['processGroup']
    (root / 'terminal-witness.json').write_text(json.dumps(dict(expected=expected,
        observed=independently_observed, launch=launch, rootUid=os.getuid(), capturedMonotonic=time.monotonic()), indent=2))
    terminal_rows = independently_observed
    terminal_agent = expected['agent']
    # A fixture-only root observation, separate from product readiness markers.
    terminal_marker('OWNED-TERMINAL-CHILD-READY:' + terminal_agent)

def terminal_child_alive():
    for expected in terminal_rows:
        proc = pathlib.Path('/proc') / str(expected['pid'])
        fields = (proc / 'stat').read_text().rsplit(')', 1)[1].split()
        if fields[0] in ['Z', 'X'] or int(fields[19]) != expected['startTicks']:
            return False
    return True

while not stop and time.monotonic() - started < duration:
    if terminal_witness and not terminal_recorded:
        try:
            capture_terminal_witness()
            terminal_recorded = True
        except (OSError, KeyError, ValueError):
            pass
    if terminal_recorded and not terminal_ended and time.monotonic() - terminal_last >= 0.1:
        terminal_last = time.monotonic()
        try:
            alive = terminal_child_alive()
        except (OSError, ValueError):
            alive = False
        if alive:
            terminal_counter += 1
            terminal_marker('OWNED-TERMINAL-CHILD-ALIVE:' + terminal_agent + ':' + str(terminal_counter))
        else:
            terminal_ended = True
            terminal_marker('OWNED-TERMINAL-CHILD-ENDED:' + terminal_agent)
    for directory in pathlib.Path('/proc').iterdir():
        if not directory.name.isdecimal():
            continue
        try:
            status = dict(line.split(':', 1) for line in (directory / 'status').read_text().splitlines() if ':' in line)
            if status.get('Uid', '').split() != ['1000'] * 4:
                continue
            if not network_recorded:
                (root / 'network-rules.txt').write_text(subprocess.check_output(['/usr/sbin/iptables-save'], text=True))
                network_recorded = True
            stat = (directory / 'stat').read_text()
            ticks = stat[stat.rfind(')') + 2:].split()[19]
            label = (directory / 'attr/current').read_text().strip()
            executable = (directory / 'exe').readlink().as_posix()
            key = (directory.name, ticks, executable, label)
            if key in records:
                continue
            records[key] = dict(pid=int(directory.name), startTicks=int(ticks), executable=executable, label=label,
                                parentPid=int(status['PPid']), uid=[int(v) for v in status['Uid'].split()],
                                gid=[int(v) for v in status['Gid'].split()], capabilities=status.get('CapEff', '').strip(),
                                noNewPrivileges=status.get('NoNewPrivs', '').strip())
            temporary = root / 'processes.tmp'
            temporary.write_text(json.dumps(list(records.values()), indent=2) + '\n')
            temporary.replace(root / 'processes.json')
        except (FileNotFoundError, PermissionError, ProcessLookupError, KeyError, ValueError):
            continue
    time.sleep(0.005)
(root / 'observer-finished.txt').write_text('completed\n')
