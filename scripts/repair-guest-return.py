#!/usr/bin/env python3
"""Stage D08 on the reviewed safe-import script; never edit a live template."""
import hashlib
import pathlib
import sys

source, target = map(pathlib.Path, sys.argv[1:3])
data = source.read_bytes()
if hashlib.sha256(data).hexdigest() != "83b51dfa61668399d0519d25cf80593f42a5d388be77e158386bb54b7ada6313":
    raise SystemExit("Safe-import input changed; inspect before staging this repair.")
text = data.decode("utf-8")
text = text.replace("use File::Temp qw(tempfile);", "use File::Temp qw(tempfile tempdir);\nuse Digest::SHA;\nuse Fcntl qw(O_RDONLY O_NOFOLLOW);")
before = """    pump($fh, $out, $size);
    close $out;
    chmod 0644, $path;"""
after = """    my $digest = Digest::SHA->new(256);
    die "Incomplete project import" if pump($fh, $out, $size, $digest) != 0;
    close($out) or die "Close project import: $!";
    chmod 0644, $path;"""
if text.count(before) != 1:
    raise SystemExit("Unexpected import branch; no output written.")
text = text.replace(before, after)
text = text.replace("push @got, [ $name, (-s $path) + 0, $mode, $mtime ];",
                    "push @got, [ $name, (-s $path) + 0, $mode, $mtime, $digest->hexdigest ];")
text = text.replace("my ($in, $out, $left) = @_;", "my ($in, $out, $left, $digest) = @_;")
text = text.replace("        print {$out} $buf if defined $out;",
                    "        die \"Import write: $!\" if defined $out && !print {$out} $buf;\n        $digest->add($buf) if defined $digest;")
start = text.index("sub send_project_changes {")
end = text.index("sub report_guest_size {", start)
text = text[:start] + r'''
# Only the parent opens the device. File discovery and export use builder rights.
sub send_project_changes {
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

''' + text[end:]
if target.exists():
    raise SystemExit("Output exists; choose a new generated path.")
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(text.encode("utf-8"))
print("Staged return repair SHA256:", hashlib.sha256(target.read_bytes()).hexdigest())
