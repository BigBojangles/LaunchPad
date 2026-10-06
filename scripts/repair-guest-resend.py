#!/usr/bin/env python3
"""Stage the reviewed D06 repair without changing a live backing image."""
import hashlib
import pathlib
import sys

source, target = map(pathlib.Path, sys.argv[1:3])
data = source.read_bytes()
if hashlib.sha256(data).hexdigest() != "abc5d2e8708ab68ea13eb287799bfc451f87ccf5feee561425f4fdfbda6a27ff":
    raise SystemExit("Preparation input changed; inspect before staging this repair.")
text = data.decode("utf-8")
text = text.replace("use IO::Select;", "use IO::Select;\nuse File::Temp qw(tempfile);")
before = """        mark('DOOR-OPEN');
        if ($child > 0) {
            kill 'TERM', $child;
            waitpid $child, 0;
            $child = 0;
        }
        read_pause_files();"""
after = """        if ($child > 0) {
            kill 'TERM', $child;
            waitpid $child, 0;
            $child = 0;
        }
        mark('DOOR-OPEN safe-import');
        unless (read_pause_files()) {
            mark('DOOR-FAILED');
            last;
        }"""
if text.count(before) != 1:
    raise SystemExit("Unexpected pause branch; no output written.")
text = text.replace(before, after)
text = text.replace("mark('DOOR-READY');", "mark('DOOR-READY safe-import');")
start = text.index("sub read_pause_files {")
end = text.index("sub pump {", start)
text = text[:start] + r'''
# The privileged parent keeps control/status; a builder child only imports files.
sub read_pause_files {
    my $in;
    sysopen($in, '/dev/virtio-ports/fence', O_RDWR) or return 0;
    binmode $in;
    my $receiver = fork();
    unless (defined $receiver) { close $in; return 0; }
    if ($receiver == 0) {
        close_guest_devices(fileno($in));
        open STDIN, '<', '/dev/null' or exit 1;
        open STDOUT, '>', '/dev/null' or exit 1;
        open STDERR, '>', '/dev/null' or exit 1;
        drop_to_builder();
        my $ok = eval { receive_pause_files($in); 1; };
        close $in;
        exit($ok ? 0 : 1);
    }
    close $in;
    waitpid $receiver, 0;
    return $? == 0;
}

sub receive_pause_files {
    my ($in) = @_;
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
        die "Unsafe file path" if $name =~ /[\x00-\x1f\\:]/ || $name =~ m{^/};
        my @parts = split m{/}, $name, -1;
        die "Unsafe file component" if grep { $_ eq '' || $_ eq '.' || $_ eq '..' } @parts;
        # Reject links in both the destination ancestors and filename. New files
        # are staged beside the target, never opened through an existing link.
        my $parent = '';
        my @parents = (split(m{/}, $root), @parts[0 .. $#parts - 1]);
        for my $part (@parents) {
            next if $part eq '';
            $parent .= '/' . $part;
            die "Linked parent" if -l $parent;
            mkdir($parent, 0755) or die "Create parent: $!" unless -d $parent;
        }
        my $path = "$parent/$parts[-1]";
        die "Linked destination" if -l $path;
        die "Non-file destination" if -e $path && !-f $path;
        my ($out, $temporary) = tempfile('.launchpad-send-XXXXXXXX', DIR => $parent, UNLINK => 1);
        binmode $out;
        my $left = $size;
        while ($left > 0) {
            my $buf;
            my $n = read($in, $buf, $left > 65536 ? 65536 : $left);
            die "Truncated file" unless defined $n && $n > 0;
            print {$out} $buf or die "Write file: $!";
            $left -= $n;
        }
        $out->flush or die "Flush file: $!";
        $out->sync or die "Sync file: $!";
        chmod($mode, $temporary) or die "File mode: $!";
        close($out) or die "Close file: $!";
        rename($temporary, $path) or die "Install file: $!";
    }
}

''' + text[end:]
text = text.replace("sub close_guest_devices {\n", "sub close_guest_devices {\n    my ($keep) = @_;\n")
text = text.replace("        next if $fd <= 2;", "        next if $fd <= 2 || (defined $keep && $fd == $keep);")
if target.exists():
    raise SystemExit("Output exists; choose a new generated path.")
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(text.encode("utf-8"))
print("Staged resend repair SHA256:", hashlib.sha256(target.read_bytes()).hexdigest())
