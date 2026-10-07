#!/usr/bin/env python3
"""Owned callback schema witness. Never retain prompts, commands or responses."""
import json
import os
import pathlib
import re
import sys
import time
import uuid


def main():
    root = pathlib.Path('/home/builder/in/project')
    output = root / 'fixture-output' / 'grok-hooks'
    nonce = sys.argv[1] if len(sys.argv) == 2 else ''
    if not re.fullmatch(r'[a-f0-9]{32}', nonce):
        return
    raw = sys.stdin.buffer.read(65537)
    if len(raw) > 65536:
        return
    value = json.loads(raw)
    if not isinstance(value, dict) or len(value) > 64:
        return
    events = ['SessionStart', 'SessionEnd', 'UserPromptSubmit', 'PostToolUse',
              'PostToolUseFailure', 'Stop', 'StopFailure', 'Notification',
              'SubagentStart', 'SubagentStop']
    event = value.get('hookEventName', value.get('hook_event_name', os.environ.get('GROK_HOOK_EVENT')))
    event = next((name for name in events if isinstance(event, str)
                  and name.lower() == event.replace('_', '').lower()), 'Unknown')
    row = dict(nonce=nonce, capturedNs=time.time_ns(), pid=os.getpid(), parentPid=os.getppid(),
               uid=os.getuid(), schema={key: type(item).__name__ for key, item in value.items()},
               hookEventName=event, identity={})
    # Names/types of unknown fields are enough to investigate the schema. Values
    # outside this identifier allowlist are never written, hashed or printed.
    for key in ['sessionId', 'agentId', 'subagentId', 'parentSessionId', 'turnId',
                'promptId', 'toolUseId', 'requestId', 'notificationType']:
        snake_key = re.sub(r'(?<!^)(?=[A-Z])', '_', key).lower()
        item = value.get(key, value.get(snake_key))
        if isinstance(item, str) and re.fullmatch(r'[A-Za-z0-9_.:-]{1,128}', item):
            row['identity'][key] = item
    row['environmentIdentity'] = {}
    for key in ['GROK_HOOK_EVENT', 'GROK_SESSION_ID']:
        item = os.environ.get(key, '')
        if re.fullmatch(r'[A-Za-z0-9_.:-]{1,128}', item):
            row['environmentIdentity'][key] = item
    for key in ['cwd', 'workspaceRoot']:
        snake_key = re.sub(r'(?<!^)(?=[A-Z])', '_', key).lower()
        row[key + 'MatchesOwnedProject'] = value.get(key, value.get(snake_key)) == str(root)
    row['adapterInput'] = {}
    for key in ['hookEventName', 'hook_event_name', 'sessionId', 'session_id', 'promptId', 'prompt_id',
                'agentId', 'agent_id', 'subagentId', 'subagent_id', 'parentSessionId', 'parent_session_id']:
        item = value.get(key)
        if isinstance(item, str) and re.fullmatch(r'[A-Za-z0-9_.:-]{1,128}', item):
            row['adapterInput'][key] = item
    row['stopMetadata'] = {}
    for key in ['stopHookActive', 'stop_hook_active', 'backgroundTasks', 'background_tasks', 'sessionCrons', 'session_crons']:
        if key not in value:
            continue
        item = value[key]
        if key in ['stopHookActive', 'stop_hook_active']:
            row['stopMetadata'][key] = item if isinstance(item, bool) else None
            row['adapterInput'][key] = item if isinstance(item, bool) else None
        else:
            # Record emptiness/counts plus bounded task provenance below. No
            # description, command, result or cron expression is retained.
            row['stopMetadata'][key + 'Count'] = len(item) if isinstance(item, list) else None
            row['adapterInput'][key] = ([] if not item else [None]) if isinstance(item, list) else None
            if isinstance(item, list):
                row['stopMetadata'][key + 'EntrySchema'] = [
                    {name: type(field).__name__ for name, field in entry.items()} if isinstance(entry, dict) else type(entry).__name__
                    for entry in item[:8]]
                # Fixed state words only, for interpreting whether a returned
                # array lists current or already finished jobs. No free text.
                allowed_states = {'running', 'pending', 'completed', 'done', 'failed', 'cancelled', 'stopped', 'finished', 'active'}
                row['stopMetadata'][key + 'States'] = [
                    {name: field for name, field in entry.items() if name in ['status', 'state']
                     and isinstance(field, str) and field in allowed_states}
                    for entry in item[:8] if isinstance(entry, dict)]
                # Preserve bounded task provenance for generated wakeups, while
                # omitting every command/description/result payload.
                safe_tasks = []
                for entry in item[:64]:
                    safe = {}
                    if isinstance(entry, dict):
                        for name in ['id', 'type', 'status', 'state']:
                            field = entry.get(name)
                            if not isinstance(field, str):
                                continue
                            if name == 'id':
                                try:
                                    if str(uuid.UUID(field)) == field:
                                        safe[name] = field
                                except ValueError:
                                    pass
                            elif name in ['status', 'state'] and field in allowed_states:
                                safe[name] = field
                            elif name == 'type' and re.fullmatch(r'[A-Za-z_]{1,32}', field):
                                safe[name] = field
                    safe_tasks.append(safe if safe else None)
                if len(item) <= 64:
                    row['adapterInput'][key] = safe_tasks
    if row['cwdMatchesOwnedProject']:
        row['adapterInput']['cwd'] = str(root)
    if row['workspaceRootMatchesOwnedProject']:
        row['adapterInput']['workspaceRoot'] = str(root)
    row['policy'] = pathlib.Path('/proc/self/attr/current').read_text().strip()
    output.mkdir(exist_ok=True)
    target = output / (uuid.uuid4().hex + '.json')
    target.write_text(json.dumps(row) + '\n')


if __name__ == '__main__':
    try:
        main()
    except Exception:
        pass
    # Passive callback failure must not affect the CLI or inject model context.
    sys.exit(0)
