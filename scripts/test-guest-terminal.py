#!/usr/bin/env python3
"""Exercise the actual Perl binding on owned Linux PTYs before VM proof."""
import json
import os
import pathlib
import pty
import select
import subprocess
import sys
import time

before, after, report_dir = map(pathlib.Path, sys.argv[1:4])
report_dir.mkdir(parents=True, exist_ok=False)
original = before.read_text()
patched = after.read_text()
helper = "sub prepare_agent_terminal {" + patched.split("sub prepare_agent_terminal {", 1)[1].split("sub start_agent {", 1)[0]
legacy = "setsid();\n" + original.split("    if ($console) {", 1)[1].split("    close $status", 1)[0]
legacy = "setsid();\n    if ($console) {" + legacy.split("setsid();\n", 1)[1]

header = r'''
use strict; use warnings;
use POSIX qw(setsid); use Fcntl qw(O_RDWR O_NOCTTY);
use IO::Handle; use JSON::PP qw(encode_json);
my ($tty, $record_path) = @ARGV;
open my $output, '>', $record_path or die $!; $output->autoflush(1);
open my $diagnostics, '>', "$record_path.diagnostics" or die $!;
$SIG{INT} = 'IGNORE';
my $console; sysopen($console, $tty, __FLAGS__) or die $!;
sub agent_mark { my ($out, $message) = @_; print {$out} $message, "\n"; }
sub record { print {$output} encode_json($_[0]), "\n"; }
'''
footer = r'''
for my $launch ('fresh', 'restarted') {
    my $child = fork(); die $! unless defined $child;
    if ($child == 0) {
        __BIND__
        my $fg = pack('i', 0);
        my $ok = defined(ioctl(STDIN, 0x540F, $fg));
        record({kind=>'binding', launch=>$launch, group=>getpgrp(),
            foreground=>($ok ? unpack('i', $fg) : undef)});
        $SIG{INT} = sub { record({kind=>'interrupt', launch=>$launch}); exit 0; };
        $SIG{ALRM} = sub { record({kind=>'uninterrupted', launch=>$launch}); exit 2; };
        STDOUT->autoflush(1);
        print "\r\nPTY-READY-$launch\r\n";
        alarm 2;
        my $byte; sysread(STDIN, $byte, 1);
        exit 3;
    }
    waitpid($child, 0);
    record({kind=>'exit', launch=>$launch, status=>$?});
}
'''
results = {}
for label, flags, binding, definitions in (
    ('before', 'O_RDWR', legacy, ''),
    ('after', 'O_RDWR | O_NOCTTY', 'prepare_agent_terminal($diagnostics);', helper),
    ('refused', 'O_RDWR', 'prepare_agent_terminal($diagnostics);', helper),
):
    master, slave = pty.openpty()
    records = report_dir / (label + '.jsonl')
    program = header.replace('__FLAGS__', flags) + definitions + footer.replace('__BIND__', binding)
    (report_dir / (label + '.pl')).write_text(program)
    process = subprocess.Popen(['perl', '-e', program, os.ttyname(slave), str(records)],
                               start_new_session=True, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    output = b''
    sent = set()
    try:
        deadline = time.monotonic() + 8
        while process.poll() is None and time.monotonic() < deadline:
            if select.select([master], [], [], .05)[0]:
                output += os.read(master, 4096)
            for launch in ('fresh', 'restarted'):
                if launch not in sent and ('PTY-READY-' + launch).encode() in output:
                    os.write(master, b'\x03')
                    sent.add(launch)
        stdout, stderr = process.communicate(timeout=1)
        if process.returncode != 0:
            raise RuntimeError(label + ' harness failed: ' + stderr.decode())
    finally:
        if process.poll() is None: process.kill(); process.wait()
        os.close(master); os.close(slave)
        (report_dir / (label + '-terminal.txt')).write_bytes(output)
    items = [json.loads(line) for line in records.read_text().splitlines()]
    results[label] = items
    bindings = [item for item in items if item['kind'] == 'binding']
    if label == 'refused':
        assert not bindings, items
        assert all(item['status'] == 256 for item in items if item['kind'] == 'exit'), items
        assert pathlib.Path(str(records) + '.diagnostics').read_text().splitlines() == ['AGENT-TERMINAL-FAILED'] * 2
    elif label == 'before':
        assert len(bindings) == 2, (label, items)
        assert all(item['foreground'] is None for item in bindings), items
        assert len([item for item in items if item['kind'] == 'uninterrupted']) == 2, items
    else:
        assert len(bindings) == 2, (label, items)
        assert all(item['foreground'] == item['group'] for item in bindings), items
        assert len([item for item in items if item['kind'] == 'interrupt']) == 2, items
        assert all(item['status'] == 0 for item in items if item['kind'] == 'exit'), items
(report_dir / 'terminal-pty-proof.json').write_text(json.dumps(results, indent=2) + '\n')
print('Actual Perl PTY proof: original reproduces both failures; repaired fresh/restarted sessions receive Ctrl+C; a conflicting terminal is refused before launch.')
