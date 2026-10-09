"""Fail closed on unreviewed guest leftovers and stale maintenance payloads.

Install pinned reader packages in src/LaunchPad/obj/package/image-audit-tools:
  py -3 -m pip install --target <that directory> -r scripts/release-audit-requirements.txt
Never install them into the shipped guest or global Python environment.
"""
import argparse
import hashlib
import json
import posixpath
import re
import stat
import sys
import tarfile
from pathlib import Path
from image_audit_lib import open_filesystem, walk, fingerprint, resolve, sha256_file

def forbidden(path, policy):
    return any(path == p or path.startswith(p + '/') for p in policy['removedTrees'])

def verify(root, policy, tools):
    sys.path.insert(0, str(tools))
    images = root / 'images'
    m = json.loads((images/'runtime.json').read_text(encoding='utf-8-sig'))
    if not m.get('directBoot'):
        raise ValueError('Release image must declare matching direct boot')
    # New release image is independent of unsanitized historical backing layers.
    if m.get('dependencies') != []:
        raise ValueError('Clean release image must be standalone')
    def artifact(item):
        name = item['file']
        if Path(name).name != name or '/' in name or '\\' in name:
            raise ValueError('Unsafe artifact name')
        path = images/name
        if path.is_symlink() or sha256_file(path) != item['sha256']:
            raise ValueError('Artifact changed: '+name)
        return path
    image = artifact(m['image'])
    # Reject even hidden backing references, independent of manifest claims.
    from dissect.hypervisor.disk.qcow2 import QCow2
    q = QCow2(image)
    if q.header.backing_file_offset or q.needs_data_file:
        raise ValueError('Release disk has an external backing/data file')
    artifact(m['directBoot']['kernel']); artifact(m['directBoot']['initrd'])
    fs = open_filesystem(image)
    if int(fs.sb.s_state) != 1 or int(fs.sb.s_feature_incompat) & 4:
        raise ValueError('Filesystem is not cleanly closed')
    builders = [line.split(':') for line in fs.get('/etc/shadow').open().read().decode('utf-8').splitlines()
                if line.startswith('builder:')]
    if len(builders) != 1 or len(builders[0]) != 9 or builders[0][1] != '*':
        raise ValueError("Release builder password must be locked with '*' and contain no inherited password hash")
    exceptions = {row['path']: row for row in policy['reviewedNameExceptions']}
    clean_home = {'/home/builder','/home/builder/.bashrc','/home/builder/.profile','/home/builder/.bash_logout',
                  '/root','/root/.bashrc','/root/.profile','/root/.ssh'}
    errors = []; count = 0; matched = 0; required = {}
    for path, node in walk(fs):
        count += 1
        if path.startswith(('/home/builder/','/root/')) and path not in clean_home:
            errors.append('Prefilled user/provider/project state remains: '+path)
        if path in ('/var/lib/launchpad/import.json','/etc/gitconfig','/root/.gitconfig','/root/.git-credentials'):
            errors.append('Prefilled project/Git state remains: '+path)
        if forbidden(path, policy):
            errors.append('Removed path remains: '+path)
        if path.startswith(('/var/cache/apt/archives/','/var/lib/apt/lists/')) and not stat.S_ISDIR(node.filetype):
            errors.append('Build download cache remains: '+path)
        if path.startswith('/run/') and not stat.S_ISDIR(node.filetype):
            errors.append('Transient disk state remains: '+path)
        if path.startswith('/var/log/apt/') and not stat.S_ISDIR(node.filetype):
            errors.append('Build log remains: '+path)
        if re.search(r'test|proof|probe|fixture',posixpath.basename(path),re.I):
            allowed = exceptions.get(path)
            if not allowed or fingerprint(node) != allowed['fingerprint']:
                errors.append('Unreviewed forbidden name/content: '+path)
            else:
                matched += 1
        if path.startswith(('/usr/local/bin/','/usr/local/sbin/','/usr/local/lib/launchpad/','/opt/','/home/builder/')):
            if re.search(r'owned-|inspect|diagnos|repair|staging',posixpath.basename(path),re.I) and path not in exceptions:
                errors.append('Custom build/diagnostic leftover: '+path)
        if path in policy['requiredFiles']:
            required[path] = node
    for path in policy['requiredFiles']:
        if path not in required:
            errors.append('Required product file missing: '+path)
    helper = fs.get('/usr/local/bin/launchpad-session').open().read()
    source = Path(__file__).resolve().parent
    if helper != (source/'guest-session-supervisor.pl').read_bytes():
        errors.append('Image supervisor differs from reviewed production source')
    if b'busy\\nneeds-an-answer\\n' in helper:
        errors.append('Synthetic startup status remains')
    unit = fs.get('/etc/systemd/system/launchpad-session.service').open().read()
    if unit != (source/'launchpad-session.service').read_bytes():
        errors.append('Image unit differs from reviewed production source')
    if b'ExecStart=/usr/local/bin/launchpad-session\n' not in unit or b'bl-proof' in unit:
        errors.append('Production startup unit is not wired correctly')
    enabled = fs.get('/etc/systemd/system/multi-user.target.wants/launchpad-session.service')
    if not stat.S_ISLNK(enabled.filetype) or enabled.link != '/etc/systemd/system/launchpad-session.service':
        errors.append('Production startup is not enabled')
    if fs.get('/etc/machine-id').size != 0 or resolve(fs,'/var/lib/dbus/machine-id').size != 0:
        errors.append('Template contains a pre-generated machine ID')
    dbus = fs.get('/var/lib/dbus/machine-id')
    if not stat.S_ISLNK(dbus.filetype) or dbus.link != '/etc/machine-id':
        errors.append('dbus identity must refer to canonical machine-id')
    kit = json.loads((images/m.get('maintenanceManifest','maintenance.json')).read_text(encoding='utf-8-sig'))
    if kit['version'] != m['version']:
        errors.append('Maintenance/runtime version mismatch')
    payload = artifact(kit['payload'])
    if kit.get('kernel') != m['directBoot']['kernel'] or kit.get('initrd') != m['directBoot']['initrd']:
        errors.append('Maintenance boot assets differ from selected direct boot')
    if hashlib.sha256(helper).hexdigest() != kit['guestScriptSha256']:
        errors.append('Maintenance supervisor identity mismatch')
    with tarfile.open(payload,'r:gz') as tar:
        members = tar.getmembers()
        names = {x.name for x in members}
        if len(names) != len(members):
            errors.append('Duplicate maintenance archive member')
        for member in members:
            if not member.isfile() or member.name.startswith('/') or '..' in Path(member.name).parts:
                errors.append('Unsafe maintenance member: '+member.name)
            if re.search(r'test|proof|probe|fixture',posixpath.basename(member.name),re.I):
                errors.append('Rejected maintenance member: '+member.name)
        for name in ('launchpad-session','launchpad-session.service','apply.sh','SHA256SUMS'):
            if name not in names: errors.append('Required maintenance member missing: '+name)
        if 'SHA256SUMS' in names:
            rows = tar.extractfile('SHA256SUMS').read().decode('ascii').splitlines()
            declared = {}
            for row in rows:
                checksum, separator, name = row.partition('  ')
                if not separator or not re.fullmatch('[0-9a-f]{64}', checksum) or name in declared:
                    errors.append('Invalid maintenance checksum entry')
                    continue
                declared[name] = checksum
            if set(declared) != names-{'SHA256SUMS'}:
                errors.append('Maintenance checksum inventory is incomplete')
            for member in members:
                if member.isfile() and member.name in declared and hashlib.sha256(tar.extractfile(member).read()).hexdigest() != declared[member.name]:
                    errors.append('Maintenance internal checksum failed: '+member.name)
        if 'launchpad-session' in names and tar.extractfile('launchpad-session').read() != helper:
            errors.append('Maintenance can install a different supervisor')
        if 'launchpad-session.service' in names and tar.extractfile('launchpad-session.service').read() != unit:
            errors.append('Maintenance can install a different service')
        if 'apply.sh' in names:
            apply = tar.extractfile('apply.sh').read().decode('utf-8')
            if apply.encode('utf-8') != (source/'guest-maintenance-apply.sh').read_bytes():
                errors.append('Maintenance installer differs from reviewed source')
            if 'install -o root -g root -m 0750 launchpad-session /usr/local/bin/launchpad-session' not in apply:
                errors.append('Maintenance installer does not select production supervisor')
            for line in apply.splitlines():
                if re.search(r'bl-proof|windows-test',line) and not line.lstrip().startswith(('#','rm -f ')):
                    errors.append('Maintenance can restore rejected functionality: '+line)
    if errors:
        raise ValueError('\n'.join(errors[:100]) + ('\nAdditional violations omitted' if len(errors)>100 else ''))
    return {'result':'PASS','image':str(image),'sha256':m['image']['sha256'],
            'entries':count,'reviewedNameMatches':matched,'standalone':True,
            'templateMachineIdEmpty':True,'builderPasswordLocked':True,'maintenanceCannotRestoreOldNames':True,
            'method':'offline allocated files, content-bound exceptions and matching maintenance archive; no boot'}

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--runtime-root',type=Path,required=True)
    parser.add_argument('--policy',type=Path,default=Path(__file__).with_name('release-image-policy.json'))
    parser.add_argument('--tools',type=Path,default=Path(__file__).resolve().parents[1]/'src/LaunchPad/obj/package/image-audit-tools')
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    try:
        result=verify(args.runtime_root.resolve(),json.loads(args.policy.read_text()),args.tools)
    except Exception as error:
        print('Release image check FAILED: '+str(error),file=sys.stderr)
        return 1
    if args.output: args.output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result))
    return 0

if __name__=='__main__': sys.exit(main())
