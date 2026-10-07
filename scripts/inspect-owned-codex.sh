#!/usr/bin/env bash
# Inspect only a stopped owned Codex fixture, without booting or changing it.
set -euo pipefail
project=$(realpath -- "$(dirname -- "$0")/..")
disk=$(realpath -- "${1:?stopped owned session required}")
report=$(realpath -- "${2:?its existing private report directory required}")
case "$disk" in "$project"/tests/LaunchPad.Tests/TestResults/guest/*/session.qcow2) ;; *) exit 2 ;; esac
test "$(dirname -- "$disk")" = "$report"
test -f "$report/agent-tools-private.json"
python3 - "$report/agent-tools-private.json" <<'PY'
import json, sys
record = json.load(open(sys.argv[1]))
assert record['mode'] == 'activity-observe' and record['shutdown'] is True
PY
before=$(sha256sum "$disk" | cut -d' ' -f1)
export LIBGUESTFS_BACKEND=direct
export SUPERMIN_KERNEL='/mnt/c/Program Files/WSL/tools/kernel'
export SUPERMIN_MODULES="/lib/modules/$(uname -r)"
guestfish --ro -a "$disk" -i find /home/builder/.codex > "$report/codex-paths-private.txt"
guestfish --ro -a "$disk" -i <<EOF
download /home/builder/.codex/app-server-daemon/daemon.stderr.log "$report/codex-daemon-stderr-private.log"
download /home/builder/.codex/app-server-daemon/loaded-threads.json "$report/codex-loaded-threads-private.json"
download /home/builder/.codex/logs_2.sqlite "$report/codex-logs-private.sqlite"
download /home/builder/.codex/logs_2.sqlite-wal "$report/codex-logs-private.sqlite-wal"
download /home/builder/.codex/state_5.sqlite "$report/codex-state-private.sqlite"
download /home/builder/.codex/state_5.sqlite-wal "$report/codex-state-private.sqlite-wal"
EOF
python3 - "$report" <<'PY'
import json, pathlib, sqlite3, sys
report = pathlib.Path(sys.argv[1])
database = sqlite3.connect('file:' + str(report / 'codex-logs-private.sqlite') + '?mode=ro', uri=True)
schema = database.execute("SELECT name,sql FROM sqlite_master WHERE type='table'").fetchall()
(report / 'codex-log-schema-private.json').write_text(json.dumps(schema, indent=2))
rows = database.execute("SELECT level,target,substr(feedback_log_body,1,1200) FROM logs WHERE target LIKE '%unix_socket%' OR feedback_log_body LIKE '%handshake%' OR feedback_log_body LIKE '%invalid HTTP%' ORDER BY id LIMIT 25").fetchall()
(report / 'codex-socket-log-selection-private.json').write_text(json.dumps(rows, indent=2))
state = sqlite3.connect('file:' + str(report / 'codex-state-private.sqlite') + '?mode=ro', uri=True)
columns = [row[1] for row in state.execute('PRAGMA table_info(threads)')]
metadata_columns = [name for name in ('id', 'cwd', 'source', 'model_provider', 'thread_source', 'originator', 'cli_version') if name in columns]
threads = [dict(zip(metadata_columns, row)) for row in state.execute('SELECT ' + ','.join(metadata_columns) + ' FROM threads LIMIT 10')]
(report / 'codex-thread-metadata-private.json').write_text(json.dumps(dict(columns=columns, threads=threads), indent=2))
PY
test "$(sha256sum "$disk" | cut -d' ' -f1)" = "$before"
printf 'Owned Codex paths/logs collected read-only; disk hash unchanged.\n'
