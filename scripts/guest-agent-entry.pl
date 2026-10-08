#!/usr/bin/perl
# Root-owned entry, entered only after aa-exec and setpriv. The diagnostics
# descriptor closes on agent exec; the agent never inherits the control pipe.
use strict;
use warnings;
use Fcntl qw(F_SETFD FD_CLOEXEC);
use IO::Handle;
use JSON::PP;
use Digest::SHA;
use Cwd ();
use Errno qw(ENOENT);

my ($fd, $agent, $bin) = @ARGV;
die "Invalid entry arguments" unless @ARGV == 3 && $fd =~ /\A[0-9]+\z/
    && $agent =~ /\A(?:grok|codex|claude)\z/
    && $bin eq "/usr/local/bin/$agent";
open my $diagnostics, '>&=', int($fd) or die "No agent diagnostics";
$diagnostics->autoflush(1);
fcntl($diagnostics, F_SETFD, FD_CLOEXEC) or die "Diagnostic close-on-exec";
my @inner_arguments;
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
    # Additive, root-managed candidate only. Existing images without this
    # manifest retain their prior launch; never claim those images are double-boxed.
    my $policy_path = '/etc/launchpad/inner-sandbox.json';
    my @policy_stat = lstat($policy_path);
    die "Inner policy lookup failed" unless @policy_stat || $! == ENOENT;
    if (@policy_stat) {
        die "Untrusted inner policy" unless -f _ && !-l _ && $policy_stat[4] == 0
            && ($policy_stat[2] & 0022) == 0 && $policy_stat[3] == 1 && $policy_stat[7] <= 8192;
        for my $parent ('/etc', '/etc/launchpad', '/usr', '/usr/local', '/usr/local/bin') {
            my @parent_stat = lstat($parent);
            die "Untrusted inner policy ancestry" unless @parent_stat && -d _ && !-l _
                && $parent_stat[4] == 0 && ($parent_stat[2] & 0022) == 0;
        }
        open my $policy_file, '<', $policy_path or die "Inner policy unavailable";
        local $/;
        my $policy = decode_json(<$policy_file>);
        close $policy_file;
        die "Invalid inner policy" unless ref($policy) eq 'HASH' && ($policy->{schema} // 0) == 1
            && ref($policy->{agents}) eq 'HASH';
        my $pin = $policy->{agents}{$agent};
        die "Agent inner sandbox not pinned" unless ref($pin) eq 'HASH'
            && ($pin->{version} // '') =~ /\A\d+\.\d+\.\d+\z/
            && ($pin->{binarySha256} // '') =~ /\A[0-9a-f]{64}\z/;
        my @binary_stat = stat($bin);
        die "Untrusted agent binary" unless @binary_stat && -f _ && $binary_stat[4] == 0
            && ($binary_stat[2] & 0022) == 0;
        my $real_binary = Cwd::abs_path($bin) // die "Agent binary target unavailable";
        my @components = split '/', $real_binary;
        pop @components;
        my $parent = '';
        for my $component (@components) {
            next unless length($component);
            $parent .= '/' . $component;
            my @parent_stat = lstat($parent);
            die "Untrusted agent binary ancestry" unless @parent_stat && -d _ && !-l _
                && $parent_stat[4] == 0 && ($parent_stat[2] & 0022) == 0;
        }
        open my $binary, '<', $bin or die "Agent binary unavailable";
        binmode $binary;
        my $hash = Digest::SHA->new(256)->addfile($binary)->hexdigest;
        close $binary;
        die "Agent changed since inner sandbox verification" unless $hash eq $pin->{binarySha256};
        die "Wrong project workspace" unless Cwd::getcwd() eq '/home/builder/in/project';
        if ($agent eq 'grok') {
            @inner_arguments = ('--sandbox', 'workspace');
        } elsif ($agent eq 'codex') {
            @inner_arguments = ('--sandbox', 'workspace-write', '-c', 'sandbox_workspace_write.exclude_slash_tmp=true',
                '-c', 'sandbox_workspace_write.exclude_tmpdir_env_var=true');
        } else {
            # Earlier Claude releases let repository settings override this
            # launch policy. Only a verified modern pin may use this contract.
            my @version = split /\./, $pin->{version};
            die "Claude sandbox compatibility requires a newer verified pin" unless
                $version[0] > 2 || ($version[0] == 2 && ($version[1] > 1 || ($version[1] == 1 && $version[2] >= 285)));
            @inner_arguments = ('--settings', encode_json({sandbox => {enabled => JSON::PP::true,
                allowUnsandboxedCommands => JSON::PP::false, failIfUnavailable => JSON::PP::true}}));
        }
    }
    1;
};
unless ($ok) {
    print {$diagnostics} "AGENT-POLICY-FAILED\n";
    exit 1;
}
print {$diagnostics} "AGENT-PICK $agent\n";
exec $bin, @inner_arguments or do {
    print {$diagnostics} "AGENT-POLICY-FAILED\n";
    exit 1;
};
