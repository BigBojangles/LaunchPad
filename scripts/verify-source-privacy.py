"""Fail builds on identifying literals in current tracked/publishable files.

Requires Git and Python 3, like the release tooling. This reads working files,
not old commits; ignored credentials, evidence and VM state are never scanned.
Reviewed existing synthetic emails are allowed by exact file/address only.
"""
import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
EXAMPLES = Path(__file__).with_name('source-privacy-examples.json')
PROFILE = re.compile(r'[a-z]:(?:\\+|/)Users(?:\\+|/)|[a-z]%3a(?:%5c|%2f)Users(?:%5c|%2f)', re.I)
EMAIL = re.compile(r'(?<![\w.%+\-])(?:[\w.%+\-]+|"(?:[^"\\\r\n]|\\[^\r\n]){1,64}")@[\w\-]+(?:\.[\w\-]+)+', re.I)
UNICODE_ESCAPE = re.compile(r'\\u([0-9a-f]{4})|\\U([0-9a-f]{8})')

def reviewed_examples():
    policy = json.loads(EXAMPLES.read_text(encoding='utf-8-sig'))
    if policy['schema'] != 1:
        raise ValueError('Unsupported email example policy')
    allowed = {}
    for row in policy['reviewedEmailExamples']:
        path = row['path']
        if Path(path).is_absolute() or '..' in Path(path).parts or path in allowed or not row['reason']:
            raise ValueError('Invalid reviewed email example scope')
        addresses = {address.lower() for address in row['addresses']}
        if not addresses or any(not EMAIL.fullmatch(address) for address in addresses):
            raise ValueError('Invalid reviewed email example address')
        allowed[path] = addresses
    # The policy may spell its own reviewed examples, never arbitrary emails.
    allowed[EXAMPLES.relative_to(REPO).as_posix()] = set().union(*allowed.values())
    return allowed

def reserved_example(address):
    domain = address.rsplit('@', 1)[1].lower()
    return (domain in ('example.com', 'example.net', 'example.org', 'localhost')
            or domain.endswith(('.example', '.invalid', '.test', '.localhost'))
            or domain.endswith(('.example.com', '.example.net', '.example.org')))

def violations(path, data, allowed):
    findings = set()
    for encoding in ('utf-8-sig', 'utf-16-le', 'utf-16-be'):
        original = data.decode(encoding, errors='replace')
        def unescape(match):
            code = int(match[1] or match[2], 16)
            # Preserve source line numbers and invalid code points.
            return chr(code) if code not in (10, 13) and code <= 0x10ffff else match[0]
        text = UNICODE_ESCAPE.sub(unescape, original)
        for match in PROFILE.finditer(text):
            findings.add((path, text.count('\n', 0, match.start()) + 1, 'Windows profile path'))
        for match in EMAIL.finditer(text):
            address = match.group().lower()
            if not reserved_example(address) and address not in allowed.get(path, set()):
                findings.add((path, text.count('\n', 0, match.start()) + 1, 'unreviewed email address'))
    return sorted(findings)

def scan(root):
    root = root.resolve()
    repository = subprocess.run(['git', '-C', str(root), 'rev-parse', '--show-toplevel'],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if repository.returncode or Path(repository.stdout.decode('utf-8').strip()).resolve() != root:
        raise ValueError('Privacy check requires this source folder to be the Git repository root')
    # Include untracked nonignored source awaiting a checkpoint; do not create a
    # hole between adding code and tracking it. Never inspect commit history.
    command = ['git', '-C', str(root), 'ls-files', '--cached', '--others', '--exclude-standard', '--full-name', '-z']
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise ValueError('Git could not enumerate current files; build privacy coverage is unavailable')
    names = sorted(set(name for name in result.stdout.decode('utf-8').split('\0') if name))
    allowed = reviewed_examples(); findings = []; count = 0
    for name in names:
        target = root/name
        if not target.exists() and not target.is_symlink():
            continue  # A working-tree deletion is not part of this build.
        if target.is_symlink() or not target.resolve().is_relative_to(root) or not target.is_file():
            raise ValueError('Linked/non-file source input cannot be privacy-checked: '+name)
        if target.stat().st_size > 32*1024*1024:
            raise ValueError('Source file exceeds privacy scan limit: '+name)
        findings.extend(violations(name, target.read_bytes(), allowed)); count += 1
    return count, findings

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, default=REPO)
    args = parser.parse_args()
    try:
        count, findings = scan(args.repo)
    except (OSError, ValueError, KeyError) as error:
        print('Source privacy check FAILED: '+str(error), file=sys.stderr)
        return 1
    if findings:
        for path, line, reason in findings:
            # Report location and category only, never the identifying value.
            print(f'{path}({line}): error LPPRIVACY: {reason}', file=sys.stderr)
        return 1
    print(f'Source privacy check PASS: {count} current tracked/publishable files')
    return 0

if __name__ == '__main__':
    sys.exit(main())
