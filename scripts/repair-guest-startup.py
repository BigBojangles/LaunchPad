#!/usr/bin/env python3
"""Stage D05 repair from the audited script; never edit a template or source input."""
import hashlib
import pathlib
import sys

input_path, output_path = map(pathlib.Path, sys.argv[1:3])
audited_hash = "9b2143016627be60435394dc17bb5cbcf7a79343b53ca5de8dd59311ff7c4d61"
data = input_path.read_bytes()
if hashlib.sha256(data).hexdigest() != audited_hash:
    raise SystemExit("Input differs from the audited candidate; inspect it before applying this repair.")
text = data.decode("utf-8")


def replace_exact(before, after, count=1):
    global text
    if text.count(before) != count:
        raise SystemExit(f"Unexpected source shape for {before!r}; no output written.")
    text = text.replace(before, after)


replace_exact("F_GETFL F_SETFL);", "F_GETFL F_SETFL F_SETFD FD_CLOEXEC);")
replace_exact("use IO::Handle;", "use IO::Handle;\nuse IO::Select;")
replace_exact("my $child = fork();", "my ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();\nmy $child = fork();")
replace_exact("elsif ($child == 0) {\n    setsid();", "elsif ($child == 0) {\n    close $agent_diag_in;\n    setsid();")
replace_exact("else {\n    mark(\"TUI-PID $child\");", "else {\n    close $agent_diag_out;\n    relay_agent_markers($agent_diag_in);\n    mark(\"TUI-PID $child\");")
replace_exact("    start_agent();", "    start_agent($agent_diag_out);", 2)
replace_exact("        $child = fork();", "        ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();\n        $child = fork();")
replace_exact("        if (defined $child && $child == 0) {\n            setsid();", "        if (defined $child && $child == 0) {\n            close $agent_diag_in;\n            setsid();")
replace_exact("        mark('DOOR-CLOSED');", "        close $agent_diag_out;\n        relay_agent_markers($agent_diag_in);\n        mark('DOOR-CLOSED');")
replace_exact("sub start_agent {\n", "sub start_agent {\n    my ($diagnostics) = @_;\n")
replace_exact("            mark('AGENT-MISSING custom');", "            agent_mark($diagnostics, 'AGENT-MISSING custom');")
replace_exact('        mark("AGENT-MISSING $agent");', '        agent_mark($diagnostics, "AGENT-MISSING $agent");')
replace_exact('    mark("AGENT-PICK $agent");', '    agent_mark($diagnostics, "AGENT-PICK $agent");')
helpers = r'''
# Only this privileged parent writes diagnostic markers to the serial device.
# The unprivileged child has a framed pipe which closes automatically on exec.
sub agent_diagnostic_pipe {
    pipe my $input, my $output or die "agent diagnostics: $!";
    fcntl($output, F_SETFD, FD_CLOEXEC) or die "agent diagnostics cloexec: $!";
    $output->autoflush(1);
    return ($input, $output);
}

sub agent_mark {
    my ($output, $message) = @_;
    print {$output} $message, "\n" or die "agent diagnostic write: $!";
}

sub relay_agent_markers {
    my ($input) = @_;
    my $ready = IO::Select->new($input);
    my $pending = '';
    while ($ready->can_read(5)) {
        my $chunk;
        my $read = sysread($input, $chunk, 256);
        last unless defined $read && $read > 0;
        $pending .= $chunk;
        while ($pending =~ s/\A([^\n]*)\n//) {
            my $message = $1;
            mark($message) if $message =~ /\AAGENT-(?:PICK|MISSING) (?:grok|codex|claude|custom)\z/;
        }
        last if length($pending) > 128;
    }
    close $input;
}

'''
replace_exact("sub start_agent {", helpers + "sub start_agent {")
if output_path.exists():
    raise SystemExit("Output already exists; use a new diagnostic output path.")
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_bytes(text.encode("utf-8"))
print("Staged startup repair SHA256:", hashlib.sha256(output_path.read_bytes()).hexdigest())
