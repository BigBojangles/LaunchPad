#!/usr/bin/env python3
"""Owned TCP controls only. Capture each state/reason without equating it to confinement."""
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import xml.etree.ElementTree as ET

manifest_path = Path('expected-connectivity.json')
manifest = json.loads(manifest_path.read_text())
control = json.loads(Path('host-control.json').read_text())
if os.getuid() != 1000 or manifest.get('schemaVersion') != 1:
    raise SystemExit('Wrong identity or connectivity manifest schema')
expected_specs = {
    'guest-loopback-open': ('127.0.0.1', 'guest-owned-listening-socket', 'open', ['open']),
    'guest-loopback-closed': ('127.0.0.1', 'guest-owned-bound-non-listening-socket', 'closed', ['closed']),
    'host-gateway-owned-listener': ('10.0.2.2', 'actual-host-owned-loopback-listener', 'denied', ['closed', 'filtered', 'unreachable']),
}
targets = manifest.get('targets', [])
if len(targets) != 3 or {item.get('id') for item in targets} != set(expected_specs):
    raise SystemExit('Only the three explicit owned controls are permitted')
for target in targets:
    actual = tuple(target.get(key) for key in ('address', 'portSource', 'expected', 'allowedObservations'))
    if actual != expected_specs[target['id']]:
        raise SystemExit('Connectivity scope/expectation differs from the approved fixture')
host_port = control.get('port')
if type(host_port) is not int or not 1 <= host_port <= 65535 or control.get('address') != '127.0.0.1':
    raise SystemExit('Invalid owned host listener')
binary = Path('.lp-nmap-build/nmap')
binary_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
manifest_hash = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
observations = []
with socket.socket() as opened, socket.socket() as closed:
    opened.bind(('127.0.0.1', 0))
    opened.listen(8)
    # Reserve a different port without listening. It remains owned and closed,
    # avoiding a race where an unrelated service occupies a released port.
    closed.bind(('127.0.0.1', 0))
    ports = {
        'guest-loopback-open': opened.getsockname()[1],
        'guest-loopback-closed': closed.getsockname()[1],
        'host-gateway-owned-listener': host_port,
    }
    for target in targets:
        target_id = target['id']
        report_path = Path('nmap-' + target_id + '-private.xml')
        command = [str(binary.resolve()), '--unprivileged', '-sT', '-Pn', '-n', '--reason',
                   '--max-retries', '0', '--host-timeout', '20s', '-p', str(ports[target_id]),
                   '-oX', str(report_path), target['address']]
        observed = {'id': target_id, 'address': target['address'], 'port': ports[target_id],
                    'expected': target['expected'], 'command': command, 'state': 'incomplete',
                    'reason': None, 'exitCode': None, 'matchesExpectation': False}
        try:
            process = subprocess.run(command, capture_output=True, timeout=25)
            observed['exitCode'] = process.returncode
            Path('nmap-' + target_id + '-stderr-private.txt').write_bytes(process.stderr)
            tree = ET.parse(report_path)
            finish = tree.find('./runstats/finished')
            port = tree.find('./host/ports/port')
            host = tree.find('./host/status')
            if process.returncode == 0 and finish is not None and finish.get('exit') == 'success':
                if port is not None:
                    if int(port.get('portid', '-1')) != ports[target_id]:
                        raise ValueError('Scanner reported a different port')
                    state = port.find('state')
                    if state is not None:
                        observed['state'], observed['reason'] = state.get('state'), state.get('reason')
                elif host is not None and host.get('state') == 'down':
                    observed['state'], observed['reason'] = 'unreachable', host.get('reason')
                else:
                    # Nmap can collapse the single requested port into extraports.
                    # Accept it only when scaninfo proves exactly that TCP port.
                    scaninfo = tree.find('./scaninfo')
                    extra = tree.find('./host/ports/extraports')
                    if (scaninfo is not None and scaninfo.get('protocol') == 'tcp'
                            and scaninfo.get('numservices') == '1'
                            and scaninfo.get('services') == str(ports[target_id])
                            and extra is not None and extra.get('count') == '1'):
                        reason = extra.find('extrareasons')
                        observed['state'] = extra.get('state')
                        observed['reason'] = reason.get('reason') if reason is not None else None
            observed['matchesExpectation'] = observed['state'] in target['allowedObservations']
        except (OSError, ValueError, ET.ParseError, subprocess.TimeoutExpired) as error:
            observed['error'] = str(error)
        observations.append(observed)
summary = {
    'uid': os.getuid(), 'effectiveProfile': Path('/proc/self/attr/current').read_text().strip(),
    'binarySha256': binary_hash, 'manifestSha256': manifest_hash,
    'toolVersion': '7.991', 'observations': observations,
    'boundaryAssertionsPassed': all(item['matchesExpectation'] for item in observations),
    'limitations': 'Custom builder context and three owned controls only. This does not prove bundled-agent child confinement or all host boundaries.',
}
Path('nmap-coverage-private.json').write_text(json.dumps(summary, indent=2))
print('NMAP-COVERAGE-JSON:' + json.dumps(summary, separators=(',', ':')))
sys.exit(0 if summary['boundaryAssertionsPassed'] else 1)
