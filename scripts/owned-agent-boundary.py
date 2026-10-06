#!/usr/bin/env python3
"""Invoked only by an actual agent shell tool in an owned disposable guest."""
import errno
import fcntl
import json
import os
import pathlib
import socket
import subprocess
import sys
import time
import urllib.request

agent = sys.argv[1]
assert agent in ['grok', 'codex', 'claude']
root = pathlib.Path('/home/builder/in/project')
assert pathlib.Path.cwd() == root

def identity(pid):
    proc = pathlib.Path('/proc') / str(pid)
    status = dict(line.split(':', 1) for line in (proc / 'status').read_text().splitlines() if ':' in line)
    stat = (proc / 'stat').read_text()
    ticks = int(stat[stat.rfind(')') + 2:].split()[19])
    return dict(pid=pid, startTicks=ticks, parentPid=int(status['PPid']), executable=(proc / 'exe').readlink().as_posix(),
                label=(proc / 'attr/current').read_text().strip(), uid=[int(v) for v in status['Uid'].split()],
                capabilities=status['CapEff'].strip(), noNewPrivileges=status['NoNewPrivs'].strip())

current = identity(os.getpid())
for key, expected in [('label', 'launchpad-agent (enforce)'), ('uid', [1000] * 4),
                      ('capabilities', '0000000000000000'), ('noNewPrivileges', '1')]:
    assert current[key] == expected, (key, current)
ancestry = [current]
parent = current['parentPid']
found = False
for _ in range(12):
    row = identity(parent)
    ancestry.append(row)
    assert row['label'] == current['label'], row
    assert row['uid'] == [1000] * 4 and row['noNewPrivileges'] == '1', row
    if row['executable'].endswith('/' + agent):
        found = True
        break
    parent = row['parentPid']
assert found, 'Actual native agent ancestor absent'
control_path = root / 'owned_control.json'
control = json.loads(control_path.read_text()) if control_path.exists() else None
if control:
    assert control['agent'] == agent
    assert (root / 'project-identity.txt').read_text() == control['fixtureId']
    ready = root / 'fixture-output' / (agent + '-ready.json')
    ready.write_text(json.dumps(dict(fixtureId=control['fixtureId'], identity=current, ancestry=ancestry)))
    release = root / 'fixture-output' / (agent + '-release.json')
    started = time.monotonic()
    while not release.exists():
        assert time.monotonic() - started < 40, 'Concurrent fixture was not released'
        time.sleep(0.025)
    assert json.loads(release.read_text()) == control
results = []
allowed = root / (agent + '-owned-write.txt')
content = 'owned ' + agent + ' tool write' + (':' + control['fixtureId'] if control else '') + '\n'
allowed.write_text(content)
assert allowed.read_text() == content
results.append(dict(operation='project write/read', allowed=True))
# Exercise normal offline Node/npm development through the real agent child,
# including an npm-launched child inheriting the policy. No registry requests,
# real dependencies, credentials or global/home configuration changes.
npm_root = root / ('owned-' + agent + '-npm')
npm_root.mkdir()
(npm_root / 'package.json').write_text(json.dumps(dict(name='launchpad-owned-' + agent,
    version='1.0.0', private=True, scripts={'owned-probe': 'node probe.cjs'})))
(npm_root / 'probe.cjs').write_text("""
const fs = require('fs');
const status = fs.readFileSync('/proc/self/status','utf8');
const label = fs.readFileSync('/proc/self/attr/current','utf8').trim();
if (process.getuid() !== 1000 || label !== 'launchpad-agent (enforce)' ||
    !/^NoNewPrivs:\\s+1$/m.test(status) || !/^CapEff:\\s+0000000000000000$/m.test(status)) process.exit(1);
const packageRoot='/usr/local/lib/node_modules/npm/node_modules/';
const braceVersion=require(packageRoot+'brace-expansion/package.json').version;
const undiciVersion=require(packageRoot+'undici/package.json').version;
const brace=require(packageRoot+'brace-expansion');
const expand=brace.expand||brace;
if(JSON.stringify(expand('{a,b}'))!==JSON.stringify(['a','b']))throw Error('Brace expansion API failure');
const undici=require(packageRoot+'undici');
if(typeof undici.WebSocket!=='function')throw Error('Undici WebSocket API missing');
const evidence={uid:process.getuid(),label,node:process.version,braceVersion,undiciVersion};
const CachePolicy=require(packageRoot+'make-fetch-happen/lib/cache/policy.js');
const {Request,Response}=require(packageRoot+'minipass-fetch');
const cachePolicy=new CachePolicy({request:new Request('https://owned.invalid/cache-fixture'),response:new Response('owned-private-cache',{headers:{'set-cookie':'owned-fixture-only','cache-control':'max-age=60'}}),options:{cachePath:process.cwd()}});
evidence.npmCacheIsShared=cachePolicy.policy._isShared;
if(evidence.npmCacheIsShared!==false)throw Error('npm cache unexpectedly uses shared policy');
if(braceVersion==='5.0.12'){
    evidence.deepBraceResults=expand('{'.repeat(2000)+'a,b'+'}'.repeat(2000)).length;
    evidence.deepBraceFixtureCompleted=true;
}
(async()=>{
    if(undiciVersion==='6.28.1'){
        const http=require('http'),crypto=require('crypto'),sockets=[];
        const server=http.createServer();
        server.on('upgrade',(request,socket)=>{
            sockets.push(socket);
            const accept=crypto.createHash('sha1').update(request.headers['sec-websocket-key']+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
            socket.write('HTTP/1.1 101 Switching Protocols\\r\\nUpgrade: websocket\\r\\nConnection: Upgrade\\r\\nSec-WebSocket-Accept: '+accept+'\\r\\nSec-WebSocket-Protocol: unrequested\\r\\n\\r\\n');
        });
        await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
        try{
            await new Promise((resolve,reject)=>{
                const timer=setTimeout(()=>reject(Error('Owned handshake did not fail normally')),3000);
                const websocket=new undici.WebSocket('ws://127.0.0.1:'+server.address().port);
                websocket.addEventListener('error',()=>{clearTimeout(timer);resolve();},{once:true});
                websocket.addEventListener('open',()=>{clearTimeout(timer);websocket.close();reject(Error('Unrequested subprotocol accepted'));},{once:true});
            });
            evidence.unrequestedSubprotocolFixtureCompleted=true;
        }finally{for(const socket of sockets)socket.destroy();await new Promise(resolve=>server.close(resolve));}
    }
    fs.writeFileSync('npm-child.json',JSON.stringify(evidence));
})().catch(error=>{console.error(error.message);process.exitCode=1;});
""")
npm_version = subprocess.check_output(['npm', '--version'], text=True, timeout=15).strip()
subprocess.run(['npm', 'install', '--offline', '--ignore-scripts', '--no-audit', '--no-fund',
                '--cache', str(npm_root / 'owned-cache')], cwd=npm_root, check=True, timeout=20,
               stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
subprocess.run(['npm', 'run', '--offline', '--no-audit', '--no-fund', 'owned-probe'],
               cwd=npm_root, check=True, timeout=15, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
node_child = json.loads((npm_root / 'npm-child.json').read_text())
assert (npm_root / 'package-lock.json').is_file()
assert node_child['label'] == current['label'] and node_child['uid'] == 1000
results.append(dict(operation='offline npm install and script child', allowed=True,
                    npmVersion=npm_version, child=node_child))
config = pathlib.Path('/home/builder') / ('.' + agent) / ('owned-' + agent + '-lock.txt')
config.parent.mkdir(parents=True, exist_ok=True)
with config.open('w') as stream:
    fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
    stream.write('owned config lock\n')
config.unlink()
if control:
    marker = config.parent / 'launchpad-owned-fixture.txt'
    marker.write_text(control['fixtureId'])
    assert marker.read_text() == control['fixtureId']
results.append(dict(operation='own config write/lock', allowed=True))
for path, flags in [('/etc/launchpad-policy-canary', os.O_WRONLY),
                    ('/home/builder/other-project/private.txt', os.O_RDONLY),
                    ('/home/builder/other-project/private.txt', os.O_WRONLY),
                    ('/dev/hvc0', os.O_WRONLY), ('/dev/vda', os.O_WRONLY),
                    ('/dev/virtio-ports/fence', os.O_WRONLY), ('/dev/ttyS0', os.O_WRONLY)]:
    try:
        fd = os.open(path, flags)  # No creation, truncation, device read or write.
    except OSError as error:
        assert error.errno in (errno.EACCES, errno.EPERM), (path, error)
        results.append(dict(path=path, operation='open', denied=True, errno=error.errno))
    else:
        os.close(fd)
        raise AssertionError('Unexpected access: ' + path)
child_label = subprocess.check_output(['/usr/bin/python3', '-c',
    "import pathlib; print(pathlib.Path('/proc/self/attr/current').read_text().strip())"], text=True).strip()
assert child_label == current['label']
targets = json.loads((root / 'owned_targets.json').read_text())
local = json.loads((root / 'fixture-output' / (agent + '-targets.json')).read_text())
with urllib.request.urlopen('http://127.0.0.1:' + str(local['loopbackPort']) + '/owned-health', timeout=3) as reply:
    assert json.load(reply)['agent'] == agent
results.append(dict(operation='owned guest loopback', allowed=True))
try:
    connection = socket.create_connection((targets['gatewayAddress'], targets['hostPort']), timeout=3)
except OSError as error:
    assert error.errno in (errno.ECONNREFUSED, errno.EHOSTUNREACH, errno.ENETUNREACH, errno.EPERM, errno.EACCES) or isinstance(error, TimeoutError), error
    results.append(dict(operation='owned live host listener', denied=True, errno=error.errno))
else:
    connection.close()
    raise AssertionError('Host gateway unexpectedly reachable')
report = dict(agent=agent, passed=True, identity=current, ancestry=ancestry, childLabel=child_label, results=results,
              fixtureId=control['fixtureId'] if control else None)
(root / 'fixture-output' / (agent + '-boundary.json')).write_text(json.dumps(report, indent=2) + '\n')
print('OWNED-AGENT-BOUNDARY-DONE:' + agent)
