#!/usr/bin/env python3
"""Exercise the staged guest exporter, including incomplete snapshot failures."""
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile

repair = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
helper = repair[repair.index("sub export_project {"):repair.index("sub report_guest_size {")]
writer = repair[repair.index("sub syswrite_all {"):]
with tempfile.TemporaryDirectory(prefix="launchpad-return-test-") as directory:
    root = pathlib.Path(directory)
    project = root / "project"
    project.mkdir()
    values = {"edit.txt": b"old", "unchanged.txt": b"keep", "binary.dat": b"\x00\xffold"}
    for name, body in values.items():
        (project / name).write_bytes(body)
    baseline = [[name, len(body), 0, 0, hashlib.sha256(body).hexdigest()] for name, body in values.items()]
    stamp = (project / "edit.txt").stat().st_mtime_ns
    (project / "edit.txt").write_bytes(b"new")
    os.utime(project / "edit.txt", ns=(stamp, stamp))
    os.utime(project / "unchanged.txt", None)
    (project / "binary.dat").write_bytes(b"\x00\xffnew")
    (project / "empty.txt").write_bytes(b"")
    (project / "large.dat").write_bytes(b"x" * 131073)
    for metadata in [".git/config", "nested/.GIT/config"]:
        path = project / metadata
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"VM-only metadata")
    (project / "worktree").mkdir()
    (project / "worktree/.git").write_bytes(b"gitdir: somewhere")
    harness = root / "export.pl"

    def run(hook="", success=True):
        harness.write_text("use strict; use warnings; use IO::Handle; use File::Temp qw(tempfile tempdir); "
                           "use Digest::SHA; use Fcntl qw(O_RDONLY O_NOFOLLOW); use JSON::PP;\n"
                           "my $dir=$ARGV[0]; my @got=@{decode_json($ARGV[1])};\n"
                           + helper + writer + hook
                           + "\nbinmode STDOUT; my $ok=eval { export_project(\\*STDOUT); 1; }; "
                           "print STDERR $@ unless $ok; exit($ok ? 0 : 1);\n")
        result = subprocess.run(["perl", str(harness), str(project), json.dumps(baseline)],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15)
        assert (result.returncode == 0) == success, result.stderr.decode()
        return result.stdout

    def parse(wire):
        header, wire = wire.split(b"\n", 1)
        count = int(header.split()[1])
        files = {}
        for _ in range(count):
            header, wire = wire.split(b"\n", 1)
            kind, size, name = header.split(b" ", 2)
            assert kind == b"FILE"
            size = int(size)
            files[name.decode()] = wire[:size]
            wire = wire[size:]
        assert wire == b"PROJECT-END\n", wire
        return files

    expected = {"edit.txt": b"new", "binary.dat": b"\x00\xffnew", "empty.txt": b"", "large.dat": b"x" * 131073}
    assert parse(run()) == expected
    # Discovery must not declare a successful batch if a file cannot be opened.
    hook = r'''
my $original_digest = \&project_digest;
{ no warnings 'redefine'; *project_digest = sub {
    my @result = $original_digest->(@_);
    unlink($_[0]) if $_[0] =~ m{/edit\.txt$} && !defined $_[1];
    return @result;
}; }
'''
    assert run(hook, False) == b""
    (project / "edit.txt").write_bytes(b"new")
    # Files are sent from snapshots even if the original changes afterwards.
    hook = r'''
my $original_walk = \&walk_project;
{ no warnings 'redefine'; *walk_project = sub {
    $original_walk->(@_);
    if ($_[0] eq $dir) {
        open my $out, '>', "$dir/edit.txt" or die $!;
        print {$out} 'later'; close $out;
    }
}; }
'''
    assert parse(run(hook)) == expected
    (project / "edit.txt").write_bytes(b"new")
    # Shortened staging state cannot emit the completion marker.
    hook = r'''
my $original_walk = \&walk_project;
{ no warnings 'redefine'; *walk_project = sub {
    $original_walk->(@_);
    truncate($_[2]->[0]->[2], 0) if $_[0] eq $dir;
}; }
'''
    assert b"PROJECT-END" not in run(hook, False)
    # A failed channel write cannot emit the completion marker.
    hook = "{ no warnings 'redefine'; *syswrite_all = sub { return 0; }; }"
    assert run(hook, False) == b""
print("PASS: content edits with restored timestamps, unchanged files, binary/empty/large files, nested Git exclusions, immutable snapshots and incomplete/read/write failures.")
