#!/usr/bin/python3
"""Privileged guest lifecycle: stop builder writers before import/export."""
import os
import pathlib
import pwd
import signal
import sys
import time

if os.getuid() != 0 or os.geteuid() != 0 or pwd.getpwnam('builder').pw_uid != 1000:
    raise SystemExit('Unexpected quiesce identity')
if not hasattr(os, 'pidfd_open') or not hasattr(signal, 'pidfd_send_signal'):
    raise SystemExit('Process-generation handles unavailable')

def writers():
    for proc in pathlib.Path('/proc').iterdir():
        if not proc.name.isdecimal():
            continue
        try:
            status = dict(line.split(':', 1) for line in (proc / 'status').read_text().splitlines() if ':' in line)
            if status.get('Uid', '').split() != ['1000'] * 4:
                continue
            fields = (proc / 'stat').read_text().rsplit(')', 1)[1].split()
            if fields[0] not in ['Z', 'X']:
                yield int(proc.name), int(fields[19])
        except (FileNotFoundError, ProcessLookupError):
            continue

def send(pid, expected_ticks, sig):
    # Hold the process generation before validating its current UID/start time.
    # Numeric PID reuse never redirects a signal to a replacement process.
    try:
        fd = os.pidfd_open(pid)
    except ProcessLookupError:
        return
    try:
        proc = pathlib.Path('/proc') / str(pid)
        status = dict(line.split(':', 1) for line in (proc / 'status').read_text().splitlines() if ':' in line)
        fields = (proc / 'stat').read_text().rsplit(')', 1)[1].split()
        if status.get('Uid', '').split() == ['1000'] * 4 and int(fields[19]) == expected_ticks:
            signal.pidfd_send_signal(fd, sig)
    except (FileNotFoundError, ProcessLookupError):
        pass
    finally:
        os.close(fd)

started = time.monotonic()
signalled = set()
forced = False
empty_samples = 0
while time.monotonic() - started < 3:
    active = list(writers())
    if not active:
        empty_samples += 1
        if empty_samples >= 3:
            print('QUIESCE-FORCED' if forced else 'QUIESCE-CLEAN', flush=True)
            sys.exit(2 if forced else 0)
    else:
        empty_samples = 0
        force_now = time.monotonic() - started >= 2
        for pid, ticks in active:
            if force_now:
                forced = True
                send(pid, ticks, signal.SIGKILL)
            elif (pid, ticks) not in signalled:
                send(pid, ticks, signal.SIGTERM)
                signalled.add((pid, ticks))
    time.sleep(0.025)
print('QUIESCE-INCOMPLETE', flush=True)
sys.exit(3)
