#!/usr/bin/env python3
"""Stage confinement parity from pinned inputs, never mutate a live template."""
import hashlib
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
pins = {
    'bl-proof-before.sh': 'eb26905fecbf9bead21ac060c56e8392f949d30276cc4b82363f3775ce8c57ae',
    'profile-before': 'a0f442d22c2d368be784d944c92e0d585ecec299ddd6311a34a9082192b00d6a',
}
for name, expected in pins.items():
    if hashlib.sha256((root / name).read_bytes()).hexdigest() != expected:
        raise SystemExit('Reviewed input changed: ' + name)
text = (root / 'bl-proof-before.sh').read_text()
def replace(old, new, count=1):
    global text
    if text.count(old) != count:
        raise SystemExit('Unexpected source: ' + old[:70])
    text = text.replace(old, new)

replace('    prepare_agent_terminal($agent_diag_out);',
        '    require_agent_policy($agent_diag_out);\n    prepare_agent_terminal($agent_diag_out);', 2)
replace('|AGENT-TERMINAL-FAILED)\\z/', '|AGENT-(?:TERMINAL|POLICY)-FAILED)\\z/')
replace("    my $pending = '';\n    while ($ready->can_read(5))", "    my $pending = '';\n    my $picked = 0;\n    while ($ready->can_read(5))")
replace('            my $message = $1;', "            my $message = $1;\n            $picked = 1 if $message eq \"AGENT-PICK $agent\";")
replace('    close $input;\n}', "    close $input;\n    mark('AGENT-POLICY-FAILED') if $agent ne 'custom' && !$picked;\n}")
replace('    agent_mark($diagnostics, "AGENT-PICK $agent");\n    exec $bin;', '''    if ($agent ne 'custom') {
        # Only the trusted entry receives this pipe. It verifies the loaded
        # identity and no-new-privileges, then closes the pipe on real CLI exec.
        fcntl($diagnostics, F_SETFD, 0) or die "agent diagnostics inherit: $!";
        exec '/usr/bin/aa-exec', '-p', 'launchpad-agent', '--',
            '/usr/bin/setpriv', '--no-new-privs', '--',
            '/usr/local/bin/launchpad-agent', fileno($diagnostics), $agent, $bin;
        agent_mark($diagnostics, 'AGENT-POLICY-FAILED');
        exit 1;
    }
    agent_mark($diagnostics, "AGENT-PICK $agent");
    exec $bin;''')
helper = '''# The privileged child verifies policy readiness before relinquishing UID.
# Custom programs retain their existing unprivileged guest/host isolation.
sub require_agent_policy {
    my ($diagnostics) = @_;
    return if $agent eq 'custom';
    my $ready = eval {
        open my $profiles, '<', '/sys/kernel/security/apparmor/profiles' or die "Policy unavailable";
        my $loaded = grep { $_ eq "launchpad-agent (enforce)\\n" } <$profiles>;
        close $profiles;
        die "Policy not enforced" unless $loaded;
        for my $path ('/usr/bin/aa-exec', '/usr/bin/setpriv', '/usr/local/bin/launchpad-agent') {
            my @st = stat($path);
            die "Untrusted policy entry" unless @st && $st[4] == 0 && ($st[2] & 0022) == 0 && -f $path && -x $path;
        }
        1;
    };
    unless ($ready) {
        agent_mark($diagnostics, 'AGENT-POLICY-FAILED');
        exit 1;
    }
}

'''
replace('sub start_agent {', helper + 'sub start_agent {')
# Ending the foreground process does not end children in other process groups.
# Stop every remaining unprivileged writer before changing its imported files
# or producing the return snapshot; forced/incomplete cleanup stays failed.
replace('my @hvc = glob', 'my $agent_children_unsafe = 0;\nmy @hvc = glob')
replace("            kill 'TERM', $child;\n            waitpid $child, 0;", """            unless (stop_agent_children()) {
                mark('DOOR-FAILED');
                last;
            }
            waitpid $child, 0;""")
replace('sub send_project_changes {\n    my $fence;', """sub stop_agent_children {
    my $ok = eval {
        for my $path ('/usr/bin/python3', '/usr/local/lib/launchpad/quiesce.py') {
            my @st = stat($path);
            die "Untrusted child cleanup" unless @st && $st[4] == 0 && ($st[2] & 0022) == 0 && -f $path && -x $path;
        }
        system '/usr/bin/python3', '-I', '/usr/local/lib/launchpad/quiesce.py';
        die "Child cleanup incomplete" unless $? == 0;
        1;
    };
    $agent_children_unsafe = 1 unless $ok;
    mark($agent_children_unsafe ? 'AGENT-CHILDREN-FAILED' : 'AGENT-CHILDREN-STOPPED');
    return !$agent_children_unsafe;
}

sub send_project_changes {
    unless (stop_agent_children()) { mark('RETURN-FAILED'); return; }
    my $fence;""")
profile = (root / 'profile-before').read_text()
profile = profile.replace('profile grok ', 'profile launchpad-agent ')
profile = profile.replace('rwlkx,', 'rwlk,')
profile = profile.replace('  /proc/** r,', '  /proc/ r,\n  /proc/** r,')
profile = profile.replace('  /dev/tty rw,', '  /dev/tty rw,\n  # Inherited stdio only; root-owned hvc0 permissions still prevent builder opens.\n  /dev/hvc0 rw,')
profile = profile.replace('  deny /dev/hvc* rw,', '  deny /dev/hvc[1-9]* rw,')
profile = profile.replace('  /home/builder/.grok/ rwlk,', '''  /home/builder/ r,
  /home/builder/.{profile,bashrc} r,
  /home/builder/.codex/ rwkl,
  /home/builder/.codex/** rwkmlix,
  /home/builder/.claude/ rwkl,
  /home/builder/.claude/** rwkmlix,
  /home/builder/.claude.json{,.lock,.tmp*,.backup*} rwkl,
  /home/builder/.claude.json.lock/ rwkl,
  /home/builder/.claude.json.lock/** rwkl,
  /home/builder/.grok/ rwlk,''')
unit = (root / 'unit-before.service').read_text()
if unit.count('After=systemd-udevd.service') != 1:
    raise SystemExit('Unexpected service ordering')
unit = unit.replace('After=systemd-udevd.service', 'After=systemd-udevd.service apparmor.service\nWants=apparmor.service')
for name, body in [('bl-proof.sh', text), ('usr.local.bin.grok', profile), ('bl-proof.service', unit)]:
    path = root / name
    if path.exists():
        raise SystemExit('Keep previous candidate output: ' + name)
    path.write_text(body, encoding='utf-8', newline='\n')
    print(name, hashlib.sha256(path.read_bytes()).hexdigest())
