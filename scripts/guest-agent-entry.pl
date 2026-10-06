#!/usr/bin/perl
# Root-owned entry, entered only after aa-exec and setpriv. The diagnostics
# descriptor closes on agent exec; the agent never inherits the control pipe.
use strict;
use warnings;
use Fcntl qw(F_SETFD FD_CLOEXEC);
use IO::Handle;

my ($fd, $agent, $bin) = @ARGV;
die "Invalid entry arguments" unless @ARGV == 3 && $fd =~ /\A[0-9]+\z/
    && $agent =~ /\A(?:grok|codex|claude)\z/
    && $bin eq "/usr/local/bin/$agent";
open my $diagnostics, '>&=', int($fd) or die "No agent diagnostics";
$diagnostics->autoflush(1);
fcntl($diagnostics, F_SETFD, FD_CLOEXEC) or die "Diagnostic close-on-exec";
my $ok = eval {
    open my $label, '<', '/proc/self/attr/current' or die "Policy identity";
    my $identity = <$label> // '';
    close $label;
    $identity =~ s/\s+\z//;
    die "Wrong policy" unless $identity eq 'launchpad-agent (enforce)';
    open my $status, '<', '/proc/self/status' or die "Process identity";
    local $/;
    my $state = <$status>;
    close $status;
    die "Wrong user" unless $state =~ /^Uid:\s+1000\s+1000\s+1000\s+1000\s*$/m;
    die "Privilege gain possible" unless $state =~ /^NoNewPrivs:\s+1\s*$/m;
    die "Effective capabilities" unless $state =~ /^CapEff:\s+0+\s*$/m;
    1;
};
unless ($ok) {
    print {$diagnostics} "AGENT-POLICY-FAILED\n";
    exit 1;
}
print {$diagnostics} "AGENT-PICK $agent\n";
exec $bin or do {
    print {$diagnostics} "AGENT-POLICY-FAILED\n";
    exit 1;
};
