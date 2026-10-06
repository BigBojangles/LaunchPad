#!/usr/bin/env python3
"""Pinned upstream dependency repair; prepare on build host, apply only offline root."""
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tarfile
import uuid


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(blob):
    return hashlib.sha256(blob).hexdigest()


def files_in_archive(path, prefix):
    result = {}
    with tarfile.open(path) as archive:
        for member in archive:
            name = PurePosixPath(member.name)
            require(not name.is_absolute() and '..' not in name.parts, 'Unsafe archive path')
            require(member.isfile() or member.isdir(), 'Archive links/devices are not accepted')
            if member.isfile() and member.name.startswith(prefix):
                relative = member.name[len(prefix):]
                require(relative and relative not in result, 'Duplicate archive member')
                result[relative] = (archive.extractfile(member).read(), member.mode & 0o111)
    require(result and 'package.json' in result, 'Missing package tree')
    return result


def tree_digest(files):
    value = hashlib.sha256()
    for name, (blob, executable) in sorted(files.items()):
        value.update(name.encode() + b'\0' + str(executable).encode() + b'\0' + sha(blob).encode() + b'\n')
    return value.hexdigest()


def disk_files(root):
    require(root.is_dir() and not root.is_symlink(), 'Linked/missing package root')
    result = {}
    for path in root.rglob('*'):
        require(not path.is_symlink(), 'Linked package member')
        if path.is_file():
            result[path.relative_to(root).as_posix()] = (path.read_bytes(), path.stat().st_mode & 0o111)
        else:
            require(path.is_dir(), 'Special package member')
    return result


def verify_archive(root, row):
    name = row['file']
    require(PurePosixPath(name).name == name and '\\' not in name, 'Unsafe input name')
    path = root / name
    require(path.is_file() and not path.is_symlink(), 'Missing/linked patch archive')
    blob = path.read_bytes()
    require(sha(blob) == row['sha256'], 'Patch archive SHA256 differs')
    require('sha512-' + base64.b64encode(hashlib.sha512(blob).digest()).decode() == row['integrity'], 'Patch publisher integrity differs')
    files = files_in_archive(path, 'package/')
    metadata = json.loads(files['package.json'][0])
    require(metadata['name'] == row['name'] and metadata['version'] == row['version'], 'Wrong patch package')
    return files, metadata


def prepare(base_root, output_root):
    project = Path(__file__).resolve().parent.parent
    reports = project / 'tests/LaunchPad.Tests/TestResults/migration'
    require(base_root.resolve().is_relative_to(reports) and output_root.resolve().is_relative_to(reports), 'Private build inputs only')
    require(not (output_root / 'security-packages.json').exists(), 'Retain existing manifest')
    lock = json.loads((project / 'scripts/npm-security-patches.json').read_text())
    base_lock = json.loads((project / 'scripts/guest-security-packages.json').read_text())
    manifest = json.loads((base_root / 'security-packages.json').read_text())
    require(manifest['npm']['version'] == lock['npmVersion'], 'Unexpected upstream npm')
    archive = base_root / 'packages' / manifest['npm']['file']
    require(sha(archive.read_bytes()) == lock['upstreamNpmSha256'] == base_lock['npm']['sha256'], 'Unexpected upstream npm archive')
    packages = output_root / 'packages'
    packages.mkdir()
    for row in manifest['debian']['packages'] + [manifest['npm']]:
        source = base_root / 'packages' / row['file']
        require(sha(source.read_bytes()) == row['sha256'], 'Changed base input')
        shutil.copyfile(source, packages / row['file'])
    patches = []
    for pin in lock['packages']:
        row = dict(pin, file=pin['name'] + '-' + pin['version'] + '.tgz', url='https://registry.npmjs.org/' + pin['name'] + '/-/' + pin['name'] + '-' + pin['version'] + '.tgz')
        files, metadata = verify_archive(output_root, row)
        original = files_in_archive(archive, 'package/node_modules/' + row['name'] + '/')
        require(json.loads(original['package.json'][0])['version'] == row['baseVersion'], 'Wrong bundled original')
        row['baseTreeSha256'] = tree_digest(original)
        row['patchedTreeSha256'] = tree_digest(files)
        row['dependencies'] = metadata.get('dependencies', {})
        row['nodeEngine'] = metadata.get('engines', {}).get('node', '*')
        shutil.copyfile(output_root / row['file'], packages / row['file'])
        patches.append(row)
    manifest['npm']['patches'] = patches
    manifest['npm']['patchHelperSha256'] = sha(Path(__file__).read_bytes())
    manifest['npm']['patchLockSha256'] = sha((project / 'scripts/npm-security-patches.json').read_bytes())
    manifest['npm']['patchProvenance'] = 'LaunchPad repair using separately pinned official upstream patch archives; npm vendor archive unchanged.'
    (output_root / 'security-packages.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps({'patches': patches, 'manifestSha256': sha((output_root / 'security-packages.json').read_bytes())}, indent=2))


def node_check(root, name, version, dependencies, engine):
    source = """
const [root,name,version,dependencies,engine]=process.argv.slice(1);
const semver=require(root+'/node_modules/semver');
if(!semver.satisfies(process.versions.node,engine))throw Error('Node engine mismatch');
for(const [dependency,range] of Object.entries(JSON.parse(dependencies))){
 const pkg=require(require.resolve(dependency+'/package.json',{paths:[root+'/node_modules/'+name]}));
 if(!semver.satisfies(pkg.version,range))throw Error('Patch dependency mismatch: '+dependency);
}
const fs=require('fs'),path=require('path');
for(const pkgPath of fs.readdirSync(root+'/node_modules')){
 const entries=pkgPath.startsWith('@')?fs.readdirSync(root+'/node_modules/'+pkgPath).map(p=>pkgPath+'/'+p):[pkgPath];
 for(const entry of entries){
  const file=root+'/node_modules/'+entry+'/package.json';if(!fs.existsSync(file))continue;
  const pkg=require(file),range=pkg.dependencies?.[name];
  if(range&&!semver.satisfies(version,range))throw Error('Dependent range mismatch: '+entry);
 }
}
const current=require(root+'/node_modules/'+name+'/package.json').version;
if(!semver.valid(current)||!semver.valid(version))throw Error('Invalid version');
console.log(semver.gt(current,version)?'newer':semver.eq(current,version)?'equal':'older');
"""
    return subprocess.check_output(['node', '-e', source, str(root), name, version, json.dumps(dependencies), engine], text=True, timeout=15).strip()


def guest(mode):
    require(os.geteuid() == 0, 'Offline root maintenance/build only')
    if mode not in ('build', 'preflight-build'):
        require(Path('/proc/1/comm').read_text().strip() == 'sh', 'Normal agent runtime cannot apply repairs')
    inputs = Path('/opt/launchpad-security-inputs')
    require(inputs.resolve() == inputs and inputs.is_dir(), 'Linked inputs root')
    manifest = json.loads((inputs / 'security-packages.json').read_text())
    require(sha(Path(__file__).read_bytes()) == manifest['npm']['patchHelperSha256'], 'Maintenance patch helper changed')
    root = Path('/usr/local/lib/node_modules/npm')
    require(root.resolve() == root and root.is_dir(), 'Linked npm root')
    require((root / 'node_modules').resolve() == root / 'node_modules', 'Linked bundled dependency directory')
    require([row['name'] for row in manifest['npm']['patches']] == ['brace-expansion', 'undici'], 'Unexpected patch targets')
    plans = []
    for row in manifest['npm']['patches']:
        files, metadata = verify_archive(inputs, row)
        require(tree_digest(files) == row['patchedTreeSha256'], 'Patched tree changed')
        target = root / 'node_modules' / row['name']
        require(target.resolve() == target, 'Linked dependency ancestry')
        state = node_check(root, row['name'], row['version'], row['dependencies'], row['nodeEngine'])
        current = disk_files(target)
        if state == 'older':
            require(json.loads(current['package.json'][0])['version'] == row['baseVersion'] and tree_digest(current) == row['baseTreeSha256'], 'Refusing unrecognized/modified older dependency')
        elif state == 'equal':
            require(tree_digest(current) == row['patchedTreeSha256'], 'Equal-version dependency differs from vendor archive')
        plans.append((row, files, state, target))
    if mode in ('preflight', 'preflight-build'):
        print('NPM-PATCH-PREFLIGHT-OK')
        return
    receipt = []
    for row, files, state, target in plans:
        if state == 'older':
            staged = target.parent / ('.launchpad-patch-' + row['name'] + '-' + uuid.uuid4().hex)
            staged.mkdir(mode=0o755)
            for name, (blob, executable) in files.items():
                path = staged / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(blob)
                path.chmod(0o755 if executable else 0o644)
            require(tree_digest(disk_files(staged)) == row['patchedTreeSha256'], 'Staged tree differs')
            before = target.parent / (staged.name + '-before')
            target.rename(before)
            try:
                staged.rename(target)
            except BaseException:
                before.rename(target)
                raise
            require(before.parent == root / 'node_modules' and before.name.startswith('.launchpad-patch-') and not before.is_symlink(), 'Unsafe replaced package directory')
            shutil.rmtree(before)
            print('NPM-PATCH-INSTALLED:' + row['name'] + ':' + row['version'])
        else:
            print('PRESERVE-NPM-DEPENDENCY:' + row['name'] + ':' + state)
        receipt.append(dict(name=row['name'], disposition=state, version=json.loads((target / 'package.json').read_text())['version'], treeSha256=tree_digest(disk_files(target)), pinnedArchiveSha256=row['sha256']))
    output = Path('/usr/local/share/launchpad/npm-security-patches.json')
    require(not output.is_symlink() and output.parent.resolve() == output.parent, 'Linked receipt')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(dict(schema=1, patches=receipt), indent=2) + '\n')
    print('NPM-SECURITY-PATCHES-OK')


if __name__ == '__main__':
    if sys.argv[1] == 'prepare':
        prepare(Path(sys.argv[2]), Path(sys.argv[3]))
    else:
        require(sys.argv[1] in ('build', 'maintenance', 'preflight', 'preflight-build'), 'Unknown operation')
        guest(sys.argv[1])
