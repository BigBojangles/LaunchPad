#!/usr/bin/env python3
"""Stage terminal ownership repair on the exact reviewed safe-merge runtime."""
import hashlib
import pathlib
import sys

source, target = map(pathlib.Path, sys.argv[1:3])
data = source.read_bytes()
if hashlib.sha256(data).hexdigest() != "5c197b6570248bbe9aa790a590340fe9afbbb4d1461af3db98627274081406d1":
    raise SystemExit("Reviewed safe-merge script changed; inspect before staging.")
text = data.decode("utf-8")

def replace(old, new, count=1):
    global text
    if text.count(old) != count:
        raise SystemExit("Unexpected terminal source fragment: " + old[:70])
    text = text.replace(old, new)

replace("qw(O_RDWR O_WRONLY", "qw(O_RDWR O_NOCTTY O_WRONLY")
replace("sysopen $console, '/dev/hvc0', O_RDWR", "sysopen $console, '/dev/hvc0', O_RDWR | O_NOCTTY")
replace("    setsid();", "    prepare_agent_terminal($agent_diag_out);", 2)
replace("        ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();", """        # Ending a controlling session hangs up existing tty handles. Reopen
        # in the privileged parent before giving the replacement its stdio.
        close($console) if $console;
        unless (sysopen($console, '/dev/hvc0', O_RDWR | O_NOCTTY)) {
            mark('AGENT-TERMINAL-FAILED');
            mark('DOOR-FAILED');
            last;
        }
        binmode $console;
        ($seenRows, $seenCols) = (-1, -1);
        ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();""")
replace("""    if ($console) {
        # 0x540E is TIOCSCTTY. The fd was opened before setsid.
        ioctl $console, 0x540E, 0;
        open STDIN, '<&', $console or exit 1;
        open STDOUT, '>&', $console or exit 1;
        open STDERR, '>&', $console or exit 1;
    }
""", "")
replace("""            if ($console) {
                ioctl $console, 0x540E, 0;
                open STDIN, '<&', $console or exit 1;
                open STDOUT, '>&', $console or exit 1;
                open STDERR, '>&', $console or exit 1;
            }
""", "")
replace(r"/\AAGENT-(?:PICK|MISSING) (?:grok|codex|claude|custom)\z/",
        r"/\A(?:AGENT-(?:PICK|MISSING) (?:grok|codex|claude|custom)|AGENT-TERMINAL-FAILED)\z/")
helper = r'''
# The parent opens with O_NOCTTY: only this new agent session may own the
# terminal. Keep device access confined to inherited stdin/stdout/stderr.
sub prepare_agent_terminal {
    my ($diagnostics) = @_;
    my $session = setsid();
    my $ok = defined($session) && $session >= 0 && $console
        && defined(ioctl($console, 0x540E, 0))
        && open(STDIN, '<&', $console)
        && open(STDOUT, '>&', $console)
        && open(STDERR, '>&', $console);
    my $foreground = pack('i', 0);
    $ok &&= defined(ioctl(STDIN, 0x540F, $foreground)) && unpack('i', $foreground) == getpgrp();
    unless ($ok) {
        agent_mark($diagnostics, 'AGENT-TERMINAL-FAILED');
        exit 1;
    }
}

'''
replace("sub start_agent {", helper + "sub start_agent {")
if target.exists():
    raise SystemExit("Output already exists; retain it and use a new output path.")
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(text.encode("utf-8"))
print("Staged terminal repair SHA256:", hashlib.sha256(target.read_bytes()).hexdigest())
