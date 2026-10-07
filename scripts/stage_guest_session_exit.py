#!/usr/bin/env python3
"""Add a final-child exit notice to reviewed bytes, preserving the input."""
import hashlib
import pathlib
import sys


def add_exit_notice(text):
    assert 'notify_agent_exit' not in text, 'Exit notice already staged'
    assert 'use Time::HiRes' in text, 'Reviewed helper must load the bounded-writer clock'
    reap = '    my $reaped = waitpid $child, WNOHANG;\n    last if $reaped == $child;'
    assert text.count(reap) == 1, 'Unexpected final-child wait; inspect first'
    text = text.replace(reap, '''    my $reaped = waitpid $child, WNOHANG;
    if ($reaped == $child) {
        # Only the final actually reaped child; PAUSE replacements and failed
        # imports do not signal terminal completion.
        notify_agent_exit();
        last;
    }''')
    helpers = pathlib.Path(__file__).with_name('guest-session-exit.pl').read_text()
    return text + '\n' + helpers


if __name__ == '__main__':
    source, expected, destination = sys.argv[1:]
    source, output = pathlib.Path(source), pathlib.Path(destination)
    raw = source.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == expected.lower(), 'Reviewed input changed'
    assert not output.exists(), 'Preserve existing output'
    output.write_text(add_exit_notice(raw.decode().replace('\r\n', '\n')), encoding='utf-8', newline='\n')
    print(hashlib.sha256(output.read_bytes()).hexdigest())
