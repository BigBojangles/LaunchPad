"""Assemble reviewed source inputs and full Windows license inventory.

Inputs are collected separately from the named build tree and upstream package
archives. Missing provenance is recorded and rejected, never filled by guesses.
"""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import sys
import zipfile
from image_audit_lib import open_filesystem, resolve


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inputs', type=pathlib.Path, required=True)
    parser.add_argument('--version', default='1.0.2')
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    dist = root / 'dist'
    inputs = args.inputs.resolve()
    if not inputs.is_relative_to(root):
        raise ValueError('Reviewed source inputs must be inside the workspace')
    manifest = json.loads((dist / 'licenses/manifest-native.json').read_text())
    manifest['profile'] = 'Full'
    files = {row['path']: row for row in manifest['files'] if row['path'] != 'THIRD-PARTY-NOTICES.txt'}
    rows = manifest['components']
    sources = []
    zip_files = {}

    def retain(source, target):
        if source.is_symlink() or not source.is_file() or not source.stat().st_size:
            raise ValueError('Missing/linked source or license: ' + source.name)
        dest = dist / target
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, dest)
        files[target] = {'path': target, 'sha256': sha(dest)}
        return target

    def binaries(paths):
        return [{'path': p, 'sha256': sha(dist / p)} for p in paths]

    def license_files(key):
        directory = inputs / 'dll-licenses' / key
        found = []
        for source in sorted(directory.iterdir()):
            if source.is_file():
                name = source.name
                # Preserve full texts verbatim; normalize only the file name.
                if not re.match(r'(?i)^(?:copying|licen[cs]e)', name):
                    name = 'LICENSE-' + name
                found.append(retain(source, 'licenses/qemu/' + key + '/' + name))
        if not found:
            raise ValueError('No full license texts: ' + key)
        return found

    def archive_files(names):
        result = []
        for name in names:
            p = inputs / name
            if not p.is_file() or not p.stat().st_size:
                raise ValueError('Missing source/build input: ' + name)
            target = 'sources/' + name
            zip_files[target] = p
            result.append({'path': target, 'sha256': sha(p)})
        return result

    archive_name = 'LaunchPad-' + args.version + '-sources.zip'
    release_source = 'https://github.com/BigBojangles/LaunchPad/releases/download/v' + args.version + '/' + archive_name
    build = json.loads((inputs / 'build-provenance.json').read_text())
    fence_path = 'qemu/fence/qemu-system-x86_64.exe'
    if build['dirty'] or build['builtExeSha256'] != sha(dist / fence_path):
        raise ValueError('Fence source/build correspondence is not confirmed')
    qemu_licenses = [retain(inputs / 'qemu-COPYING', 'licenses/qemu/qemu-fence/COPYING')]
    retain(inputs / 'qemu-COPYING', 'qemu/fence/COPYING')
    rows.append({'id': 'qemu-fence', 'kind': 'qemu', 'version': '11.1.50+console-size',
                 'license': 'GPL-2.0-or-later', 'source': release_source,
                 'files': qemu_licenses, 'binaries': binaries([fence_path])})
    qemu_inputs = ['qemu-patched-tree.tar.gz', 'series.mbox', 'mingw-cross.txt', 'build-provenance.json',
                   'mingw-dependency-inputs.tar.gz', 'libslirp-tree.tar.gz', 'source-recipe-proof.json']
    qemu_inputs += [p.relative_to(inputs).as_posix() for p in (inputs / 'build').rglob('*') if p.is_file()]
    sources.append({'id': 'qemu-fence', 'version': '11.1.50+console-size', 'correspondenceConfirmed': True,
                    'commit': build['qemuCommit'], 'baseCommit': build['baseCommit'],
                    'files': archive_files(qemu_inputs), 'binaries': binaries([fence_path])})
    utility = json.loads((inputs / 'qemu-img-provenance.json').read_text())
    if utility['sourceCommit'] != build['qemuCommit'] or utility['sha256'] != sha(dist / 'qemu/qemu-img.exe') or not utility['createAndCheckPassed']:
        raise ValueError('New qemu-img has not passed the disk creation/check gate')
    current_utility_dlls = {p.name: sha(p) for p in (dist / 'qemu').glob('*.dll')}
    if current_utility_dlls != {r['file']: r['sha256'] for r in utility['dlls']}:
        raise ValueError('Packaged qemu-img DLL closure differs from its successful proof')
    rows.append({'id': 'qemu-img', 'kind': 'qemu', 'version': '11.1.50+console-size',
                 'license': 'GPL-2.0-or-later', 'source': release_source,
                 'files': qemu_licenses, 'binaries': binaries(['qemu/qemu-img.exe'])})
    sources.append({'id': 'qemu-img', 'version': '11.1.50+console-size', 'correspondenceConfirmed': True,
                    'commit': build['qemuCommit'], 'files': archive_files(['qemu-patched-tree.tar.gz',
                    'build/qemu-img-build.sh', 'qemu-img-provenance.json']), 'binaries': binaries(['qemu/qemu-img.exe'])})
    covered = {fence_path, 'qemu/qemu-img.exe'}
    for comp, dll, key, version, license_id, archive in [
        ('glib', 'libglib-2.0-0.dll', 'glib2-2.90.0', '2.90.0', 'LGPL-2.1-or-later', 'mingw-w64-glib2-2.90.0-1.src.tar.zst'),
        ('libiconv', 'libiconv-2.dll', 'libiconv-1.19', '1.19', 'LGPL-2.1-or-later', 'mingw-w64-libiconv-1.19-1.src.tar.zst'),
        ('libintl', 'libintl-8.dll', 'gettext-1.0', '1.0', 'LGPL-2.1-or-later', 'mingw-w64-gettext-1.0-1.src.tar.zst')]:
        match = next(r for r in build['dlls'] if r['name'] == dll)
        if match['buildSha256'] != match['shippedSha256'] or sha(dist / ('qemu/fence/' + dll)) != match['shippedSha256']:
            raise ValueError('Unmatched Fence DLL: ' + dll)
        paths = ['qemu/fence/' + dll]
        if (dist / ('qemu/' + dll)).is_file() and sha(dist / ('qemu/' + dll)) == match['shippedSha256']:
            paths.append('qemu/' + dll)
        text = license_files(key)
        rows.append({'id': comp, 'kind': 'qemu-library', 'version': version, 'license': license_id,
                     'source': release_source, 'files': text, 'binaries': binaries(paths)})
        names = [archive, 'recipes/' + key + '/PKGBUILD']
        sources.append({'id': comp, 'version': version, 'correspondenceConfirmed': True,
                        'files': archive_files(names), 'binaries': binaries(paths)})
        covered.update(paths)
    for comp, dlls, key, version, license_id in [
        ('pcre2-fence', ['libpcre2-8-0.dll'], 'fence-pcre2', '10.49', 'BSD-3-Clause'),
        ('pixman-fence', ['libpixman-1-0.dll'], 'fence-pixman', '0.46.4', 'MIT'),
        ('zlib-fence', ['zlib1.dll'], 'fence-zlib', '1.3.2', 'Zlib'),
        ('slirp-fence', ['libslirp-0.dll'], 'libslirp', '4.9.5', 'BSD-3-Clause'),
        ('gcc-fence', ['libgcc_s_seh-1.dll', 'libssp-0.dll'], 'gcc-runtime', '13.2.0', 'GPL-3.0-or-later WITH GCC-exception-3.1'),
        ('winpthreads-fence', ['libwinpthread-1.dll'], 'winpthreads', '13.0.0', 'MIT AND BSD-3-Clause')]:
        paths = ['qemu/fence/' + dll for dll in dlls]
        paths += ['qemu/' + dll for dll in dlls if (dist / ('qemu/' + dll)).is_file()]
        rows.append({'id': comp, 'kind': 'qemu-library', 'version': version, 'license': license_id,
                     'source': release_source if comp == 'slirp-fence' else 'https://packages.debian.org/' if comp in ('gcc-fence','winpthreads-fence') else 'https://packages.msys2.org/',
                     'files': license_files(key), 'binaries': binaries(paths)})
        covered.update(paths)

    sys.path.insert(0, str(root / 'src/LaunchPad/obj/package/image-audit-tools'))
    runtime = json.loads((dist / 'images/runtime.json').read_text(encoding='utf-8-sig'))
    fs = open_filesystem(dist / 'images' / runtime['image']['file'])
    installed = []
    status = fs.get('/var/lib/dpkg/status').open().read().decode()
    for entry in status.split('\n\n'):
        fields = dict(re.findall(r'(?m)^([A-Za-z-]+): (.*)$', entry))
        if fields.get('Status') == 'install ok installed':
            source = fields.get('Source', fields['Package'])
            source_name = source.split()[0]
            source_version = source.split('(', 1)[1].rstrip(')') if '(' in source else fields['Version']
            installed.append({'package': fields['Package'], 'version': fields['Version'], 'architecture': fields['Architecture'],
                              'sourcePackage': source_name, 'sourceVersion': source_version,
                              'source': 'https://sources.debian.org/src/' + source_name + '/' + source_version + '/'})
    inventory = {'schema': 1, 'imageSha256': runtime['image']['sha256'], 'packages': sorted(installed, key=lambda x: x['package'])}
    inventory_path = dist / 'licenses/debian-packages.json'
    inventory_path.write_text(json.dumps(inventory, indent=2) + '\n')
    files['licenses/debian-packages.json'] = {'path': 'licenses/debian-packages.json', 'sha256': sha(inventory_path)}
    manifest['debianInventory'] = dict(files['licenses/debian-packages.json'], imageSha256=runtime['image']['sha256'])
    common = []
    for p in (inputs / 'common-licenses').iterdir():
        common.append(retain(p, 'licenses/debian/COPYING-' + p.name))
    rows.append({'id': 'debian', 'kind': 'debian', 'version': fs.get('/etc/debian_version').open().read().decode().strip(),
                 'license': 'Per-package licenses; see installed copyright texts', 'source': 'https://sources.debian.org/', 'files': common})
    kernel_copyright = dist / 'licenses/kernel/LICENSE-debian-linux'
    kernel_copyright.parent.mkdir(parents=True, exist_ok=True)
    kernel_copyright.write_bytes(resolve(fs, '/usr/share/doc/linux-image-6.1.0-53-amd64/copyright').open().read())
    files['licenses/kernel/LICENSE-debian-linux'] = {'path': 'licenses/kernel/LICENSE-debian-linux', 'sha256': sha(kernel_copyright)}
    kernel_gpl = retain(inputs / 'common-licenses/GPL-2', 'licenses/kernel/COPYING')
    rows.append({'id': 'linux-kernel', 'kind': 'kernel', 'version': '6.1.0-53-amd64', 'license': 'GPL-2.0-only',
                 'source': 'https://sources.debian.org/src/linux/', 'files': [kernel_gpl, 'licenses/kernel/LICENSE-debian-linux']})
    note = ('LaunchPad Fence QEMU: 11.1.50 plus the public console-size patch series.\n'
            'Built with MinGW on October 4, 2026.\nSource commit: ' + build['qemuCommit'] + '\n'
            'Exact patched tree, series.mbox, build settings, mingw-cross.txt and corresponding DLL sources:\n' + release_source + '\n'
            'The sources ZIP must accompany the installer on the same release.\n')
    (dist / 'qemu/fence/QEMU-SOURCE.txt').write_text(note)
    files['qemu/fence/QEMU-SOURCE.txt'] = {'path': 'qemu/fence/QEMU-SOURCE.txt', 'sha256': sha(dist / 'qemu/fence/QEMU-SOURCE.txt')}
    sources_doc = {'schema': 1, 'components': sources,
                   'buildNotes': 'Original QEMU tree and mbox are verbatim. Only inherited Windows PATH in build metadata was reduced to /usr/bin:/bin. No developer Git config or build output is included.'}
    with zipfile.ZipFile(dist / archive_name, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for target, source in sorted(zip_files.items()):
            z.write(source, target)
        z.writestr('SOURCE-MANIFEST.json', json.dumps(sources_doc, indent=2) + '\n')
        z.writestr('README-SOURCES.txt', note + '\nThe source packages include upstream tarballs, MSYS2 PKGBUILD recipes and distribution patches.\n')
    manifest['sourceArchive'] = {'path': archive_name, 'sha256': sha(dist / archive_name)}
    unresolved = [p.relative_to(dist).as_posix() for p in (dist / 'qemu').rglob('*')
                  if p.is_file() and p.suffix.lower() in ('.dll', '.exe') and p.relative_to(dist).as_posix() not in covered]
    lines = ['LaunchPad ' + args.version + ' - Full Windows third-party notices', '',
             'Full license texts: licenses/. Corresponding source archive on this same release:', release_source, '',
             'Guest Debian package/source versions: licenses/debian-packages.json.',
             'Debian per-package copyright files are also retained inside the VM under /usr/share/doc/.', '']
    for row in rows:
        lines += [row['id'] + ' ' + row['version'], 'License: ' + row['license'], 'Source: ' + row['source'],
                  'Full text: ' + ', '.join(row['files']), '']
    if unresolved:
        lines += ['INCOMPLETE - DO NOT DISTRIBUTE', 'Unresolved runtime binary inventory:', *unresolved, '']
    notices = dist / 'THIRD-PARTY-NOTICES.txt'
    notices.write_text('\n'.join(lines), encoding='utf-8')
    files['THIRD-PARTY-NOTICES.txt'] = {'path': 'THIRD-PARTY-NOTICES.txt', 'sha256': sha(notices)}
    manifest['files'] = list(files.values())
    manifest['unresolvedBinaries'] = unresolved
    (dist / 'licenses/manifest-full.json').write_text(json.dumps(manifest, indent=2) + '\n')
    # Native license inventory must bind the same shared notice text.
    native_path = dist / 'licenses/manifest-native.json'
    native = json.loads(native_path.read_text())
    native['files'] = [r for r in native['files'] if r['path'] != 'THIRD-PARTY-NOTICES.txt'] + [files['THIRD-PARTY-NOTICES.txt']]
    native_path.write_text(json.dumps(native, indent=2) + '\n')
    print(json.dumps({'components': len(rows), 'debianPackages': len(installed), 'sourceArchive': archive_name,
                      'unresolvedBinaries': unresolved}))
    if unresolved:
        raise SystemExit('Full inventory is incomplete; installer gate must reject these binaries.')


if __name__ == '__main__':
    main()
