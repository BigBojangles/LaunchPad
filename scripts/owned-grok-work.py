#!/usr/bin/env python3
"""A short owned actual tool call, without security probes or project writes."""
import json
import os
import pathlib
import time

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root and os.getuid() == os.geteuid() == 1000
(root / 'fixture-output' / 'grok-activity-work.json').write_text(json.dumps(dict(
    pid=os.getpid(), parentPid=os.getppid(), uid=os.getuid(),
    policy=pathlib.Path('/proc/self/attr/current').read_text().strip(),
    statusFields=[line for line in pathlib.Path('/proc/self/status').read_text().splitlines()
                  if line.startswith(('Uid:', 'NoNewPrivs:', 'CapEff:'))])))
background = (root / 'owned-grok-background.json').exists()
time.sleep(12 if background else 3)
(root / 'fixture-output' / 'grok-activity-work-finished.json').write_text(json.dumps(dict(pid=os.getpid(), finishedNs=time.time_ns())))
print('OWNED-AGENT-BOUNDARY-DONE:grok', flush=True)
