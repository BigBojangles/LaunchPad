"""Build a clean immutable runtime from pinned inputs inside this repository.

Only a fresh build overlay is booted. The original image and its backing chain
are opened read-only. No existing session, installed runtime or external path is
changed. The result is standalone; validation precedes selecting it for release.
"""
import argparse
import gzip
import hashlib
import io
import json
import os
import shutil
import socket
import subprocess
import sys
import tarfile
import threading
import time
from pathlib import Path
from image_audit_lib import sha256_file

REPO=Path(__file__).resolve().parents[1]

def owned(path):
    path=path.resolve()
    if not path.is_relative_to(REPO): raise ValueError('Build paths must remain in this repository')
    return path

def archive(path, entries):
    # Reproducible archive: no host user IDs, absolute paths or current mtimes.
    with path.open('wb') as output, gzip.GzipFile(filename='',fileobj=output,mode='wb',mtime=0) as zipped:
        with tarfile.open(fileobj=zipped,mode='w') as tar:
            for name,data in sorted(entries.items()):
                info=tarfile.TarInfo(name);info.size=len(data);info.uid=info.gid=0;info.mtime=0;info.mode=0o644
                tar.addfile(info,io.BytesIO(data))

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--runtime-root',type=Path,required=True)
    p.add_argument('--scratch',type=Path,required=True)
    p.add_argument('--qemu-img',type=Path,required=True)
    p.add_argument('--firmware',type=Path,required=True)
    p.add_argument('--version',default='runtime-20261008-clean')
    args=p.parse_args()
    runtime=owned(args.runtime_root); scratch=owned(args.scratch)
    img=owned(args.qemu_img);firmware=owned(args.firmware)
    if scratch.exists(): raise ValueError('Fresh build directory required; prior diagnostics are preserved')
    if not args.version.startswith('runtime-') or any(c not in 'abcdefghijklmnopqrstuvwxyz0123456789-' for c in args.version):
        raise ValueError('Unsafe version')
    scratch.mkdir(parents=True)
    seed=json.loads((runtime/'images/runtime.json').read_text(encoding='utf-8-sig'))
    original=[]
    for item in [seed['image']]+seed['dependencies']:
        name=item['file']
        if Path(name).name!=name or '/' in name or '\\' in name:raise ValueError('Unsafe seed artifact')
        source=owned(runtime/'images'/name)
        if source.is_symlink() or sha256_file(source)!=item['sha256']:raise ValueError('Pinned seed changed')
        original.append({'path':str(source),'sha256':item['sha256']})
    kernel=owned(runtime/'images'/seed['directBoot']['kernel']['file'])
    initrd=owned(runtime/'images'/seed['directBoot']['initrd']['file'])
    for path,row in [(kernel,seed['directBoot']['kernel']),(initrd,seed['directBoot']['initrd'])]:
        if sha256_file(path)!=row['sha256']:raise ValueError('Boot asset changed')
    oldkit=json.loads((runtime/'images'/seed.get('maintenanceManifest','maintenance.json')).read_text(encoding='utf-8-sig'))
    oldpayload=owned(runtime/'images'/oldkit['payload']['file'])
    if sha256_file(oldpayload)!=oldkit['payload']['sha256']:raise ValueError('Seed maintenance changed')
    entries={
        'guest-session-supervisor.pl':(REPO/'scripts/guest-session-supervisor.pl').read_bytes(),
        'launchpad-session.service':(REPO/'scripts/launchpad-session.service').read_bytes(),
        'clean.sh':(REPO/'scripts/guest-release-clean.sh').read_bytes()
    }
    entries['SHA256SUMS']=''.join(hashlib.sha256(data).hexdigest()+'  '+name+'\n' for name,data in sorted(entries.items())).encode()
    payload=scratch/'image-input.tar.gz';archive(payload,entries)
    disk=scratch/'build.qcow2'
    subprocess.run([str(img),'create','-q','-f','qcow2','-F','qcow2','-b',str(runtime/'images'/seed['image']['file']),str(disk)],check=True)
    held=[]
    for _ in range(3):
        sock=socket.socket();sock.bind(('127.0.0.1',0));held.append(sock)
    ports=[s.getsockname()[1] for s in held]
    for s in held:s.close()
    qemu=owned(runtime/'qemu/fence/qemu-system-x86_64.exe')
    command=[str(qemu),'-machine','q35','-accel','whpx','-cpu','max','-m','2048','-smp','2',
             '-display','none','-vga','none','-nodefaults','-no-reboot','-net','none','-L',str(firmware),
             '-drive','file='+str(disk)+',if=virtio,format=qcow2,cache=writethrough,discard=unmap,detect-zeroes=unmap',
             '-kernel',str(kernel),'-initrd',str(initrd),'-append','root=/dev/vda1 ro init=/bin/sh console=ttyS0,115200 panic=-1',
             '-serial',f'tcp:127.0.0.1:{ports[1]},server=on,wait=off',
             '-qmp',f'tcp:127.0.0.1:{ports[0]},server=on,wait=off',
             '-device','virtio-serial-pci,id=m','-chardev',f'socket,id=p,host=127.0.0.1,port={ports[2]},server=on,wait=off',
             '-device','virtserialport,bus=m.0,chardev=p,name=launchpad-maintenance']
    (scratch/'command.json').write_text(json.dumps(command,indent=2))
    capture=bytearray();lock=threading.Lock();start=time.monotonic();forced=False
    with (scratch/'stderr.txt').open('wb') as errors:
        process=subprocess.Popen(command,cwd=scratch,stderr=errors,creationflags=subprocess.CREATE_NO_WINDOW)
        def connect(port):
            deadline=time.monotonic()+30
            while time.monotonic()<deadline:
                if process.poll() is not None:raise RuntimeError('Build VM exited: '+(scratch/'stderr.txt').read_text())
                try:return socket.create_connection(('127.0.0.1',port),timeout=1)
                except OSError:time.sleep(.1)
            raise TimeoutError('VM transport unavailable')
        console=None;data=None
        try:
            console=connect(ports[1]);data=connect(ports[2]);console.settimeout(None);data.settimeout(90)
            def read():
                while True:
                    try:chunk=console.recv(65536)
                    except OSError:break
                    if not chunk:break
                    with lock:capture.extend(chunk)
            thread=threading.Thread(target=read,daemon=True);thread.start()
            def wait(marker,seconds=90):
                deadline=time.monotonic()+seconds
                while time.monotonic()<deadline:
                    with lock:found=marker.encode() in capture
                    if found:return
                    if process.poll() is not None:raise RuntimeError('Build VM ended before '+marker)
                    time.sleep(.1)
                raise TimeoutError('Build did not reach '+marker)
            def send(text):console.sendall(text.encode())
            wait("can't access tty")
            send('mountpoint -q /proc || mount -t proc proc /proc\nmountpoint -q /sys || mount -t sysfs sysfs /sys\nmount -t tmpfs tmpfs /run\nstty -echo -F /dev/ttyS0\nmount -o remount,rw /\nmkdir -p /run/launchpad-image-build/payload\ntouch /run/launchpad-image-build/owned-template\nmodprobe virtio_console\nport=\nfor n in /sys/class/virtio-ports/*; do [ "$(cat "$n/name" 2>/dev/null)" = launchpad-maintenance ] && port=/dev/${n##*/}; done\n[ -n "$port" ] && exec 3<"$port" && printf "\\nLP-BUILD-READY\\n"\n')
            wait('\nLP-BUILD-READY')
            blob=payload.read_bytes();digest=hashlib.sha256(blob).hexdigest()
            send(f'dd of=/run/launchpad-image-build/input.tar.gz bs=65536 count={len(blob)} iflag=fullblock,count_bytes status=none <&3 && printf "%s  /run/launchpad-image-build/input.tar.gz\\n" "{digest}" | sha256sum -c - && tar -xzf /run/launchpad-image-build/input.tar.gz -C /run/launchpad-image-build/payload && printf "\\nLP-BUILD-INPUT-OK\\n"\n')
            data.sendall(blob);wait('\nLP-BUILD-INPUT-OK')
            send('sh /run/launchpad-image-build/payload/clean.sh && printf "\\nLP-BUILD-CLEANED\\n"\n')
            wait('\nLP-BUILD-CLEANED',180)
            send('cd /\nsync\nmount -o remount,ro / && printf "\\nLP-BUILD-SYNCED\\n"\n/sbin/poweroff -f\n')
            wait('\nLP-BUILD-SYNCED');process.wait(timeout=30)
            if process.returncode!=0:raise RuntimeError('Unclean build VM exit')
        finally:
            if process.poll() is None:process.kill();process.wait();forced=True
            if console:console.close()
            if data:data.close()
            with lock:(scratch/'build-console.log').write_bytes(capture)
            (scratch/'build-state.json').write_text(json.dumps({'forcedStop':forced,'elapsedSeconds':time.monotonic()-start,'originalImages':original},indent=2))
    bundle=scratch/'bundle';out=bundle/'images';out.mkdir(parents=True)
    output=out/('debian-12-builder-'+args.version+'.qcow2')
    subprocess.run([str(img),'convert','-p','-f','qcow2','-O','qcow2','-c',str(disk),str(output)],check=True)
    subprocess.run([str(img),'check',str(output)],check=True)
    maintenance={
        'launchpad-session':entries['guest-session-supervisor.pl'],
        'launchpad-session.service':entries['launchpad-session.service'],
        'apply.sh':(REPO/'scripts/guest-maintenance-apply.sh').read_bytes()
    }
    # Preserve matching, previously hash-verified dependencies; reject any old startup/bridge entries.
    with tarfile.open(oldpayload,'r:gz') as tar:
        for name in ('launchpad-agent','usr.local.bin.grok','quiesce.py','procps_4.0.2-3_amd64.deb','libproc2-0_4.0.2-3_amd64.deb'):
            maintenance[name]=tar.extractfile(name).read()
    maintenance['SHA256SUMS']=''.join(hashlib.sha256(v).hexdigest()+'  '+k+'\n' for k,v in sorted(maintenance.items())).encode()
    kitname='launchpad-maintenance-'+args.version+'.tar.gz';archive(out/kitname,maintenance)
    def row(path):return {'file':path.name,'sha256':sha256_file(path)}
    for path in (kernel,initrd):shutil.copyfile(path,out/path.name)
    m=dict(seed,version=args.version,image=row(output),dependencies=[],maintenanceManifest='maintenance.json')
    m['capabilities'] = sorted(set(m.get('capabilities', [])) | {'agent-choice', 'config-home-builder'})
    kit=dict(oldkit,version=args.version,kernel=row(out/kernel.name),initrd=row(out/initrd.name),payload=row(out/kitname),guestScriptSha256=hashlib.sha256(maintenance['launchpad-session']).hexdigest())
    (out/'runtime.json').write_text(json.dumps(m,indent=2)+'\n');(out/'maintenance.json').write_text(json.dumps(kit,indent=2)+'\n')
    for item in original:
        if sha256_file(item['path'])!=item['sha256']:raise RuntimeError('Original input changed')
    print(json.dumps({'result':'BUILT','bundle':str(bundle),'image':str(output),'sha256':m['image']['sha256'],'standalone':True,'originalImagesUnchanged':True}))

if __name__=='__main__':main()
