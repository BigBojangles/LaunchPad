#!/usr/bin/env python3
"""Root-owned metadata-only VM portability aid. Never copies HOME or file contents."""
import argparse
import json
import os
import pathlib
import re
import stat
import time

ROOTS = {
    'provider-state': ('/home/builder/.grok', '/home/builder/.codex', '/home/builder/.claude'),
    'tools-cache-config': ('/home/builder/.local', '/home/builder/.cache', '/home/builder/.config',
                           '/home/builder/.npm', '/home/builder/.cargo', '/home/builder/.rustup', '/home/builder/go'),
    'temporary': ('/tmp', '/var/tmp'),
}
MAX_ENTRIES = 20000
MAX_SECONDS = 10
MAX_BASELINE_BYTES = 4 * 1024 * 1024
STATE = pathlib.Path('/var/lib/launchpad/portability')


def inventory():
    started = time.monotonic()
    rows = {}
    skipped = 0
    incomplete = False
    def visit(directory, path, category, depth=0):
        nonlocal skipped, incomplete
        if depth >= 48:
            skipped += 1
            incomplete = True
            return
        with os.scandir(directory) as children:
            for child in children:
                if len(rows) >= MAX_ENTRIES or time.monotonic() - started >= MAX_SECONDS:
                    incomplete = True
                    return
                full = path + '/' + child.name
                try:
                    info = os.stat(child.name, dir_fd=directory, follow_symlinks=False)
                    if stat.S_ISLNK(info.st_mode):
                        skipped += 1
                    elif stat.S_ISDIR(info.st_mode):
                        fd = os.open(child.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                     dir_fd=directory)
                        try:
                            visit(fd, full, category, depth + 1)
                        finally:
                            os.close(fd)
                    elif stat.S_ISREG(info.st_mode):
                        rows[full] = [category, info.st_size, info.st_mtime_ns, info.st_ino, info.st_nlink]
                    else:
                        skipped += 1
                except FileNotFoundError:
                    incomplete = True
                except OSError:
                    incomplete = True

    for category, roots in ROOTS.items():
        for root in roots:
            fd = None
            try:
                # Pin every ancestor, not just the leaf, so writable-home/tmp
                # directory replacement cannot redirect a privileged traversal.
                fd = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
                for component in pathlib.PurePosixPath(root).parts[1:]:
                    next_fd = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                      dir_fd=fd)
                    os.close(fd)
                    fd = next_fd
                visit(fd, root, category)
            except FileNotFoundError:
                pass
            except OSError:
                incomplete = True
            finally:
                if fd is not None:
                    os.close(fd)
    return {'schema': 1, 'files': rows, 'incomplete': incomplete, 'skipped': skipped}


def compare(before, after):
    totals = {category: {'added': 0, 'changed': 0, 'removed': 0, 'bytes': 0} for category in ROOTS}
    for path, metadata in after['files'].items():
        previous = before['files'].get(path)
        if previous != metadata:
            counts = totals[metadata[0]]
            counts['added' if previous is None else 'changed'] += 1
            counts['bytes'] += metadata[1]
    for path, metadata in before['files'].items():
        if path not in after['files']:
            totals[metadata[0]]['removed'] += 1
    return {'schema': 1, 'categories': totals,
            'incomplete': before['incomplete'] or after['incomplete'],
            'skipped': before['skipped'] + after['skipped'],
            'filesCopied': 0, 'contentsRead': False, 'confinementProof': False}


def sync_state_directory():
    fd = os.open(STATE, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=('before', 'after'))
    parser.add_argument('generation')
    parser.add_argument('project')
    args = parser.parse_args()
    mode = args.mode
    if not re.fullmatch('[0-9a-f]{32}', args.generation) or not re.fullmatch('[0-9a-f]{16}', args.project):
        raise SystemExit('Bounded parent-owned launch/project identities required.')
    if os.getuid() != 0 or os.geteuid() != 0:
        raise SystemExit('Portability snapshots require the trusted guest parent.')
    for parent in (STATE.parent, STATE):
        parent.mkdir(mode=0o700, exist_ok=True)
        info = parent.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077:
            raise SystemExit('Portability state is not a private owned directory.')
    identity = args.project + '-' + args.generation
    baseline = STATE / (identity + '.before.json')
    current = inventory()
    current.update(generation=args.generation, project=args.project)
    if mode == 'before':
        body = (json.dumps(current, separators=(',', ':')) + '\n').encode()
        if len(body) > MAX_BASELINE_BYTES:
            raise SystemExit('Portability baseline exceeded its bound; no complete report available.')
        fd = os.open(baseline, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as output:
            output.write(body)
            output.flush()
            os.fsync(output.fileno())
        sync_state_directory()
    else:
        fd = os.open(baseline, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        with os.fdopen(fd, 'rb') as source:
            info = os.fstat(source.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or info.st_size > MAX_BASELINE_BYTES:
                raise SystemExit('Portability baseline is not a bounded owned file.')
            before = json.load(source)
        if before.get('schema') != 1 or not isinstance(before.get('files'), dict) \
                or before.get('generation') != args.generation or before.get('project') != args.project:
            raise SystemExit('Invalid portability baseline.')
        # Only aggregate categories leave this tool. Private filenames/secret
        # values never enter the summary or any phone notification.
        report = compare(before, current)
        report.update(generation=args.generation, project=args.project)
        body = (json.dumps(report, separators=(',', ':')) + '\n').encode()
        fd = os.open(STATE / (identity + '.report.json'), os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as output:
            output.write(body)
            output.flush()
            os.fsync(output.fileno())
        # Publish the report's directory entry before removing its source.
        # A failed fsync preserves the baseline for recovery.
        sync_state_directory()
        if not report['incomplete'] and report['skipped'] == 0:
            baseline.unlink()
            sync_state_directory()
        print(body.decode().strip())


if __name__ == '__main__':
    main()
