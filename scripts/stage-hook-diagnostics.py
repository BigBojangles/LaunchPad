#!/usr/bin/env python3
"""Patch a reviewed helper into a new output; never modify templates or sessions."""
import hashlib
import pathlib
import sys
sys.dont_write_bytecode = True
from stage_guest_session_exit import add_exit_notice

source, expected, destination = sys.argv[1:]
source = pathlib.Path(source)
output = pathlib.Path(destination)
raw = source.read_bytes()
assert hashlib.sha256(raw).hexdigest() == expected.lower(), 'Reviewed helper changed'
assert not output.exists(), 'Preserve existing output'
text = raw.decode().replace('\r\n', '\n')
assert 'relay_hook_diagnostics' not in text
assert text.count("my ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();") == 1
helpers = (pathlib.Path(__file__).parent / 'guest-hook-diagnostic.pl').read_text()
text = text.replace("my ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();",
                    helpers + '\nmy ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();')
assert text.count('    drop_to_builder();\n    start_agent(') == 1
assert text.count('            drop_to_builder();\n            start_agent(') == 1
text = text.replace('drop_to_builder();\n    start_agent(',
                    'drop_to_builder();\n    configure_hook_diagnostic();\n    start_agent(')
text = text.replace('drop_to_builder();\n            start_agent(',
                    'drop_to_builder();\n            configure_hook_diagnostic();\n            start_agent(')
assert text.count('    report_guest_size();\n    my $reaped') == 1
text = text.replace('    report_guest_size();\n    my $reaped',
                    '    relay_hook_diagnostics();\n    report_guest_size();\n    my $reaped')
assert text.count('\nsend_project_changes();') == 1
text = text.replace('\nsend_project_changes();',
                    '\nrelay_hook_diagnostics();\ncleanup_hook_diagnostic();\nsend_project_changes();')
assert text.count('    my ($fh, $data) = @_;\n    my $off = 0;') == 1
text = text.replace('    my ($fh, $data) = @_;\n    my $off = 0;',
                    '''    my ($fh, $data) = @_;
    # Resynchronize after a partial optional record before any mandatory header.
    # The sole parent writer never interleaves diagnostics with AUTH/HOME bodies.
    if ($hook_partial && $status && fileno($fh) == fileno($status)) {
        $data = "\\n" . $data;
        $hook_partial = 0;
    }
    my $off = 0;''')
auth_start = text.index('sub send_auth {')
auth_end = text.index('\nsub send_home {', auth_start)
auth_helpers = (pathlib.Path(__file__).parent / 'guest-auth-sync.pl').read_text()
auth_state = "my ($auth_stamp, $auth_checked) = ('', 0);"
assert auth_helpers.count(auth_state) == 1
auth_helpers = auth_helpers.replace(auth_state, '')
auth_anchor = 'my ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();'
assert text.count(auth_anchor) == 1
text = text[:auth_start] + auth_helpers + text[auth_end:]
text = text.replace(auth_anchor, auth_state + '\n' + auth_anchor)
assert text.count('    relay_hook_diagnostics();\n    report_guest_size();') == 1
text = text.replace('    relay_hook_diagnostics();\n    report_guest_size();',
                    '    relay_auth_updates();\n    relay_hook_diagnostics();\n    report_guest_size();')
assert text.count('\nrelay_hook_diagnostics();\ncleanup_hook_diagnostic();') == 1
text = text.replace('\nrelay_hook_diagnostics();\ncleanup_hook_diagnostic();',
                    '\nrelay_auth_updates(1);\nrelay_hook_diagnostics();\ncleanup_hook_diagnostic();')
text = add_exit_notice(text)
output.write_text(text, encoding='utf-8', newline='\n')
print(hashlib.sha256(output.read_bytes()).hexdigest())
