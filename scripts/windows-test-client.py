#!/usr/bin/env python3
"""Pack a Linux project snapshot / unpack a bound Windows test result.

Binary protocol on stdout/stdin; diagnostics go to stderr. The dedicated guest
broker/host owner supplies transport separately. This never opens host paths,
uses host credentials, sends model prompts, or applies files to the project.
"""
import argparse
import hashlib
import json
import os
import pathlib
import stat
import socket
import struct
import sys
import uuid

MAX_META = 4 * 1024 * 1024
MAX_SNAPSHOT = 2 * 1024 * 1024 * 1024
MAX_RESULT = 32 * 1024 * 1024
END = b'LP-WINDOWS-FILES-END\n'


def identifier(value):
    if len(value) != 32 or uuid.UUID(hex=value).hex != value.lower():
        raise ValueError('Expected a 32-character session/request ID.')
    return value


def safe_path(value):
    if not isinstance(value, str) or not 0 < len(value) <= 240:
        return False
    if any(ord(c) < 32 or c in '\\:<>"|?*' for c in value):
        return False
    parts = value.split('/')
    for part in parts:
        if part in ('', '.', '..') or part.endswith(('.', ' ')):
            return False
        stem = part.split('.')[0].upper()
        if stem in ('CON', 'PRN', 'AUX', 'NUL') or len(stem) == 4 and stem[:3] in ('COM', 'LPT') and stem[3] in '123456789':
            return False
    return parts[0].lower() != '.launchpad-test'


def unlinked(path):
    current = pathlib.Path(os.path.abspath(path))
    for item in (current, *current.parents):
        try:
            if stat.S_ISLNK(item.lstat().st_mode):
                raise ValueError('Linked snapshot/result path.')
        except FileNotFoundError:
            pass
    return current


def opened(path):
    # Pin ancestors with O_NOFOLLOW directory descriptors on the Linux guest.
    # Opening only the final component would miss a swapped parent symlink.
    # O_PATH pins/searches directories without requesting directory read access;
    # O_RDONLY here unnecessarily required the agent to read '/' and ancestors.
    # Keep actual file reads subject to ordinary DAC/AppArmor permissions.
    absolute = pathlib.Path(os.path.abspath(path))
    if any(not hasattr(os, name) for name in ('O_PATH', 'O_DIRECTORY', 'O_NOFOLLOW', 'O_NONBLOCK')):
        raise ValueError('Safe snapshot opening requires Linux directory-reference support.')
    directory_flags = os.O_PATH | os.O_DIRECTORY | os.O_NOFOLLOW
    descriptor = os.open(absolute.anchor, directory_flags)
    try:
        if not stat.S_ISDIR(os.fstat(descriptor).st_mode):
            raise ValueError('Snapshot ancestor is not a directory.')
        for part in absolute.parts[1:-1]:
            child = os.open(part, directory_flags, dir_fd=descriptor)
            try:
                if not stat.S_ISDIR(os.fstat(child).st_mode):
                    raise ValueError('Snapshot ancestor is not a directory.')
            except BaseException:
                os.close(child)
                raise
            os.close(descriptor)
            descriptor = child
        # Avoid a FIFO-open wait; reject nonregular targets before reading.
        file_fd = os.open(absolute.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=descriptor)
        try:
            info = os.fstat(file_fd)
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                raise ValueError('Only ordinary single-link snapshot files are supported.')
            stream = os.fdopen(file_fd, 'rb')
            file_fd = None
            return stream
        finally:
            if file_fd is not None:
                os.close(file_fd)
    finally:
        os.close(descriptor)


def snapshot(root):
    root = unlinked(root)
    if not root.is_dir() or root == pathlib.Path(root.anchor):
        raise ValueError('Choose a project directory, not a filesystem root.')
    files, total, entries = [], 0, 0
    for directory, folders, names in os.walk(root, followlinks=False):
        folders.sort()
        for name in sorted(folders + names):
            entries += 1
            if entries > 40000:
                raise ValueError('Snapshot has too many entries.')
            path = pathlib.Path(directory) / name
            relative = path.relative_to(root).as_posix()
            if not safe_path(relative) or stat.S_ISLNK(path.lstat().st_mode):
                raise ValueError('Unsafe/linked snapshot entry: ' + relative)
        for name in sorted(names):
            path = pathlib.Path(directory) / name
            with opened(path) as stream:
                size = os.fstat(stream.fileno()).st_size
                total += size
                if len(files) >= 20000 or total > MAX_SNAPSHOT:
                    raise ValueError('Snapshot exceeds 20,000 files or 2 GiB.')
                digest = hashlib.sha256()
                while chunk := stream.read(65536):
                    digest.update(chunk)
                files.append(dict(path=path.relative_to(root).as_posix(), size=size, sha256=digest.hexdigest()))
    validate_files(files, 20000, MAX_SNAPSHOT)
    return root, files


def validate_files(files, count_limit, byte_limit):
    if not isinstance(files, list) or len(files) > count_limit:
        raise ValueError('Invalid file list.')
    seen, total = set(), 0
    for file in files:
        if not isinstance(file, dict) or set(file) != {'path', 'size', 'sha256'}:
            raise ValueError('Invalid file record.')
        name, size, digest = file['path'], file['size'], file['sha256']
        if not safe_path(name) or type(size) is not int or size < 0 or not isinstance(digest, str) or len(digest) != 64:
            raise ValueError('Invalid file identity.')
        if any(c not in '0123456789abcdefABCDEF' for c in digest) or name.casefold() in seen:
            raise ValueError('Invalid hash or duplicate file.')
        total += size
        if total > byte_limit:
            raise ValueError('File data exceeds its limit.')
        seen.add(name.casefold())
    for name in seen:
        parts = name.split('/')
        if any('/'.join(parts[:index]) in seen for index in range(1, len(parts))):
            raise ValueError('File/directory collision.')


def pack(args, output=None):
    generation = identifier(args.generation)
    request_id = identifier(args.request_id) if args.request_id else uuid.uuid4().hex
    arguments = args.arguments[1:] if args.arguments[:1] == ['--'] else args.arguments
    if len(arguments) > 128 or sum(map(len, arguments)) > 32768 or any(len(arg) > 8192 or '\0' in arg for arg in arguments):
        raise ValueError('Too many/oversized command arguments.')
    if args.cwd != '.' and not safe_path(args.cwd):
        raise ValueError('Unsafe working directory.')
    if not 1 <= args.timeout <= 7200 or len(args.artifact) > 128 or len(set(p.casefold() for p in args.artifact)) != len(args.artifact):
        raise ValueError('Invalid duration/artifact list.')
    if any(not safe_path(path) or len(path) > 220 for path in args.artifact):
        raise ValueError('Unsafe artifact path.')
    root, files = snapshot(args.project)
    if args.tool == 'project':
        if not safe_path(args.program) or not args.program.lower().endswith('.exe') or args.program not in {f['path'] for f in files}:
            raise ValueError('Choose a snapshot Windows .exe.')
    elif args.program is not None:
        raise ValueError('--program is only for the project tool.')
    request = dict(version=1, generation=generation, requestId=request_id, tool=args.tool, program=args.program,
                   workingDirectory=args.cwd, arguments=arguments, interactive=args.interactive, timeoutSeconds=args.timeout,
                   artifacts=args.artifact, files=files)
    metadata = json.dumps(request, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    if len(metadata) > MAX_META:
        raise ValueError('Snapshot metadata is too large.')
    print('Windows test request ' + request_id, file=sys.stderr)
    output = sys.stdout.buffer if output is None else output
    output.write(('LP-WINDOWS-TEST 1 %d\n' % len(metadata)).encode('ascii'))
    output.write(metadata)
    for file in files:
        digest, left = hashlib.sha256(), file['size']
        with opened(root / file['path']) as stream:
            if os.fstat(stream.fileno()).st_size != left:
                raise ValueError('Snapshot changed before transfer.')
            while left:
                chunk = stream.read(min(65536, left))
                if not chunk:
                    raise ValueError('Snapshot changed during transfer.')
                output.write(chunk)
                digest.update(chunk)
                left -= len(chunk)
        if digest.hexdigest().lower() != file['sha256'].lower():
            raise ValueError('Snapshot changed during transfer.')
    output.write(END)
    output.flush()
    return request


def exact(stream, count):
    data = bytearray()
    while len(data) < count:
        chunk = stream.read(count - len(data))
        if not chunk:
            raise ValueError('Truncated Windows result.')
        data.extend(chunk)
    return bytes(data)


def unpack(args, source=None):
    generation, request_id = identifier(args.generation), identifier(args.request_id)
    source = sys.stdin.buffer if source is None else source
    line = source.readline(129)
    prefix = b'LP-WINDOWS-RESULT 1 '
    if not line.startswith(prefix) or not line.endswith(b'\n') or not line[len(prefix):-1].isdigit():
        raise ValueError('Invalid Windows result frame.')
    size = int(line[len(prefix):-1])
    if not 2 <= size <= MAX_META:
        raise ValueError('Windows result metadata exceeds its limit.')
    result = json.loads(exact(source, size))
    if not isinstance(result, dict) or result.get('version') != 1 or result.get('generation') != generation or result.get('requestId') != request_id:
        raise ValueError('Wrong Windows result identity.')
    if result.get('outcome') not in ('finished', 'failed', 'canceled', 'timed-out', 'interrupted') or result['outcome'] == 'finished' and type(result.get('exitCode')) is not int:
        raise ValueError('Invalid Windows result outcome.')
    validate_files(result.get('files'), 130, MAX_RESULT)
    destination = unlinked(args.output)
    # Never write into an existing project/result or silently overwrite files.
    destination.mkdir(parents=True, exist_ok=False)
    for file in result['files']:
        path = destination / file['path']
        path.parent.mkdir(parents=True, exist_ok=True)
        unlinked(path)
        digest, left = hashlib.sha256(), file['size']
        with path.open('xb') as output:
            while left:
                chunk = exact(source, min(65536, left))
                output.write(chunk)
                digest.update(chunk)
                left -= len(chunk)
            output.flush()
            os.fsync(output.fileno())
        if digest.hexdigest().lower() != file['sha256'].lower():
            raise ValueError('Windows result hash mismatch; partial data retained.')
    if exact(source, len(END)) != END:
        raise ValueError('Windows result completion marker is missing.')
    with (destination / 'result.json').open('x', encoding='utf-8') as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.flush()
        os.fsync(output.fileno())
    accepted = dict(requestId=request_id, outcome=result['outcome'], exitCode=result.get('exitCode'),
                    error=result.get('error'), resultDirectory=str(destination))
    print(json.dumps(accepted))
    return accepted


def run(args):
    state = unlinked(pathlib.Path.home() / '.local' / 'state' / 'launchpad' / 'windows-tests')
    state.mkdir(parents=True, exist_ok=True)
    bundle = None
    with socket.socket(socket.AF_UNIX) as connection:
        connection.settimeout(75)
        connection.connect(args.socket)
        _, uid, _ = struct.unpack('3i', connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        if uid != 0:
            raise PermissionError('The Windows test endpoint is not the trusted root broker.')
        with connection.makefile('rwb', buffering=0) as channel:
            connection.sendall(b'LP-WINDOWS-OPEN 1\n')
            line = channel.readline(193)
            prefix = b'LP-WINDOWS-SESSION 1 '
            if not line.startswith(prefix) or not line.endswith(b'\n'):
                raise ValueError('Windows test broker did not provide a session.')
            generation = identifier(line[len(prefix):-1].decode('ascii'))
            args.generation = generation
            if args.resume:
                bundle = unlinked(args.resume)
                with opened(bundle) as saved:
                    header = saved.readline(129)
                    prefix = b'LP-WINDOWS-TEST 1 '
                    if not header.startswith(prefix) or not header.endswith(b'\n') or not header[len(prefix):-1].isdigit():
                        raise ValueError('Invalid saved Windows test request.')
                    length = int(header[len(prefix):-1])
                    if not 2 <= length <= MAX_META or os.fstat(saved.fileno()).st_size > MAX_SNAPSHOT + MAX_META + 1024:
                        raise ValueError('Saved Windows test request exceeds its limit.')
                    request = json.loads(exact(saved, length))
                    if not isinstance(request, dict):
                        raise ValueError('Invalid saved Windows test metadata.')
                    if request.get('generation') != generation:
                        raise ValueError('Saved request belongs to another session; it was preserved and was not restarted.')
                    args.request_id = identifier(request.get('requestId', ''))
                    args.timeout = request['timeoutSeconds']
            else:
                if not args.tool:
                    raise ValueError('Choose --tool for a new test, or --resume for an unchanged saved request.')
                project = unlinked(args.project)
                if state == project or state.is_relative_to(project):
                    raise ValueError('Choose a project outside the managed Windows test request/result storage.')
                args.request_id = identifier(args.request_id) if args.request_id else uuid.uuid4().hex
                bundle = state / (args.request_id + '.request')
                with bundle.open('xb') as saved:
                    request = pack(args, saved)
                    saved.flush()
                    os.fsync(saved.fileno())
            print('Saved Windows test request: ' + str(bundle), file=sys.stderr)
            connection.settimeout(30)
            with opened(bundle) as saved:
                while chunk := saved.read(65536):
                    connection.sendall(chunk)
            # Build/test runtime is bounded independently from transfer idleness.
            connection.settimeout(min(7290, int(args.timeout) + 90))
            args.output = args.output or str(state / (args.request_id + '-result-' + uuid.uuid4().hex))
            accepted = unpack(args, channel)
            return 0 if accepted['outcome'] == 'finished' and accepted['exitCode'] == 0 else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='mode', required=True)
    def add_request_options(target, direct):
        target.add_argument('--project', required=direct, default='.')
        target.add_argument('--generation', required=direct)
        target.add_argument('--request-id')
        target.add_argument('--tool', choices=('project', 'dotnet', 'node', 'python', 'powershell'), required=direct)
        target.add_argument('--program')
        target.add_argument('--cwd', default='.')
        target.add_argument('--timeout', type=int, default=600)
        target.add_argument('--interactive', action='store_true')
        target.add_argument('--artifact', action='append', default=[])
        target.add_argument('arguments', nargs=argparse.REMAINDER)
    add_request_options(commands.add_parser('pack'), True)
    run_parser = commands.add_parser('run')
    run_parser.add_argument('--socket', default='/run/launchpad-windows-test/bridge.sock')
    run_parser.add_argument('--resume')
    run_parser.add_argument('--output')
    add_request_options(run_parser, False)
    unpack_parser = commands.add_parser('unpack')
    unpack_parser.add_argument('--generation', required=True)
    unpack_parser.add_argument('--request-id', required=True)
    unpack_parser.add_argument('--output', required=True)
    args = parser.parse_args()
    try:
        if args.mode == 'run':
            return run(args)
        (pack if args.mode == 'pack' else unpack)(args)
    except (OSError, ValueError, KeyError, TypeError) as error:
        print('Windows test transfer failed: ' + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
