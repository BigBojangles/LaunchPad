#!/usr/bin/env python3
"""Exec an actual native agent in the owned foreground terminal, no controller."""
import json
import os
import pathlib
import termios

from owned_model_fixture import Fixture, configure

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root and os.getuid() == os.geteuid() == 1000
control = json.loads((root / 'owned_terminal.json').read_text())
agent = control['agent']
assert agent in ['grok', 'codex', 'claude']
assert (root / 'nonce.txt').read_text() == control['nonce']
assert os.tcgetpgrp(0) == os.getpgrp()
assert termios.tcgetattr(0)[3] & termios.ISIG
output = root / 'fixture-output'
output.mkdir(exist_ok=True)
environment = dict(os.environ)
environment.update(CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC='1', DISABLE_TELEMETRY='1',
                   DO_NOT_TRACK='1', TERM='xterm-256color')
server = Fixture(agent, output)
command = configure(agent, environment, output, server.server_address[1], 'owned-agent-interruption.py')
# Only this owned model fixture is a detached process; the actual agent below
# replaces this foreground process and receives the real terminal lifecycle.
fixture_pid = os.fork()
if fixture_pid == 0:
    os.setsid()
    with open('/dev/null', 'rb') as source, (output / 'model-server.log').open('w') as sink:
        os.dup2(source.fileno(), 0)
        os.dup2(sink.fileno(), 1)
        os.dup2(sink.fileno(), 2)
        try:
            server.serve_forever()
        finally:
            server.server_close()
    os._exit(0)
server.server_close()
(output / 'terminal-launch.json').write_text(json.dumps(dict(agent=agent, fixtureId=control['fixtureId'],
    nativePid=os.getpid(), foregroundGroup=os.tcgetpgrp(0), modelFixturePid=fixture_pid,
    modelFixtureScope='Unprivileged owned loopback server only; not a shipping component.')))
os.execvpe('/usr/bin/aa-exec', ['/usr/bin/aa-exec', '-p', 'launchpad-agent', '--',
    '/usr/bin/setpriv', '--no-new-privs', '--', *command], environment)
