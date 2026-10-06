#!/usr/bin/env python3
"""Stage conflict-safe initial/resend imports on the reviewed return candidate."""
import hashlib
import pathlib
import sys

source, target = map(pathlib.Path, sys.argv[1:3])
data = source.read_bytes()
if hashlib.sha256(data).hexdigest() != "93b04f77c8956256218d9956bda1ba7cdc09531acd1c78917b79b76b00037fd6":
    raise SystemExit("Return candidate changed; inspect before staging.")
text = data.decode("utf-8").replace("use Digest::SHA;", "use Digest::SHA;\nuse JSON::PP qw(encode_json decode_json);")
text = text.replace("my @got;", """my $baselineRoot = '/var/lib/launchpad';
my $baselinePath = "$baselineRoot/import.json";
my @got = load_import_baseline();
my $initialStaging = tempdir('launchpad-import-XXXXXXXX', TMPDIR => 1, CLEANUP => 1);
own_builder($initialStaging);
my @incoming;
mark('IMPORT-READY safe-merge');""")
start = text.index("    $name =~ s{\\\\}{/}g;")
end = text.index("system('chown', '-R', 'builder:builder', $dir);", start)
text = text[:start] + r'''
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
''' + text[end:]
text = text.replace("mark('DOOR-READY safe-import');", "mark('DOOR-READY safe-import safe-merge');")
start = text.index("sub read_pause_files {")
end = text.index("sub pump {", start)
text = text[:start] + r'''
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

''' + text[end:]
if target.exists():
    raise SystemExit("Output exists; choose a new private path.")
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(text.encode("utf-8"))
print("Staged safe-reopen SHA256:", hashlib.sha256(target.read_bytes()).hexdigest())
