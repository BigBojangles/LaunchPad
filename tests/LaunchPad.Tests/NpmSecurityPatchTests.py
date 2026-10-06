"""Offline malicious-input checks for the privileged dependency patch helper."""
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest

REPOSITORY = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('npm_patch', REPOSITORY / 'scripts/npm-security-patches.py')
patch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(patch)


class MaliciousPatchInputs(unittest.TestCase):
    def archive(self, root, entries):
        path = root / 'owned-fixture.tgz'
        with tarfile.open(path, 'w:gz') as output:
            for name, data, kind in entries:
                member = tarfile.TarInfo(name)
                member.type = kind
                member.size = len(data) if kind == tarfile.REGTYPE else 0
                member.linkname = '/owned-forbidden-system-canary'
                output.addfile(member, io.BytesIO(data) if member.isfile() else None)
        return path

    def test_path_escape_is_rejected_without_extracting(self):
        with tempfile.TemporaryDirectory(prefix='launchpad-owned-patch-') as location:
            root = Path(location)
            archive = self.archive(root, [('package/../../escaped', b'x', tarfile.REGTYPE)])
            with self.assertRaises(ValueError): patch.files_in_archive(archive, 'package/')
            self.assertEqual([archive], list(root.iterdir()))

    def test_link_to_system_and_special_device_are_rejected(self):
        for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.CHRTYPE):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory(prefix='launchpad-owned-patch-') as location:
                root = Path(location)
                archive = self.archive(root, [('package/package.json', b'{}', tarfile.REGTYPE), ('package/canary', b'', kind)])
                with self.assertRaises(ValueError): patch.files_in_archive(archive, 'package/')
                self.assertEqual([archive], list(root.iterdir()))

    def test_duplicate_member_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix='launchpad-owned-patch-') as location:
            archive = self.archive(Path(location), [('package/package.json', b'{}', tarfile.REGTYPE)] * 2)
            with self.assertRaises(ValueError): patch.files_in_archive(archive, 'package/')

    def test_changed_archive_fails_before_package_metadata_or_application(self):
        with tempfile.TemporaryDirectory(prefix='launchpad-owned-patch-') as location:
            root = Path(location)
            (root / 'patch.tgz').write_bytes(b'tampered')
            with self.assertRaisesRegex(ValueError, 'SHA256'):
                patch.verify_archive(root, dict(file='patch.tgz', sha256='0'*64, integrity='sha512-invalid'))

    def test_modification_and_execute_permission_change_tree_identity(self):
        before = {'package.json': (b'{}', 0), 'index.js': (b'owned', 0)}
        modified = dict(before, **{'index.js': (b'changed', 0)})
        executable = dict(before, **{'index.js': (b'owned', 0o111)})
        self.assertNotEqual(patch.tree_digest(before), patch.tree_digest(modified))
        self.assertNotEqual(patch.tree_digest(before), patch.tree_digest(executable))


if __name__ == '__main__': unittest.main()
