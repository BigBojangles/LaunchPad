#!/usr/bin/env python3
"""Exercise actual merge helpers with a durable fixture baseline across calls."""
import json
import pathlib
import subprocess
import sys
import tempfile

repair = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
helpers = repair[repair.index("sub receive_pause_files {"):repair.index("sub rewrite_origin {")]
helpers += repair[repair.index("sub copy_exact {"):repair.index("sub walk_project {")]
with tempfile.TemporaryDirectory(prefix="launchpad-reopen-") as directory:
    root = pathlib.Path(directory)
    project = root / "project"
    project.mkdir()
    baseline = root / "baseline.json"
    baseline.write_text("[]")
    harness = root / "merge.pl"
    harness.write_text("use strict; use warnings; use IO::Handle; use File::Temp qw(tempfile tempdir); "
                       "use Digest::SHA; use Fcntl qw(O_RDONLY O_NOFOLLOW); use JSON::PP;\n"
                       "my $dir=$ARGV[0]; my @got=@{decode_json($ARGV[1])}; binmode STDIN;\n"
                       + helpers + "\nreceive_pause_files(\\*STDIN); print encode_json(\\@got);\n")

    def run(body, success=True):
        previous = baseline.read_text()
        result = subprocess.run(["perl", str(harness), str(project), previous], input=body,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10)
        assert (result.returncode == 0) == success, result.stderr.decode()
        if success:
            json.loads(result.stdout)
            baseline.write_bytes(result.stdout)
        else:
            assert baseline.read_text() == previous

    run(b"FILE 3 a.txt\noldFILE 3 b.txt\nold")
    (project / "a.txt").write_bytes(b"vm!")
    run(b"FILE 3 b.txt\nnew")
    assert (project / "a.txt").read_bytes() == b"vm!"
    assert (project / "b.txt").read_bytes() == b"new"
    # An earlier valid file cannot be applied before a later conflict is found.
    run(b"FILE 4 earlier.txt\nsafeFILE 3 a.txt\nwin", False)
    assert not (project / "earlier.txt").exists()
    assert (project / "a.txt").read_bytes() == b"vm!"
    # Identical content converges safely and becomes the new shared baseline.
    run(b"FILE 3 a.txt\nvm!")
    run(b"FILE 3 a.txt\nwin")
    assert (project / "a.txt").read_bytes() == b"win"
    (project / "b.txt").unlink()
    run(b"FILE 3 b.txt\nnew", False)
    assert not (project / "b.txt").exists()
    run(b"FILE 4 earlier.txt\nsafeFILE 9 broken.txt\nshort", False)
    assert not (project / "earlier.txt").exists()
    run(b"FILE 1 duplicate.txt\naFILE 1 duplicate.txt\nb", False)
    assert not (project / "duplicate.txt").exists()
    (project / "legacy.txt").write_bytes(b"preserve")
    run(b"FILE 3 legacy.txt\nnew", False)
    assert (project / "legacy.txt").read_bytes() == b"preserve"
    run(b"FILE 8 legacy.txt\npreserve")
    run(b"FILE 3 legacy.txt\nnew")
    assert (project / "legacy.txt").read_bytes() == b"new"
print("PASS: persistent shared hashes, guest-only preservation, host-only updates, full-batch conflict/truncation/duplicate refusal, convergence, guest deletion and unknown legacy data preservation.")
