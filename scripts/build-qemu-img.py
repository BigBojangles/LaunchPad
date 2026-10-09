"""Build only qemu-img from the authorized WSL Fence sources, inside obj.

Never modifies the original WSL source/build or activates a runtime. Staging
and disk-utility verification happen before any live dist replacement.
"""
import argparse
import hashlib
import json
import pathlib
import shlex
import subprocess
import tarfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inputs', type=pathlib.Path, required=True)
    parser.add_argument('--linux-build-alias')
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    inputs = args.inputs.resolve()
    if not inputs.is_relative_to(root):
        raise ValueError('Inputs must remain inside the workspace')
    work = root / 'src/LaunchPad/obj/package/qemu-img-build'
    work.mkdir(parents=True, exist_ok=True)
    source_archive = inputs / 'qemu-patched-tree.tar.gz'
    # Include every source path; exclude Git administration only.
    with source_archive.open('wb') as out:
        subprocess.run(['wsl.exe', '-d', 'Ubuntu', '-u', 'root', '--', 'tar', '-czf', '-',
                        '--exclude=.git', '-C', '/root/qemu-console-size', 'src'], stdout=out, check=True)
    # Validate archive paths/links, then let Linux tar preserve source links,
    # including the optional, uninitialized ROM links not used by qemu-img.
    with tarfile.open(source_archive) as archive:
        for item in archive.getmembers():
            destination = (work / item.name).resolve()
            if not destination.is_relative_to(work.resolve()) or item.name.startswith('/'):
                raise ValueError('Unsafe source path')
            if item.issym() or item.islnk():
                target = ((destination.parent if item.issym() else work) / item.linkname).resolve()
                if not target.is_relative_to(work.resolve()):
                    raise ValueError('Source link escapes build workspace')
            elif not item.isdir() and not item.isfile():
                raise ValueError('Unexpected source entry type')
    linux = '/mnt/' + work.drive[0].lower() + work.as_posix()[2:]
    if args.linux_build_alias and args.linux_build_alias != '/tmp/launchpad-qemu-img-build':
        raise ValueError('Only the explicitly approved temporary bind-mount path is supported')
    build_linux = args.linux_build_alias or linux
    original = (inputs / 'build/config.status').read_text()
    invocation = next(line for line in original.splitlines() if line.startswith("exec '../src/configure'"))
    configure = shlex.split(invocation)[2:-1]
    configure = ['--enable-tools' if arg == '--disable-tools' else arg for arg in configure]
    command = ' '.join(shlex.quote(arg) for arg in configure)
    portable_script = ('set -eu\n'
              ': "${LAUNCHPAD_BUILD_ROOT:?Set the workspace build directory}"\n'
              ': "${LAUNCHPAD_DEPENDENCY_ROOT:?Set the original or reconstructed MinGW dependency directory}"\n'
              'cd "$LAUNCHPAD_BUILD_ROOT"\n'
              'mkdir -p out tmp home cache\n'
              'export HOME="$PWD/home" TMPDIR="$PWD/tmp" XDG_CACHE_HOME="$PWD/cache"\n'
              'export PYTHONDONTWRITEBYTECODE=1\n'
              'mkdir -p "$LAUNCHPAD_BUILD_ROOT/pkgconfig"\n'
              # Rebase extracted MSYS2 prefixes; preserve the original .pc files.
              'for pc in "$LAUNCHPAD_DEPENDENCY_ROOT"/mingw64/lib/pkgconfig/*.pc; do\n'
              '  tr -d "\\r" < "$pc" | sed "s|^prefix=/mingw64$|prefix=$LAUNCHPAD_DEPENDENCY_ROOT/mingw64|" > "$LAUNCHPAD_BUILD_ROOT/pkgconfig/$(basename "$pc")"\n'
              'done\n'
              'export PKG_CONFIG_LIBDIR="$LAUNCHPAD_BUILD_ROOT/pkgconfig"\n'
              'unset PKG_CONFIG_PATH PKG_CONFIG_SYSROOT_DIR\n'
              'cd out\n'
              '../src/configure ' + command + '\n'
              'ninja -j 4 qemu-img.exe\n')
    (inputs / 'build/qemu-img-build.sh').write_text(portable_script)
    archive_linux = '/mnt/' + source_archive.drive[0].lower() + source_archive.as_posix()[2:]
    prefix = 'set -eu\n'
    if args.linux_build_alias:
        prefix += ('alias_path=' + shlex.quote(build_linux) + '\n'
                   'mkdir "$alias_path"\n'
                   'mounted=0\n'
                   'cleanup() { cd /; if [ "$mounted" = 1 ]; then umount "$alias_path"; fi; rmdir "$alias_path"; }\n'
                   'trap cleanup EXIT\n'
                   'mount --bind ' + shlex.quote(linux) + ' "$alias_path"\n'
                   'mounted=1\n')
    script = (prefix + 'export LAUNCHPAD_BUILD_ROOT=' + shlex.quote(build_linux) + '\n'
              'export LAUNCHPAD_DEPENDENCY_ROOT=/root/qemu-console-size/deps/root\n'
              'tar -xzf ' + shlex.quote(archive_linux) + ' -C "$LAUNCHPAD_BUILD_ROOT"\n' + portable_script)
    command_doc = {'sourceCommit': json.loads((inputs / 'build-provenance.json').read_text())['qemuCommit'],
                   'configureArguments': configure, 'target': 'qemu-img.exe',
                   'compiler': 'x86_64-w64-mingw32-gcc (GCC) 13-win32',
                   'workingDirectory': str(work)}
    (work / 'command.json').write_text(json.dumps(command_doc, indent=2))
    with (work / 'build.log').open('wb') as log:
        result = subprocess.run(['wsl.exe', '-d', 'Ubuntu', '-u', 'root', '--', 'sh', '-s'],
                                input=script.encode(), stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        print((work / 'build.log').read_text(errors='replace')[-5000:])
        raise SystemExit(result.returncode)
    exe = work / 'out/qemu-img.exe'
    print(json.dumps({'result': 'BUILT, NOT ACTIVATED', 'path': str(exe),
                      'sha256': hashlib.sha256(exe.read_bytes()).hexdigest(), 'log': str(work / 'build.log')}))


if __name__ == '__main__':
    main()
