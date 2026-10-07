#!/usr/bin/env python3
"""Actual interactive CLI plus a separately connected read-only daemon observer."""
import importlib.util
import json
import os
import pathlib
import re
import stat
import subprocess
import termios
import time
import uuid

from owned_model_fixture import Fixture, configure

root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root and os.getuid() == os.geteuid() == 1000
output = root / 'fixture-output'
output.mkdir(exist_ok=True)
(root / 'owned_activity.json').write_text('{}')
spec = importlib.util.spec_from_file_location('codex_activity', root / 'codex-activity-observer.py')
observer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(observer)
environment = dict(os.environ)
environment.update(TERM='xterm-256color', DISABLE_TELEMETRY='1', DO_NOT_TRACK='1')
server = Fixture('codex', output)
configure('codex', environment, output, server.server_address[1], 'owned-codex-work.py')
config = pathlib.Path('/home/builder/.codex/config.toml')
with config.open('a') as stream:
    stream.write('\n[projects."/home/builder/in/project"]\ntrust_level = "trusted"\n')
configuration = config.read_bytes()
assert os.isatty(0) and os.tcgetpgrp(0) == os.getpgrp()
assert termios.tcgetattr(0)[3] & termios.ISIG
main_pid = os.getpid()
model_pid = os.fork()
if model_pid == 0:
    os.setsid()
    with open('/dev/null', 'rb') as source, (output / 'codex-model-server.log').open('w') as sink:
        os.dup2(source.fileno(), 0)
        os.dup2(sink.fileno(), 1)
        os.dup2(sink.fileno(), 2)
        server.serve_forever()
    os._exit(0)
server.server_close()
observer_pid = os.fork()
if observer_pid == 0:
    terminal_marker = os.dup(1)
    os.setsid()
    with open('/dev/null', 'rb') as source, (output / 'codex-observer.log').open('w') as sink:
        os.dup2(source.fileno(), 0)
        os.dup2(sink.fileno(), 1)
        os.dup2(sink.fileno(), 2)
        lines, states, failures, calls = [], [], [], []
        producer = observer.Producer(uuid.uuid4().hex, lambda prefix, value: lines.append(prefix + json.dumps(value)))
        proxy = None
        thread_id = None
        held_run = None
        disconnected = False
        reconnected_active = False
        released = False
        completed = False
        deadline = time.monotonic() + 45
        failure = None
        observation_configuration = None
        (output / 'codex-config-at-launch.toml').write_bytes(configuration)
        with (output / 'codex-proxy-help.log').open('w') as log:
            subprocess.run(['codex', 'app-server', 'proxy', '--help'], stdout=log, stderr=subprocess.STDOUT,
                env=environment, timeout=10, check=False)
        def model_consumed():
            wire = output / 'codex-wire.json'
            return wire.exists() and any(row.get('hasToolResult') for row in json.loads(wire.read_text()))
        def owned_socket():
            # Pinned CLI daemon diagnostics place its socket here, whereas the
            # proxy's default points into CODEX_HOME. Discover only this owned
            # disposable user's directory; never create/start/redirect a daemon.
            directory = pathlib.Path('/tmp') / ('codex-daemon-' + str(os.getuid()))
            info = directory.lstat()
            if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o022:
                raise observer.ObservationUnavailable('owned-daemon-directory-unverified')
            sockets = []
            for path in directory.iterdir():
                if not re.fullmatch(r'[0-9a-f]{64}', path.name):
                    continue
                info = path.lstat()
                if stat.S_ISSOCK(info.st_mode) and info.st_uid == os.getuid():
                    sockets.append(path)
            if len(sockets) != 1:
                raise observer.ObservationUnavailable('owned-daemon-socket-ambiguous-or-absent')
            (output / 'codex-observer-socket.json').write_text(json.dumps(dict(path=str(sockets[0]))))
            return sockets[0]
        try:
            while time.monotonic() < deadline:
                try:
                    if proxy is None:
                        proxy = observer.ReadOnlyUnixSocket(owned_socket())
                    value = observer.metadata(proxy, root, thread_id)
                    if observation_configuration is None:
                        observation_configuration = config.read_bytes()
                        (output / 'codex-config-at-observation.toml').write_bytes(observation_configuration)
                    thread_id = value['threadId']
                    producer.publish(value)
                    states.append(value)
                    (output / 'codex-observed-metadata.json').write_text(json.dumps(states, indent=2))
                    if value['runActive'] and (output / 'codex-activity-work.json').exists() and not released:
                        if not disconnected:
                            held_run = value['runId']
                            calls.extend(proxy.calls)
                            proxy.close()
                            proxy = None
                            disconnected = True
                        else:
                            assert value['runId'] == held_run, 'Reconnected to another run'
                            reconnected_active = True
                            released = True
                            (output / 'codex-activity-release').write_text('owned release')
                    if released and value['outcome'] == 'completed' and not value['runActive']:
                        completed = True
                        break
                except (OSError, observer.ObservationUnavailable) as error:
                    failures.append(str(error))
                    (output / 'codex-observer-reasons.json').write_text(json.dumps(failures))
                    if proxy is not None:
                        calls.extend(proxy.calls)
                        proxy.close()
                        proxy = None
                time.sleep(0.2)
            assert completed and reconnected_active, 'Actual interactive Codex run/reconnect/completion was not observed'
            assert model_consumed(), 'Actual tool result was not consumed by the model fixture'
            assert config.read_bytes() == observation_configuration, 'CLI configuration changed during observation'
            assert any(line.startswith('LP-EVENT ') and json.loads(line[9:])['kind'] == 'RunFinished' for line in lines)
        except Exception as error:
            failure = type(error).__name__ + ': ' + str(error)
        finally:
            if proxy is not None:
                calls.extend(proxy.calls)
                proxy.close()
            allowed = all(method == 'initialize' or method in observer.ALLOWED_READS for method in calls)
            if not allowed:
                failure = 'Observer sent a non-observational method'
            (output / 'codex-activity-events.log').write_text('\n'.join(lines) + '\n')
            (output / 'codex-config-after-observation.toml').write_bytes(config.read_bytes())
            (output / 'codex-activity-proof.json').write_text(json.dumps(dict(interactive=True, pid=main_pid,
                completed=completed, released=released, reconnectedActive=reconnected_active,
                modelConsumedToolResult=model_consumed(), configUnchanged=config.read_bytes() == observation_configuration,
                configurationChangedBeforeObservation=observation_configuration is not None and observation_configuration != configuration,
                observerAppArmor=pathlib.Path('/proc/self/attr/current').read_text().strip(), transport='unix-websocket',
                observerMethods=calls, failure=failure,
                limitations='Actual foreground console. Owned fixture uses its own model and never/danger-full-access CLI options inside enforced AppArmor; production launch policy unchanged. Approval/question-ID and all short-turn coverage remain unverified.'), indent=2))
            (output / 'results.json').write_text(json.dumps(dict(mode='activity-observe', results=[dict(
                name='codex-activity', exit=0 if failure is None else 1, timedOut=False)])))
            marker = ('AGENT-TOOLS-FAILED\n' if failure else '') + 'AGENT-TOOLS-DONE\n'
            os.write(terminal_marker, marker.encode())
            os.close(terminal_marker)
    os._exit(0)
(output / 'codex-interactive-launch.json').write_text(json.dumps(dict(pid=main_pid,
    foregroundGroup=os.tcgetpgrp(0), modelPid=model_pid, observerPid=observer_pid)))
os.execvpe('/usr/bin/aa-exec', ['/usr/bin/aa-exec', '-p', 'launchpad-agent', '--',
    '/usr/bin/setpriv', '--no-new-privs', '--', 'codex', '--no-alt-screen', '-a', 'never',
    '--sandbox', 'danger-full-access', 'Run exactly python3 owned-codex-work.py codex through your shell tool, then finish. This is an owned activity fixture.'], environment)
