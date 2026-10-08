#!/usr/bin/env python3
"""Passive Grok schema capture. No stdout, approvals, network, or device opens."""
import hashlib
import json
import os
import re
import stat
import sys
import time

EVENTS = {'SessionStart', 'SessionEnd', 'UserPromptSubmit', 'PreToolUse', 'PostToolUse',
          'PostToolUseFailure', 'PermissionDenied', 'Stop', 'StopFailure', 'Notification',
          'SubagentStart', 'SubagentStop', 'PreCompact', 'PostCompact'}
FIELDS = {'hookEventName', 'hook_event_name', 'sessionId', 'session_id', 'turnId', 'turn_id',
          'promptId', 'prompt_id', 'notificationType', 'notification_type', 'type', 'message',
          'title', 'cwd', 'workspaceRoot', 'workspace_root', 'toolName', 'toolInput',
          'tool_name', 'tool_input', 'toolUseId', 'requestId', 'agentId', 'subagentId',
          'parentSessionId', 'stopHookActive', 'stop_hook_active', 'backgroundTasks',
          'background_tasks', 'sessionCrons', 'session_crons', 'reason', 'stopReason', 'status',
          'subagentType', 'subagent_type', 'subagent_id', 'parent_session_id'}
NOTIFICATION_TYPES = {'permission_prompt', 'idle_prompt', 'elicitation_dialog', 'auth_success',
                      'info', 'warning', 'task_complete'}
MAX_LOG = 1024 * 1024


def project(value, nonce):
    """Pure projection: retain schema and fixed tokens, never free-text values."""
    if not isinstance(value, dict) or len(value) > 64:
        return None
    event = value.get('hookEventName', value.get('hook_event_name', os.environ.get('GROK_HOOK_EVENT')))
    event = next((name for name in EVENTS if isinstance(event, str)
                  and name.lower() == event.replace('_', '').lower()), 'Unknown')
    def field(camel, snake):
        return value.get(camel, value.get(snake))
    def identifier_hash(item):
        if isinstance(item, str) and re.fullmatch(r'[A-Za-z0-9_.:-]{1,128}', item):
            return hashlib.sha256((nonce + ':' + item).encode()).hexdigest()
        return None
    def count(item):
        return min(len(item), 1000000) if isinstance(item, list) else -1
    notice = field('notificationType', 'notification_type')
    # Some versions may call this "type". Capture its schema; don't guess its meaning.
    stop = field('stopHookActive', 'stop_hook_active')
    return dict(version=2, nonce=nonce, capturedUnixMs=time.time_ns() // 1000000,
                event=event, fields={name: type(item).__name__ for name, item in value.items()
                                     if name in FIELDS},
                sessionHash=identifier_hash(field('sessionId', 'session_id')),
                turnHash=identifier_hash(field('turnId', 'turn_id')),
                promptHash=identifier_hash(field('promptId', 'prompt_id')),
                childSession=any(value.get(name) not in (None, '') for name in
                    ('subagentId', 'subagent_id', 'subagentType', 'subagent_type',
                     'parentSessionId', 'parent_session_id')),
                notificationType=notice if isinstance(notice, str) and notice in NOTIFICATION_TYPES else
                    ('absent' if notice is None else 'other'),
                stopHookActive=stop if isinstance(stop, bool) else None,
                backgroundTasksCount=count(field('backgroundTasks', 'background_tasks')),
                sessionCronsCount=count(field('sessionCrons', 'session_crons')),
                cwdMatchesProject=value.get('cwd') == '/home/builder/in/project')


def config_bytes(nonce, log):
    command = '/usr/bin/python3 -I /usr/local/lib/launchpad/guest-hook-diagnostic.py capture ' + nonce + ' ' + log
    # PreToolUse is intentionally omitted: the diagnostic never installs a blocking hook.
    return (json.dumps(dict(hooks={event: [dict(hooks=[dict(type='command', command=command,
                       timeout=2)])] for event in sorted(EVENTS - {'PreToolUse'})}),
                       sort_keys=True) + '\n').encode()


def hooks_directory():
    home = os.open('/home/builder/.grok', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        try:
            os.mkdir('hooks', 0o700, dir_fd=home)
        except FileExistsError:
            pass
        return os.open('hooks', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=home)
    finally:
        os.close(home)


def main():
    if len(sys.argv) != 4 or not re.fullmatch(r'[0-9a-f]{32}', sys.argv[2]) or not re.fullmatch(
            r'/tmp/launchpad-hooks-[A-Za-z0-9_]+', sys.argv[3]):
        return
    mode, nonce, log = sys.argv[1:]
    if mode in {'setup', 'cleanup'}:
        directory = hooks_directory()
        try:
            name = 'launchpad-diagnostic-' + nonce + '.json'
            expected = config_bytes(nonce, log)
            if mode == 'setup':
                fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                             0o600, dir_fd=directory)
                with os.fdopen(fd, 'wb') as output:
                    output.write(expected)
            else:
                fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
                with os.fdopen(fd, 'rb') as source:
                    if stat.S_ISREG(os.fstat(source.fileno()).st_mode) and source.read(len(expected) + 1) == expected:
                        os.unlink(name, dir_fd=directory)
        finally:
            os.close(directory)
    elif mode == 'capture':
        # A personal hook retained after a crash is inert in later launches.
        if os.environ.get('LP_HOOK_NONCE') != nonce or os.environ.get('LP_HOOK_LOG') != log:
            return
        raw = sys.stdin.buffer.read(65537)
        if len(raw) > 65536:
            return
        row = project(json.loads(raw), nonce)
        if row is None:
            return
        body = (json.dumps(row, separators=(',', ':')) + '\n').encode()
        if len(body) > 4096:
            return
        # Atomic directory creation serializes callbacks without flock's AppArmor
        # file-lock permission. Contention drops this optional receipt immediately.
        # A crashed holder leaves diagnostics unavailable until the next VM nonce.
        lock_path = log + '.append-lock'
        try:
            os.mkdir(lock_path, 0o700)
        except FileExistsError:
            return
        try:
            fd = os.open(log, os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW | os.O_NONBLOCK)
            try:
                info = os.fstat(fd)
                if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1:
                    return
                if info.st_size + len(body) <= MAX_LOG:
                    os.write(fd, body)
            finally:
                os.close(fd)
        finally:
            os.rmdir(lock_path)


if __name__ == '__main__':
    try:
        main()
    except Exception:
        pass
    # A diagnostic failure must not block a user command or contaminate stdout.
    sys.exit(0)
