#!/bin/sh
# Bounded process/terminal observation as the ordinary guest agent user.
exec 3<&0
python3 - <<'PY'
import os, pathlib, subprocess, sys, time
os.dup2(3, 0)
os.close(3)
print('CLAUDE-DIAGNOSTIC-BEGIN', flush=True)
print('IDENTITY', os.getuid(), os.getgid(), 'TTY', os.isatty(0), os.isatty(1), flush=True)
print('ENV', {key: os.environ.get(key) for key in ('HOME', 'PATH', 'TERM', 'LANG', 'USER')}, flush=True)
for name, prefixes in (('cpuinfo', ('model name', 'flags')), ('meminfo', ('MemTotal:', 'MemAvailable:'))):
    lines = pathlib.Path('/proc', name).read_text().splitlines()
    print(name.upper(), '\n'.join(dict.fromkeys(line for line in lines if line.startswith(prefixes))), flush=True)
try:
    result = subprocess.run(['/usr/local/bin/claude', '--help'], stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=12, text=True)
    print('HELP-EXIT', result.returncode, 'HELP-STDOUT', result.stdout[:2500], 'HELP-STDERR', result.stderr[:1000], flush=True)
except subprocess.TimeoutExpired:
    print('HELP-TIMEOUT', flush=True)
debug = pathlib.Path('.lp-claude-startup-private.log').resolve()
child = subprocess.Popen(['/usr/local/bin/claude', '--debug-file', str(debug)],
                         stdin=sys.stdin, stdout=sys.stdout, stderr=sys.stderr)
try:
    for delay in (2, 8, 15):
        time.sleep(delay)
        print('\nCHILD-SNAPSHOT', child.pid, 'EXIT', child.poll(), flush=True)
        if child.poll() is not None:
            break
        directory = pathlib.Path('/proc') / str(child.pid)
        for name in ('wchan', 'syscall', 'stat', 'status'):
            try:
                text = (directory / name).read_text()
                if name == 'status':
                    text = '\n'.join(line for line in text.splitlines() if line.startswith(('Name:', 'State:', 'Uid:', 'Gid:', 'Threads:', 'VmRSS:', 'VmSize:', 'NoNewPrivs:', 'Seccomp:')))
                print(name.upper(), text.strip(), flush=True)
            except OSError as error:
                print(name.upper(), 'UNAVAILABLE', error.errno, flush=True)
        for fd in (0, 1, 2):
            try: print('FD', fd, os.readlink(directory / 'fd' / str(fd)), flush=True)
            except OSError as error: print('FD', fd, 'UNAVAILABLE', error.errno, flush=True)
        for task in list((directory / 'task').iterdir())[:8]:
            try: print('THREAD', task.name, (task / 'wchan').read_text().strip(), flush=True)
            except OSError: pass
        print('DEBUG-EXISTS', debug.exists(), flush=True)
finally:
    if child.poll() is None:
        child.terminate()
        try: child.wait(timeout=3)
        except subprocess.TimeoutExpired: child.kill(); child.wait(timeout=3)
print('CHILD-FINAL-EXIT', child.returncode, flush=True)
print('CLAUDE-DIAGNOSTIC-END', flush=True)
PY
