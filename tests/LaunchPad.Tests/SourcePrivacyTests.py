"""Focused privacy gate checks; only disposable project-local test files."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.parse import quote

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('privacy', REPO/'scripts/verify-source-privacy.py')
privacy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(privacy)

class SourcePrivacy(unittest.TestCase):
    def setUp(self):
        self.allowed = privacy.reviewed_examples()
        self.profile = 'C:' + chr(92) + 'Users' + chr(92) + 'Example Profile'
        self.email = 'private.sender' + '@' + 'gmail.com'

    def test_profile_literals_escaped_forward_slash_encoded_and_case(self):
        variants = (self.profile, json.dumps(self.profile), self.profile.replace(chr(92), '/'),
                    self.profile.replace(chr(92), chr(92)+'u005c'),
                    quote(self.profile, safe=''), self.profile.lower())
        for text in variants:
            with self.subTest(text=text):
                self.assertTrue(privacy.violations('sample.cs', text.encode(), self.allowed))

    def test_binary_metadata_and_unicode_encodings_are_scanned(self):
        for encoding in ('utf-8', 'utf-16-le', 'utf-16-be'):
            with self.subTest(encoding=encoding):
                self.assertTrue(privacy.violations('image.bin', b'\x00'+self.profile.encode(encoding), self.allowed))

    def test_personal_email_on_any_domain_is_rejected(self):
        for domain in ('gmail.com', 'outlook.com', 'mailbox.org', 'private-company.tech'):
            address = 'private.sender'+'@'+domain
            self.assertTrue(privacy.violations('sample.txt', address.encode(), self.allowed))
        for address in ('"private.sender"'+'@'+'gmail.com', 'prívate.sender'+'@'+'mailbox.org',
                        self.email.replace('@', chr(92)+'u0040')):
            self.assertTrue(privacy.violations('sample.txt', address.encode(), self.allowed))

    def test_reserved_test_domains_and_general_paths_are_allowed(self):
        for domain in ('example.com', 'example.net', 'example.org', 'company.invalid', 'company.test'):
            self.assertFalse(privacy.violations('sample.txt', ('fixture'+'@'+domain).encode(), self.allowed))
        self.assertFalse(privacy.violations('sample.cs', b'Path.Combine(testRoot, "Folder With Spaces")', self.allowed))

    def test_reviewed_email_is_allowed_only_in_its_exact_file(self):
        address = 'user'+'@'+'gmail.com'
        self.assertFalse(privacy.violations('tests/LaunchPad.Tests/EmailProviderPresetsTests.cs', address.encode(), self.allowed))
        self.assertTrue(privacy.violations('src/another.cs', address.encode(), self.allowed))

    def test_working_contents_are_checked_and_git_failures_are_closed(self):
        with tempfile.TemporaryDirectory(dir=REPO/'tests/LaunchPad.Tests/TestResults', prefix='privacy-owned-') as folder:
            root = Path(folder)
            (root/'sample.txt').write_text(self.email)
            outputs = [subprocess.CompletedProcess([], 0, str(root).encode(), b''),
                       subprocess.CompletedProcess([], 0, b'sample.txt\0', b'')]
            with patch.object(privacy.subprocess, 'run', side_effect=outputs):
                count, findings = privacy.scan(root)
            self.assertEqual(1, count); self.assertTrue(findings)
            with patch.object(privacy.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, b'', b'failure')):
                with self.assertRaises(ValueError): privacy.scan(root)

    def test_guard_does_not_read_history_and_requires_the_correct_root(self):
        with patch.object(privacy.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, str(REPO.parent).encode(), b'')):
            with self.assertRaises(ValueError): privacy.scan(REPO)
        source = (REPO/'scripts/verify-source-privacy.py').read_text()
        self.assertNotIn("'rev-list'", source)
        self.assertNotIn("'cat-file'", source)

if __name__ == '__main__': unittest.main()
