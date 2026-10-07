#!/usr/bin/env python3
"""One harmless held command in the disposable Codex activity project."""
import json
import os
import pathlib
import time

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root and os.getuid() == os.geteuid() == 1000
assert (pathlib.Path('/proc/self/attr/current').read_text().strip() == 'launchpad-agent (enforce)')
status = pathlib.Path('/proc/self/status').read_text()
assert 'NoNewPrivs:\t1' in status and 'CapEff:\t0000000000000000' in status
output = root / 'fixture-output'
(output / 'codex-activity-work.json').write_text(json.dumps(dict(pid=os.getpid(), held=True)))
deadline = time.monotonic() + 60
while not (output / 'codex-activity-release').exists():
    assert time.monotonic() < deadline, 'Owned activity command was not released'
    time.sleep(0.025)
(output / 'codex-activity-write.txt').write_text('Owned interactive Codex command completed.\n')
print('OWNED-AGENT-BOUNDARY-DONE:codex', flush=True)
