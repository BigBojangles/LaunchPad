"""Execute production preflight blocks with synthetic package-version queries.

Real dpkg version comparison, no installation or privileged guest/host writes.
These tests do not claim the hypothetical newer Perl/procps versions are installed.
"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

REPOSITORY = Path(__file__).resolve().parents[2]


class PackagePreflight(unittest.TestCase):
    def run_block(self, filename, start, end, versions, output):
        source=(REPOSITORY/'scripts'/filename).read_text()
        block=source.split(start,1)[1].split(end,1)[0]
        with tempfile.TemporaryDirectory(prefix='launchpad-owned-package-plan-') as location:
            root=Path(location)
            query=root/'dpkg-query'
            query.write_text('#!/bin/sh\ncase "$*" in\n*libproc2-0) printf "%s" "$OWNED_LIBPROC";;\n*procps) printf "%s" "$OWNED_PROCPS";;\n*perl-base) printf "%s" "$OWNED_PERL_BASE";;\n*perl-modules-5.36) printf "%s" "$OWNED_PERL_MODULES";;\n*libperl5.36) printf "%s" "$OWNED_LIBPERL";;\n*perl) printf "%s" "$OWNED_PERL";;\n*) exit 2;;\nesac\n')
            query.chmod(0o755)
            environment=dict(os.environ,PATH=str(root)+os.pathsep+os.environ['PATH'],**versions)
            return subprocess.run(['sh','-c','set -eu\n'+start+block+'\n'+output],env=environment,text=True,capture_output=True,timeout=10)

    def procps(self, procps, library):
        return self.run_block('guest-maintenance-apply.sh','procps_version=', 'if [ -d security-packages ]; then',
            dict(OWNED_PROCPS=procps,OWNED_LIBPROC=library),'printf "%s" "$procps_install"')

    def perl(self, versions):
        return self.run_block('guest-security-packages-apply.sh','perl_lower=0','test ! -L /usr/local/lib/node_modules',
            dict(zip(('OWNED_PERL_BASE','OWNED_PERL_MODULES','OWNED_LIBPERL','OWNED_PERL'),versions)),
            'printf "%s:%s" "$perl_lower" "$perl_ready"')

    def test_absent_procps_pair_requires_installation(self):
        result=self.procps('','')
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertEqual('true',result.stdout)

    def test_equal_and_synthetic_newer_procps_pairs_are_preserved(self):
        if not Path('/usr/bin/ps').is_file() or not Path('/usr/lib/x86_64-linux-gnu/libproc2.so.0').exists():
            self.skipTest('Host fixture prerequisites unavailable; no preserve-path pass claimed')
        for version in ('2:4.0.2-3','2:4.0.4-9'):
            with self.subTest(version=version):
                result=self.procps(version,version)
                self.assertEqual(0,result.returncode,result.stderr)
                self.assertEqual('false',result.stdout)

    def test_partial_or_mixed_procps_pair_is_refused(self):
        for versions in (('2:4.0.2-3',''),('','2:4.0.2-3'),('2:4.0.4-9','2:4.0.2-3')):
            with self.subTest(versions=versions):
                result=self.procps(*versions)
                self.assertNotEqual(0,result.returncode)
                self.assertEqual('',result.stdout)

    def test_coherent_older_perl_family_requires_all_four_updates(self):
        result=self.perl(['5.36.0-7+deb12u3']*4)
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertEqual('4:0',result.stdout)

    def test_equal_and_synthetic_newer_perl_families_are_preserved(self):
        for version in ('5.36.0-7+deb12u4','5.36.0-7+deb12u5'):
            with self.subTest(version=version):
                result=self.perl([version]*4)
                self.assertEqual(0,result.returncode,result.stderr)
                self.assertEqual('0:4',result.stdout)

    def test_mixed_perl_versions_are_refused_even_when_each_is_newer(self):
        for versions in (['5.36.0-7+deb12u3']*3+['5.36.0-7+deb12u4'],['5.36.0-7+deb12u4']*3+['5.36.0-7+deb12u5']):
            with self.subTest(versions=versions):
                result=self.perl(versions)
                self.assertNotEqual(0,result.returncode)
                self.assertEqual('',result.stdout)


if __name__=='__main__': unittest.main()
