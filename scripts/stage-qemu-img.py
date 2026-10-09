"""Prove a locally built qemu-img and stage only its actual non-system DLLs."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inputs', type=pathlib.Path, required=True)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    inputs = args.inputs.resolve()
    if not inputs.is_relative_to(root):
        raise ValueError('Inputs must remain in the workspace')
    work = root / 'src/LaunchPad/obj/package/qemu-img-build'
    candidate = work / 'out/qemu-img.exe'
    stage = work / 'utility-proof'
    stage.mkdir(exist_ok=True)
    shutil.copyfile(candidate, stage / 'qemu-img.exe')

    def linux(path):
        return '/mnt/' + path.drive[0].lower() + path.as_posix()[2:]

    def imports(path):
        output = subprocess.check_output(['wsl.exe', '-d', 'Ubuntu', '-u', 'root', '--',
                                         'x86_64-w64-mingw32-objdump', '-p', linux(path)])
        return re.findall(rb'DLL Name:\s*([^\s]+)', output)

    pending = [stage / 'qemu-img.exe']
    known = {'qemu-img.exe'}
    windows_dlls = {
        'advapi32.dll', 'bcrypt.dll', 'bcryptprimitives.dll', 'cfgmgr32.dll',
        'comdlg32.dll', 'crypt32.dll', 'dnsapi.dll', 'dwmapi.dll', 'gdi32.dll',
        'imm32.dll', 'iphlpapi.dll', 'kernel32.dll', 'kernelbase.dll', 'mpr.dll',
        'msvcrt.dll', 'ncrypt.dll', 'netapi32.dll', 'normaliz.dll', 'ntdll.dll',
        'ole32.dll', 'oleaut32.dll', 'pathcch.dll', 'powrprof.dll', 'psapi.dll',
        'rpcrt4.dll', 'secur32.dll', 'setupapi.dll', 'shell32.dll', 'shlwapi.dll',
        'synchronization.dll', 'ucrtbase.dll', 'user32.dll', 'userenv.dll',
        'uxtheme.dll', 'version.dll', 'winhttp.dll', 'winmm.dll', 'winspool.drv',
        'ws2_32.dll', 'wtsapi32.dll',
    }
    while pending:
        for raw in imports(pending.pop()):
            name = raw.decode('ascii')
            if not re.fullmatch(r'[A-Za-z0-9._-]+\.dll', name, re.I):
                raise ValueError('Invalid import name')
            if name.lower().startswith(('api-ms-win-', 'ext-ms-win-')) or name.lower() in windows_dlls:
                continue
            if name.lower() in known:
                continue
            source = root / 'dist/qemu/fence' / name
            if not source.is_file() or source.is_symlink():
                raise ValueError('Unresolved DLL dependency: ' + name)
            target = stage / name
            shutil.copyfile(source, target)
            pending.append(target)
            known.add(name.lower())
    exe = stage / 'qemu-img.exe'
    env = dict(os.environ)
    env['PATH'] = str(stage) + os.pathsep + str(pathlib.Path(os.environ['SystemRoot']) / 'System32')
    version = subprocess.check_output([str(exe), '--version'], cwd=stage, env=env, text=True)
    if 'version 11.1.50' not in version:
        raise ValueError('Unexpected utility version: ' + version)
    image = stage / 'create-check.qcow2'
    if image.exists():
        raise ValueError('Proof image already exists; preserve it and review before retrying')
    create = subprocess.check_output([str(exe), 'create', '-f', 'qcow2', str(image), '16M'], cwd=stage, env=env, text=True)
    check = subprocess.check_output([str(exe), 'check', '-f', 'qcow2', str(image)], cwd=stage, env=env, text=True)
    info = json.loads(subprocess.check_output([str(exe), 'info', '--output=json', str(image)], cwd=stage, env=env, text=True))
    if info['format'] != 'qcow2' or info['virtual-size'] != 16 * 1024 * 1024 or info.get('backing-filename'):
        raise ValueError('Created image metadata is wrong')
    overlay = stage / 'create-check-overlay.qcow2'
    if overlay.exists():
        raise ValueError('Overlay proof already exists; preserve it and review before retrying')
    subprocess.check_output([str(exe), 'create', '-f', 'qcow2', '-F', 'qcow2', '-b', str(image), str(overlay)], cwd=stage, env=env, text=True)
    overlay_check = subprocess.check_output([str(exe), 'check', str(overlay)], cwd=stage, env=env, text=True)
    chain = json.loads(subprocess.check_output([str(exe), 'info', '--output=json', '--backing-chain', str(overlay)], cwd=stage, env=env, text=True))
    if len(chain) != 2 or any(r['format'] != 'qcow2' or r['virtual-size'] != 16 * 1024 * 1024 for r in chain):
        raise ValueError('Created overlay backing chain is wrong')
    files = sorted((p for p in stage.iterdir() if p.name.lower() in known), key=lambda p: p.name)
    report = {'sourceCommit': json.loads((inputs / 'build-provenance.json').read_text())['qemuCommit'],
              'sha256': sha(candidate), 'versionOutput': version, 'createAndCheckPassed': True,
              'createOutput': create, 'checkOutput': check, 'imageInfo': {k:v for k,v in info.items() if k != 'filename'},
              'overlayCreateAndCheckPassed': True, 'overlayCheckOutput': overlay_check,
              'dlls': [{'file': p.name, 'sha256': sha(p)} for p in files if p.suffix == '.dll']}
    # No real/session image is opened in this proof. Activate only after success.
    dist_qemu = (root / 'dist/qemu').resolve()
    if not dist_qemu.is_relative_to(root) or (root / 'dist/qemu').is_symlink():
        raise ValueError('Unsafe distribution directory')
    removed = []
    for old in dist_qemu.iterdir():
        if old.is_file() and old.suffix.lower() == '.dll' and old.name.lower() not in known:
            if old.is_symlink() or old.resolve().parent != dist_qemu:
                raise ValueError('Unsafe old DLL path')
            removed.append({'file': old.name, 'sha256': sha(old)})
    old_utility_hash = sha(dist_qemu / 'qemu-img.exe')
    for source in files:
        target = dist_qemu / source.name
        if target.is_symlink():
            raise ValueError('Unsafe utility target')
        temporary = target.with_name(target.name + '.pending')
        shutil.copyfile(source, temporary)
        os.replace(temporary, target)
    for old in removed:
        (dist_qemu / old['file']).unlink()
    report['removedVendorDlls'] = removed
    report['replacedVendorUtilitySha256'] = old_utility_hash
    (inputs / 'qemu-img-provenance.json').write_text(json.dumps(report, indent=2) + '\n')
    (work / 'proof.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'result': 'PASS', 'utilitySha256': report['sha256'],
                      'dlls': [p['file'] for p in report['dlls']], 'removedVendorDlls': len(removed)}))


if __name__ == '__main__':
    main()
