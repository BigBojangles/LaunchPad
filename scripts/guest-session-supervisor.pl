#!/usr/bin/perl
use strict;
use warnings;
use POSIX qw(setsid WNOHANG);
use Fcntl qw(O_RDWR O_NOCTTY O_WRONLY O_NONBLOCK F_GETFL F_SETFL F_SETFD FD_CLOEXEC);
use IO::Handle;
use IO::Select;
use File::Temp qw(tempfile tempdir);
use Digest::SHA;
use JSON::PP qw(encode_json decode_json);
use Fcntl qw(O_RDONLY O_NOFOLLOW);
sub mark {
    open my $tty, '>>', '/dev/ttyS0' or die "tty: $!";
    $tty->autoflush(1);
    print {$tty} $_[0], "\n";
    close $tty;
}

open my $up, '<', '/proc/uptime' or die "uptime: $!";
my $line = <$up>;
close $up;
my ($sec) = split /\./, $line;
mark("CMD-RAN $sec");

my $port = '/dev/virtio-ports/fence';
my $dir = '/home/builder/in/project';
mkdir '/home/builder/in' unless -d '/home/builder/in';
mkdir $dir unless -d $dir;
own_builder('/home/builder/in', $dir);

my $fh;
my $opened = 0;
for (1 .. 150) {
    if (-e $port && open $fh, '<', $port) {
        $opened = 1;
        last;
    }
    select undef, undef, undef, 0.2;
}
unless ($opened) {
    mark('FENCE-OPEN-FAIL');
    exit 1;
}
binmode $fh;

my $baselineRoot = '/var/lib/launchpad';
my $baselinePath = "$baselineRoot/import.json";
my @got = load_import_baseline();
my $initialStaging = tempdir('launchpad-import-XXXXXXXX', TMPDIR => 1, CLEANUP => 1);
own_builder($initialStaging);
my @incoming;
mark('IMPORT-READY safe-merge');
my $agent = 'grok';
my $agentCmd = '';
while (1) {
    my $header = <$fh>;
    last unless defined $header;
    $header =~ s/\r?\n\z//;
    next if $header eq '';
    if ($header =~ /^AGENT (grok|codex|claude|custom)$/) {
        $agent = $1;
        mark("AGENT-IN $agent");
        next;
    }
    if ($header =~ /^AGENT-CMD ([A-Za-z0-9._-]{1,64})$/) {
        $agentCmd = $1;
        next;
    }
    if ($header =~ /^PROGRAM (\d+) ([A-Za-z0-9._-]{1,64})$/) {
        my ($psize, $pname) = ($1 + 0, $2);
        if ($psize < 0 || $psize > 268435456) {
            mark('REJECT program');
            pump($fh, undef, $psize) if $psize > 0;
            next;
        }
        system('mkdir', '-p', '/home/builder/.local/bin');
        my $pdest = "/home/builder/.local/bin/$pname";
        open my $pout, '>', $pdest or do {
            mark('WRITE-FAIL program');
            exit 1;
        };
        binmode $pout;
        pump($fh, $pout, $psize);
        close $pout;
        chmod 0755, $pdest;
        system('chown', 'builder:builder', $pdest);
        $agentCmd = $pname;
        mark("PROGRAM-IN $pname");
        next;
    }
    if ($header =~ /^AUTH (\d+)$/) {
        my $size = $1 + 0;
        if ($size > 1048576) {
            mark('REJECT auth');
            my $skip = $size;
            while ($skip > 0) {
                my $buf;
                my $n = read $fh, $buf, ($skip > 65536 ? 65536 : $skip);
                last unless $n;
                $skip -= $n;
            }
            next;
        }
        mkdir '/home/builder/.grok' unless -d '/home/builder/.grok';
        chmod 0700, '/home/builder/.grok';
        my $authPath = '/home/builder/.grok/auth.json';
        if ($size == 0) {
            unlink $authPath;
            mark('AUTH-IN 0');
            next;
        }
        open my $authOut, '>', $authPath or do {
            mark('WRITE-FAIL auth');
            exit 1;
        };
        binmode $authOut;
        pump($fh, $authOut, $size);
        close $authOut;
        chmod 0600, $authPath;
        mark("AUTH-IN $size");
        next;
    }
    if ($header =~ /^HOME (\d+)$/) {
        my $size = $1 + 0;
        if ($size == 0 || $size > 268435456) {
            mark('HOME-IN skip');
            my $skip = $size;
            while ($skip > 0) {
                my $buf;
                my $n = read $fh, $buf, ($skip > 65536 ? 65536 : $skip);
                last unless $n;
                $skip -= $n;
            }
            next;
        }
        mkdir '/home/builder/.grok' unless -d '/home/builder/.grok';
        chmod 0700, '/home/builder/.grok';
        own_builder('/home/builder/.grok');
        my $tarPath = '/tmp/bl-home-in.tar';
        open my $tarOut, '>:raw', $tarPath or do {
            mark('WRITE-FAIL home');
            exit 1;
        };
        my $homeLeft = pump($fh, $tarOut, $size);
        close $tarOut;
        chmod 0600, $tarPath;
        if ($homeLeft == 0 && home_tar_ok($tarPath) && extract_home($tarPath)) {
            mark("HOME-IN $size");
        }
        else {
            mark('HOME-IN fail');
        }
        unlink $tarPath;
        next;
    }
    my ($name, $size);
    if ($header =~ /^FILE (\d+) (.+)$/) {
        ($size, $name) = ($1, $2);
    }
    elsif ($header =~ /^FILE (\S+) (\d+)$/) {
        ($name, $size) = ($1, $2);
    }
    else {
        mark('BAD-LINE');
        next;
    }

    my $item = stage_import_file($fh, $size, $name, $dir, 0644, $initialStaging);
    chmod 0644, $item->[3];
    push @incoming, $item;
}
close $fh;
my $firstImport = !@got;
unless (run_import_child(sub { apply_import_batch(\@incoming); })) {
    mark('IMPORT-FAILED');
    exit 1;
}
rewrite_origin("$dir/.git/config") if $firstImport;
system('chown', '-R', 'builder:builder', $dir);
# AUTH writes this directory as root mode 0700. Grok runs as builder and
# reports "Failed to load config" when it cannot read it.
if (-d '/home/builder/.grok') {
    system('chown', '-R', 'builder:builder', '/home/builder/.grok');
    chmod 0700, '/home/builder/.grok';
    chmod 0600, '/home/builder/.grok/auth.json' if -f '/home/builder/.grok/auth.json';
    mark('GROK-HOME builder');
}

mark('WARN fence-ready');

my $agent_children_unsafe = 0;
my @hvc = glob '/dev/hvc*';
if (@hvc) {
    mark('HVC-NODE ' . join(' ', @hvc));
}
else {
    mark('HVC-MISSING');
}

my $status;
my $statusBuf = '';
my $console;
if (sysopen $status, '/dev/virtio-ports/status', O_RDWR | O_NONBLOCK) {
    binmode $status;
    $status->autoflush(1);
    # Status is emitted only by actual lifecycle events, never startup placeholders.
}

# This open blocks until the Windows tab is connected, then Grok draws
# straight onto that tab. The parent keeps the fd and never closes it.
# A second open is EBUSY, and an open then close hangs up the host.
# The window size arrives from QEMU on the virtio console, not from here.
if (-e '/dev/hvc0' && sysopen $console, '/dev/hvc0', O_RDWR | O_NOCTTY) {
    binmode $console;
}
else {
    mark('HVC-OPEN-FAIL');
}

my ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();
my $child = fork();
if (!defined $child) {
    mark('TUI-PID 0');
    $child = 0;
}
elsif ($child == 0) {
    close $agent_diag_in;
    require_agent_policy($agent_diag_out);
    prepare_agent_terminal($agent_diag_out);
    chdir $dir or exit 1;
    $ENV{HOME} = '/home/builder';
    $ENV{TERM} = 'xterm-256color';
    $ENV{GROK_LOGIN_DEVICE_FLOW} = 'true';
    close $status if $status;
    close_guest_devices();
    drop_to_builder();
    start_agent($agent_diag_out);
    exit 1;
}
else {
    close $agent_diag_out;
    relay_agent_markers($agent_diag_in);
    mark("TUI-PID $child");
    if (open my $comm, '<', "/proc/$child/comm") {
        my $name = <$comm> // '';
        $name =~ s/\s+\z//;
        mark("TUI-COMM $name");
        close $comm;
    }
    select undef, undef, undef, 0.8;
    if (open my $again, '<', "/proc/$child/comm") {
        my $name = <$again> // '';
        $name =~ s/\s+\z//;
        mark("TUI-ALIVE $name");
        close $again;
    }
    else {
        mark('TUI-ALIVE gone');
    }
}

mark('PROCS-BEGIN');
opendir my $proc, '/proc' or die "proc: $!";
while (my $entry = readdir $proc) {
    next unless $entry =~ /^\d+$/;
    next unless open my $comm, '<', "/proc/$entry/comm";
    my $name = <$comm> // '';
    close $comm;
    $name =~ s/\s+\z//;
    mark("PROC $entry $name");
}
closedir $proc;
mark('PROCS-END');

mark('FILES-BEGIN');
for my $item (@got) {
    mark(sprintf('FILE %s %d %04o', $item->[0], $item->[1], $item->[2]));
}
mark('FILES-END');
mark('MOUNTS-BEGIN');
open my $mounts, '<', '/proc/mounts' or die "mounts: $!";
while (my $mount = <$mounts>) {
    $mount =~ s/\n\z//;
    mark($mount);
}
close $mounts;
mark('MOUNTS-END');
my $authSent = 0;
my $homeSent = 0;
my ($seenRows, $seenCols) = (-1, -1);
mark('DOOR-READY safe-import safe-merge');
while ($child > 0) {
    my $line = status_line();
    if (defined $line && $line eq 'PAUSE') {
        if ($child > 0) {
            unless (stop_agent_children()) {
                mark('DOOR-FAILED');
                last;
            }
            waitpid $child, 0;
            $child = 0;
        }
        mark('DOOR-OPEN safe-import');
        unless (read_pause_files()) {
            mark('DOOR-FAILED');
            last;
        }
        # Ending a controlling session hangs up existing tty handles. Reopen
        # in the privileged parent before giving the replacement its stdio.
        close($console) if $console;
        unless (sysopen($console, '/dev/hvc0', O_RDWR | O_NOCTTY)) {
            mark('AGENT-TERMINAL-FAILED');
            mark('DOOR-FAILED');
            last;
        }
        binmode $console;
        ($seenRows, $seenCols) = (-1, -1);
        ($agent_diag_in, $agent_diag_out) = agent_diagnostic_pipe();
        $child = fork();
        if (defined $child && $child == 0) {
            close $agent_diag_in;
            require_agent_policy($agent_diag_out);
    prepare_agent_terminal($agent_diag_out);
            chdir $dir or exit 1;
            $ENV{HOME} = '/home/builder';
            $ENV{TERM} = 'xterm-256color';
            $ENV{GROK_LOGIN_DEVICE_FLOW} = 'true';
            close $status if $status;
            close_guest_devices();
            drop_to_builder();
            start_agent($agent_diag_out);
            exit 1;
        }
        close $agent_diag_out;
        relay_agent_markers($agent_diag_in);
        mark('DOOR-CLOSED');
        next;
    }
    if (defined $line && $line eq 'AUTH-OUT' && !$authSent) {
        send_auth();
        $authSent = 1;
    }
    if (defined $line && $line eq 'HOME-OUT' && !$homeSent) {
        send_home();
        $homeSent = 1;
    }
    report_guest_size();
    my $reaped = waitpid $child, WNOHANG;
    last if $reaped == $child;
    select undef, undef, undef, 0.1 unless defined $line;
}
send_project_changes();

# Read the size QEMU's virtio console stored in the tty. This does not set it.

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
    my $picked = 0;
    while ($ready->can_read(5)) {
        my $chunk;
        my $read = sysread($input, $chunk, 256);
        last unless defined $read && $read > 0;
        $pending .= $chunk;
        while ($pending =~ s/\A([^\n]*)\n//) {
            my $message = $1;
            $picked = 1 if $message eq "AGENT-PICK $agent";
            mark($message) if $message =~ /\A(?:AGENT-(?:PICK|MISSING) (?:grok|codex|claude|custom)|AGENT-(?:TERMINAL|POLICY)-FAILED)\z/;
        }
        last if length($pending) > 128;
    }
    close $input;
    mark('AGENT-POLICY-FAILED') if $agent ne 'custom' && !$picked;
}


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

# The privileged child verifies policy readiness before relinquishing UID.
# Custom programs retain their existing unprivileged guest/host isolation.
sub require_agent_policy {
    my ($diagnostics) = @_;
    return if $agent eq 'custom';
    my $ready = eval {
        open my $profiles, '<', '/sys/kernel/security/apparmor/profiles' or die "Policy unavailable";
        my $loaded = grep { $_ eq "launchpad-agent (enforce)\n" } <$profiles>;
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

sub start_agent {
    my ($diagnostics) = @_;
    my $bin = '/usr/local/bin/grok';
    if ($agent eq 'codex') {
        $bin = '/usr/local/bin/codex';
    }
    elsif ($agent eq 'claude') {
        $bin = '/usr/local/bin/claude';
    }
    elsif ($agent eq 'custom') {
        if ($agentCmd !~ /^[A-Za-z0-9._-]{1,64}$/) {
            agent_mark($diagnostics, 'AGENT-MISSING custom');
            exit 1;
        }
        $bin = "/home/builder/.local/bin/$agentCmd";
        $bin = "/usr/local/bin/$agentCmd" unless -x $bin;
    }
    unless (-x $bin) {
        agent_mark($diagnostics, "AGENT-MISSING $agent");
        exit 1;
    }
    if ($agent ne 'custom') {
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
    exec $bin;
    exit 1;
}


# The privileged parent keeps control/status; a builder child only imports files.

sub load_import_baseline {
    die "Linked baseline directory" if -l $baselineRoot;
    unless (-d $baselineRoot) { mkdir($baselineRoot, 0700) or die "Create baseline: $!"; }
    my @root = stat($baselineRoot);
    die "Untrusted baseline directory" unless @root && $root[4] == 0 && ($root[2] & 0077) == 0;
    return () unless -e $baselinePath || -l $baselinePath;
    sysopen(my $in, $baselinePath, O_RDONLY | O_NOFOLLOW) or die "Open baseline: $!";
    die "Invalid baseline file" unless -f $in && -s $in <= 67108864;
    local $/;
    my $body = <$in>;
    close $in;
    return @{validated_baseline($body)};
}

sub validated_baseline {
    my ($body) = @_;
    my $items = decode_json($body);
    die "Invalid baseline" unless ref($items) eq 'ARRAY';
    my %seen;
    for my $item (@$items) {
        die "Invalid baseline entry" unless ref($item) eq 'ARRAY' && @$item == 5;
        validate_import_name($item->[0]);
        die "Invalid baseline hash" unless $item->[4] =~ /\A[0-9a-f]{64}\z/;
        die "Duplicate baseline entry" if $seen{$item->[0]}++;
    }
    return $items;
}

sub save_import_baseline {
    my ($out, $temporary) = tempfile('import-XXXXXXXX', DIR => $baselineRoot, UNLINK => 1);
    print {$out} encode_json(\@got) or die "Write baseline: $!";
    $out->flush or die "Flush baseline: $!";
    $out->sync or die "Sync baseline: $!";
    close($out) or die "Close baseline: $!";
    rename($temporary, $baselinePath) or die "Install baseline: $!";
}

# Parent drains the result pipe while the builder child applies the batch.
# Protected baseline state is changed only after the child reports success.
sub run_import_child {
    my ($work, $keep) = @_;
    pipe(my $report, my $result) or return 0;
    my $receiver = fork();
    unless (defined $receiver) { close $report; close $result; return 0; }
    if ($receiver == 0) {
        close $report;
        close_guest_devices($keep);
        open STDIN, '<', '/dev/null' or exit 1;
        open STDOUT, '>', '/dev/null' or exit 1;
        open STDERR, '>', '/dev/null' or exit 1;
        drop_to_builder();
        my $ok = eval { $work->(); syswrite_all($result, encode_json(\@got)) or die "Import result"; 1; };
        close $result;
        exit($ok ? 0 : 1);
    }
    close $result;
    my $body = '';
    my $oversize = 0;
    while (1) {
        my $piece;
        my $n = sysread($report, $piece, 65536);
        last unless defined $n && $n > 0;
        $body .= $piece unless $oversize;
        if (length($body) > 67108864) { $oversize = 1; $body = ''; }
    }
    close $report;
    waitpid $receiver, 0;
    return 0 if $? != 0 || $oversize;
    return eval { @got = @{validated_baseline($body)}; save_import_baseline(); 1; } ? 1 : 0;
}

sub read_pause_files {
    my $in;
    sysopen($in, '/dev/virtio-ports/fence', O_RDWR) or return 0;
    binmode $in;
    my $ok = run_import_child(sub { receive_pause_files($in); }, fileno($in));
    close $in;
    return $ok;
}

sub receive_pause_files {
    my ($in) = @_;
    my $staging = tempdir('launchpad-import-XXXXXXXX', TMPDIR => 1, CLEANUP => 1);
    my @incoming;
    while (defined(my $header = <$in>)) {
        die "Incomplete header" unless $header =~ s/\n\z//;
        $header =~ s/\r\z//;
        my ($size, $name, $root, $mode);
        if ($header =~ /^FILE (\d+) (.+)$/) {
            ($size, $name, $root, $mode) = ($1 + 0, $2, $dir, 0644);
        }
        elsif ($header =~ /^PROGRAM (\d+) ([A-Za-z0-9._-]{1,64})$/) {
            ($size, $name, $root, $mode) = ($1 + 0, $2, '/home/builder/.local/bin', 0755);
        }
        else { die "Unexpected import header"; }
        push @incoming, stage_import_file($in, $size, $name, $root, $mode, $staging);
    }
    apply_import_batch(\@incoming);
}

sub validate_import_name {
    my ($name) = @_;
    die "Unsafe file path" if !defined $name || $name =~ /[\x00-\x1f\\:]/ || $name =~ m{^/};
    my @parts = split m{/}, $name, -1;
    die "Unsafe file component" if !@parts || grep { $_ eq '' || $_ eq '.' || $_ eq '..' } @parts;
    return @parts;
}

sub stage_import_file {
    my ($in, $size, $name, $root, $mode, $staging) = @_;
    validate_import_name($name);
    my ($out, $temporary) = tempfile('file-XXXXXXXX', DIR => $staging, UNLINK => 0);
    binmode $out;
    my $digest = Digest::SHA->new(256);
    die "Truncated import" if pump($in, $out, $size, $digest) != 0;
    $out->flush or die "Flush import: $!";
    $out->sync or die "Sync import: $!";
    close($out) or die "Close import: $!";
    return [ $root, $name, $mode, $temporary, $size, $digest->hexdigest ];
}

sub import_target {
    my ($root, $name) = @_;
    my @parts = validate_import_name($name);
    my $parent = '';
    for my $part (split(m{/}, $root), @parts[0 .. $#parts - 1]) {
        next if $part eq '';
        $parent .= '/' . $part;
        die "Linked import parent" if -l $parent;
        mkdir($parent, 0755) or die "Create import parent: $!" unless -d $parent;
    }
    my $path = "$parent/$parts[-1]";
    die "Linked import destination" if -l $path;
    die "Non-file import destination" if -e $path && !-f $path;
    return ($path, $parent);
}

sub apply_import_batch {
    my ($incoming) = @_;
    my %known = map { $_->[0] => $_ } @got;
    my %seen;
    my @ready;
    # Preflight the complete batch before replacing any project content.
    for my $item (@$incoming) {
        my ($root, $name, $mode, $temporary, $size, $hash) = @$item;
        die "Duplicate import path" if $seen{"$root/$name"}++;
        my ($path, $parent) = import_target($root, $name);
        my $current = -e $path ? (project_digest($path))[1] : undef;
        my $previous = $root eq $dir && defined $known{$name} ? $known{$name}->[4] : undef;
        if ($root eq $dir) {
            die "Guest file conflicts with host import" if defined $current && $current ne $hash && (!defined $previous || $current ne $previous);
            die "Guest removed a host-imported file" if !defined $current && defined $previous;
        }
        push @ready, [ $item, $path, $parent, $current ];
    }
    for my $entry (@ready) {
        my ($item, $path, $parent, $current) = @$entry;
        my ($root, $name, $mode, $temporary, $size, $hash) = @$item;
        my ($checked) = import_target($root, $name);
        my $now = -e $checked ? (project_digest($checked))[1] : undef;
        die "Guest file changed after import preflight" unless ($now // '') eq ($current // '');
        unless (defined $current && $current eq $hash) {
            my ($out, $next) = tempfile('.launchpad-send-XXXXXXXX', DIR => $parent, UNLINK => 1);
            binmode $out;
            my ($copied, $copied_hash) = project_digest($temporary, $out);
            die "Staged import changed" unless $copied == $size && $copied_hash eq $hash;
            $out->flush or die "Flush target: $!";
            $out->sync or die "Sync target: $!";
            chmod($mode, $next) or die "Target mode: $!";
            close($out) or die "Close target: $!";
            rename($next, $path) or die "Install target: $!";
        }
        if ($root eq $dir) {
            my @st = stat($path);
            $known{$name} = [ $name, $size, $st[2] & 07777, $st[9] + 0, $hash ];
        }
    }
    @got = map { $known{$_} } sort keys %known;
}

sub pump {
    my ($in, $out, $left, $digest) = @_;
    while ($left > 0) {
        my $buf;
        my $want = $left > 65536 ? 65536 : $left;
        my $n = read $in, $buf, $want;
        return $left unless $n;
        die "Import write: $!" if defined $out && !print {$out} $buf;
        $digest->add($buf) if defined $digest;
        $left -= $n;
    }
    return 0;
}

sub rewrite_origin {
    my ($path) = @_;
    return unless -f $path;
    open my $in, '<', $path or return;
    my @lines = <$in>;
    close $in;
    my $inOrigin = 0;
    for my $i (0 .. $#lines) {
        my $trim = $lines[$i];
        $trim =~ s/^\s+//;
        if ($trim =~ /^\[/ && $trim =~ /\]/) {
            $inOrigin = $trim =~ /^\[remote "origin"\]/;
            next;
        }
        if ($inOrigin && $trim =~ /^url/) {
            $lines[$i] = "\turl = file:///home/builder/fence.git\n";
        }
    }
    open my $out, '>', $path or return;
    print {$out} @lines;
    close $out;
}


# Only the parent opens the device. File discovery and export use builder rights.
sub stop_agent_children {
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
    my $fence;
    unless (sysopen($fence, '/dev/virtio-ports/fence', O_WRONLY)) {
        mark('RETURN-FAILED');
        return;
    }
    binmode $fence;
    my $sender = fork();
    unless (defined $sender) { close $fence; mark('RETURN-FAILED'); return; }
    if ($sender == 0) {
        close_guest_devices(fileno($fence));
        open STDIN, '<', '/dev/null' or exit 1;
        open STDOUT, '>', '/dev/null' or exit 1;
        open STDERR, '>', '/dev/null' or exit 1;
        drop_to_builder();
        my $ok = eval { export_project($fence); 1; };
        close $fence;
        exit($ok ? 0 : 1);
    }
    close $fence;
    waitpid $sender, 0;
    mark($? == 0 ? 'RETURN-COMPLETE' : 'RETURN-FAILED');
}

sub export_project {
    my ($fence) = @_;
    my %sent = map { $_->[0] => $_->[4] } @got;
    my $staging = tempdir('launchpad-return-XXXXXXXX', TMPDIR => 1, CLEANUP => 1);
    my @changed;
    # Stage complete changed files before declaring the batch or any file length.
    walk_project($dir, \%sent, \@changed, $staging);
    syswrite_all($fence, 'PROJECT ' . scalar(@changed) . "\n") or die "Return header";
    for my $item (@changed) {
        my ($rel, $size, $temporary, $expected) = @$item;
        sysopen(my $in, $temporary, O_RDONLY | O_NOFOLLOW) or die "Open snapshot: $!";
        binmode $in;
        die "Snapshot length changed" unless -f $in && -s $in == $size;
        syswrite_all($fence, "FILE $size $rel\n") or die "Return file header";
        my $digest = Digest::SHA->new(256);
        copy_exact($in, $size, sub {
            $digest->add($_[0]);
            syswrite_all($fence, $_[0]) or die "Return body";
        });
        close($in) or die "Close snapshot: $!";
        die "Snapshot content changed" unless $digest->hexdigest eq $expected;
    }
    syswrite_all($fence, "PROJECT-END\n") or die "Return completion";
}

sub copy_exact {
    my ($in, $size, $consume) = @_;
    my $left = $size;
    while ($left > 0) {
        my $buf;
        my $n = read($in, $buf, $left > 65536 ? 65536 : $left);
        die "Unreadable or shortened file" unless defined $n && $n > 0;
        $consume->($buf);
        $left -= $n;
    }
    my $extra;
    my $n = read($in, $extra, 1);
    die "Unreadable or grown file" unless defined $n && $n == 0;
}

sub project_digest {
    my ($path, $out) = @_;
    sysopen(my $in, $path, O_RDONLY | O_NOFOLLOW) or die "Read project file: $!";
    binmode $in;
    die "Non-file project input" unless -f $in;
    my @before = stat($in);
    my $digest = Digest::SHA->new(256);
    copy_exact($in, $before[7], sub {
        $digest->add($_[0]);
        print {$out} $_[0] or die "Snapshot write: $!" if defined $out;
    });
    my @after = stat($in);
    die "Project file changed during read" unless @after &&
        join(':', @before[0, 1, 7, 9, 10]) eq join(':', @after[0, 1, 7, 9, 10]);
    close($in) or die "Close project file: $!";
    return ($before[7] + 0, $digest->hexdigest);
}

sub walk_project {
    my ($root, $sent, $out, $staging) = @_;
    opendir my $dh, $root or die "Read project directory: $!";
    my @names = sort readdir $dh;
    closedir($dh) or die "Close project directory: $!";
    for my $name (@names) {
        next if $name eq '.' || $name eq '..' || lc($name) eq '.git';
        my $full = "$root/$name";
        next if -l $full;
        if (-d $full) {
            walk_project($full, $sent, $out, $staging);
            next;
        }
        next unless -f $full;
        my $rel = $full;
        $rel =~ s{^\Q$dir\E/}{};
        die "Unrepresentable return path" if $rel =~ /[\x00-\x1f\\:]/;
        my ($size, $hash) = project_digest($full);
        next if defined $sent->{$rel} && $sent->{$rel} eq $hash;
        my ($snapshot, $temporary) = tempfile('file-XXXXXXXX', DIR => $staging, UNLINK => 0);
        binmode $snapshot;
        my ($copied, $copied_hash) = project_digest($full, $snapshot);
        die "Project file changed before snapshot" unless $size == $copied && $hash eq $copied_hash;
        $snapshot->flush or die "Snapshot flush: $!";
        $snapshot->sync or die "Snapshot sync: $!";
        close($snapshot) or die "Close snapshot: $!";
        push @$out, [ $rel, $size, $temporary, $hash ];
    }
}

sub report_guest_size {
    return unless $console;
    my $buf = pack('S4', 0, 0, 0, 0);
    return unless ioctl $console, 0x5413, $buf;
    my ($rowsGot, $colsGot) = unpack('S2', $buf);
    return if $rowsGot == $seenRows && $colsGot == $seenCols;
    ($seenRows, $seenCols) = ($rowsGot, $colsGot);
    mark("GUEST-SIZE $rowsGot $colsGot");
}

sub send_auth {
    return unless $status;
    my $path = '/home/builder/.grok/auth.json';
    if (!-s $path) {
        syswrite_all($status, "AUTH 0\n");
        return;
    }
    open my $in, '<:raw', $path or do {
        syswrite_all($status, "AUTH 0\n");
        return;
    };
    local $/;
    my $body = <$in>;
    close $in;
    $body = '' unless defined $body;
    if (length($body) == 0 || length($body) > 1048576) {
        syswrite_all($status, "AUTH 0\n");
        return;
    }
    syswrite_all($status, 'AUTH ' . length($body) . "\n" . $body);
}

sub send_home {
    return unless $status;
    my $root = '/home/builder/.grok';
    my @parts;
    push @parts, 'sessions' if -d "$root/sessions";
    if (!@parts) {
        syswrite_all($status, "HOME 0\n");
        return;
    }
    my $tmp = '/tmp/bl-home-out.tar';
    unlink $tmp;
    if (system('tar', '-cf', $tmp, '-C', $root, @parts) != 0 || !-s $tmp) {
        syswrite_all($status, "HOME 0\n");
        unlink $tmp;
        return;
    }
    my $size = -s $tmp;
    if ($size > 268435456) {
        syswrite_all($status, "HOME BIG\n");
        unlink $tmp;
        return;
    }
    open my $in, '<:raw', $tmp or do {
        syswrite_all($status, "HOME 0\n");
        unlink $tmp;
        return;
    };
    syswrite_all($status, 'HOME ' . $size . "\n");
    my $left = $size;
    while ($left > 0) {
        my $buf;
        my $want = $left > 65536 ? 65536 : $left;
        my $n = read $in, $buf, $want;
        last unless $n;
        syswrite_all($status, $buf);
        $left -= $n;
    }
    close $in;
    unlink $tmp;
}

sub own_builder {
    my @pw = getpwnam('builder');
    return unless @pw;
    foreach my $path (@_) {
        chown $pw[2], $pw[3], $path if defined $path && -e $path;
    }
}

sub home_tar_ok {
    my ($tarPath) = @_;
    open my $list, '-|', 'tar', '-tf', $tarPath or return 0;
    my $ok = 1;
    while (my $member = <$list>) {
        $member =~ s/\r?\n\z//;
        if ($member eq '' || $member =~ m{^/} || $member =~ m{(^|/)\.\.(/|$)} || $member !~ m{^sessions(/|$)}) {
            $ok = 0;
            last;
        }
    }
    close $list;
    return $ok && $? == 0;
}

sub extract_home {
    my ($tarPath) = @_;
    my $pid = fork();
    return 0 unless defined $pid;
    if ($pid == 0) {
        drop_to_builder();
        exec 'tar', '-xf', $tarPath, '-C', '/home/builder/.grok';
        exit 1;
    }
    waitpid $pid, 0;
    return $? == 0;
}

sub close_guest_devices {
    my ($keep) = @_;
    opendir my $dh, '/proc/self/fd' or return;
    my @fds;
    while (my $ent = readdir $dh) {
        next unless $ent =~ /^\d+$/;
        push @fds, $ent + 0;
    }
    closedir $dh;
    foreach my $fd (@fds) {
        next if $fd <= 2 || (defined $keep && $fd == $keep);
        my $target = readlink "/proc/self/fd/$fd" // '';
        next unless $target =~ m{^/dev/(ttyS|hvc|virtio-ports/)};
        POSIX::close($fd);
    }
}

sub drop_to_builder {
    my @pw = getpwnam('builder');
    exit 1 unless @pw;
    my $uid = $pw[2];
    my $gid = $pw[3];
    # 157 is prctl. 38 is PR_SET_NO_NEW_PRIVS.
    syscall(157, 38, 1, 0, 0, 0);
    $) = "$gid $gid";
    $( = $gid;
    $> = $uid;
    $< = $uid;
    exit 1 if $> != $uid;
}

sub status_line {
    return undef unless $status;
    if ($statusBuf =~ s/\A([^\n]*)\r?\n//) {
        return $1;
    }
    my $chunk;
    my $n = sysread $status, $chunk, 512;
    if (defined $n && $n > 0) {
        $statusBuf .= $chunk;
    }
    if ($statusBuf =~ s/\A([^\n]*)\r?\n//) {
        return $1;
    }
    return undef;
}

sub syswrite_all {
    my ($fh, $data) = @_;
    my $off = 0;
    while ($off < length($data)) {
        my $n = syswrite $fh, $data, length($data) - $off, $off;
        if (!defined $n) {
            if ($!{EAGAIN} || $!{EWOULDBLOCK}) {
                select undef, undef, undef, 0.05;
                next;
            }
            return 0;
        }
        return 0 if $n == 0;
        $off += $n;
    }
    return 1;
}
