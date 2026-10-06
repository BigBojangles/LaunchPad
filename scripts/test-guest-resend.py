#!/usr/bin/env python3
"""Exercise the actual staged Perl receiver on owned Linux fixtures."""
import pathlib
import hashlib
import json
import subprocess
import sys
import tempfile

repair = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
helper = repair[repair.index("sub receive_pause_files {"):repair.index("sub pump {")]
merge = "sub apply_import_batch {" in helper
if merge:
    helper += repair[repair.index("sub pump {"):repair.index("sub rewrite_origin {")]
    helper += repair[repair.index("sub copy_exact {"):repair.index("sub walk_project {")]
with tempfile.TemporaryDirectory(prefix="launchpad-resend-") as directory:
    root = pathlib.Path(directory)
    project = root / "project"
    project.mkdir()
    harness = root / "receive.pl"
    harness.write_text("use strict; use warnings; use IO::Handle; use File::Temp qw(tempfile tempdir); "
                       "use Digest::SHA; use JSON::PP; use Fcntl qw(O_RDONLY O_NOFOLLOW);\n"
                       + "my $dir = $ARGV[0]; my @got=@{decode_json($ARGV[1])}; binmode STDIN; receive_pause_files(\\*STDIN);\n" + helper)

    def run(body, success):
        known = [[p.relative_to(project).as_posix(), p.stat().st_size, 0, 0, hashlib.sha256(p.read_bytes()).hexdigest()]
                 for p in project.rglob("*") if p.is_file() and not p.is_symlink() and "linked-parent" not in p.parts]
        result = subprocess.run(["perl", str(harness), str(project), json.dumps(known)], input=body,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10)
        assert (result.returncode == 0) == success, result.stderr.decode()

    run(b"FILE 3 new/nested/a.txt\nnewFILE 0 empty.txt\n", True)
    assert (project / "new/nested/a.txt").read_bytes() == b"new"
    assert (project / "empty.txt").read_bytes() == b""
    run(b"FILE 6 new/nested/a.txt\nshort", False)
    assert (project / "new/nested/a.txt").read_bytes() == b"new"
    outside = root / "outside.txt"
    outside.write_bytes(b"preserve")
    (project / "link.txt").symlink_to(outside)
    run(b"FILE 3 link.txt\nbad", False)
    assert outside.read_bytes() == b"preserve"
    (project / "linked-parent").symlink_to(root, target_is_directory=True)
    run(b"FILE 3 linked-parent/outside.txt\nbad", False)
    assert outside.read_bytes() == b"preserve"
    run(b"FILE 3 ../outside.txt\nbad", False)
    run(b"FILE 3 new/nested/a.txt", False)
    run(b"AUTH 3\nbad", False)
    # Replacing a hardlink writes a new inode, preserving its unrelated peer.
    import os
    os.link(outside, project / "hardlink.txt")
    run(b"FILE 3 hardlink.txt\nnew", True)
    assert outside.read_bytes() == b"preserve"
    assert (project / "hardlink.txt").read_bytes() == b"new"
    assert not list(project.rglob(".launchpad-send-*"))
print("PASS: nested/empty import, truncated body/header, links, traversal, unexpected headers, hardlink preservation.")
