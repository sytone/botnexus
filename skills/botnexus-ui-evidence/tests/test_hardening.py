"""Synthetic security regressions. No container, gateway or browser execution."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import shutil
import unittest
from unittest import mock

import test_focused_sidebar as baseline
from test_focused_sidebar import ROOT, powershell

SNAPSHOT = ROOT / 'snapshot.py'


class OutputHardening(baseline.FocusedSidebarContract):
    def test_extra_file_directory_and_link_rejected(self):
        self.validate()
        for name in ('config.json', 'gateway.log', '.hidden'):
            with self.subTest(name=name):
                extra = self.root / name
                extra.write_text('synthetic sensitive artifact')
                self.validate(False)
                extra.unlink()
        extra = self.root / 'unexpected-directory'
        extra.mkdir()
        self.validate(False)
        extra.rmdir()

    def test_link_rejected_even_when_name_and_hash_match(self):
        self.validate()
        image = self.root / self.captures[0]['screenshot']['path']
        outside = self.root.parent / (self.root.name + '-outside.png')
        outside.write_bytes(image.read_bytes())
        self.addCleanup(outside.unlink)
        image.unlink()
        # NTFS junction is also tested where unprivileged symlinks are unavailable.
        try:
            image.symlink_to(outside)
        except OSError:
            subprocess.run(['cmd', '/c', 'mklink', '/J', str(image), str(outside.parent)], check=True, capture_output=True)
        self.validate(False)

    def test_stale_digest_is_rejected(self):
        self.validate()
        result = powershell("Test-UiEvidenceOutput -OutputDirectory '" + str(self.root) + "' -ExpectedTree " + 'c' * 64)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('source context digest mismatch', result.stderr.lower())


class SnapshotHardening(unittest.TestCase):
    def setUp(self):
        self.area = tempfile.TemporaryDirectory(prefix='evidence-snapshot-fixture-')
        self.addCleanup(self.area.cleanup)
        self.repo = Path(self.area.name) / 'repo'
        self.repo.mkdir()
        self.destination = Path(self.area.name) / 'context'
        self.git('init', '-q')
        self.git('config', 'core.autocrlf', 'false')
        self.git('config', 'core.symlinks', 'false')
        self.git('config', 'user.name', 'Synthetic Fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        (self.repo / '.gitignore').write_text('*.env\nignored/\n')
        (self.repo / 'source.cs').write_bytes(b'committed source\n')
        self.git('add', '.')
        self.git('commit', '-qm', 'test: fixture')
        self.head = self.git('rev-parse', 'HEAD').strip()

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.repo), *args], text=True)

    def snapshot(self):
        result = subprocess.run(['python', '-B', str(SNAPSHOT), '--repository', str(self.repo), '--destination', str(self.destination), '--expected-commit', self.head], capture_output=True, text=True, timeout=30)
        return result

    def test_ignored_secrets_never_enter_context(self):
        (self.repo / 'provider.env').write_text('SYNTHETIC_SECRET_DO_NOT_COPY')
        (self.repo / 'ignored').mkdir()
        (self.repo / 'ignored/config.json').write_text('SYNTHETIC_SECRET_DO_NOT_COPY')
        result = self.snapshot()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.destination / 'source/source.cs').read_bytes(), b'committed source\n')
        self.assertFalse((self.destination / 'source/provider.env').exists())
        self.assertFalse((self.destination / 'source/ignored').exists())
        packet = json.loads(result.stdout)
        self.assertEqual(packet['commit'], self.head)
        self.assertEqual(len(packet['tree']), 64)

    def test_dirty_and_untracked_rejected(self):
        for name in ('source.cs', 'untracked.txt'):
            with self.subTest(name=name):
                (self.repo / name).write_text('changed')
                result = self.snapshot()
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.destination.exists())
                if name == 'source.cs':
                    self.git('checkout', '--', name)
                else:
                    (self.repo / name).unlink()

    def test_tracked_secret_rejected(self):
        (self.repo / 'auth.json').write_text('synthetic credential')
        self.git('add', 'auth.json')
        self.git('commit', '-qm', 'test: forbidden credential fixture')
        self.head = self.git('rev-parse', 'HEAD').strip()
        result = self.snapshot()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('secret', result.stderr.lower())

    def test_tracked_symlink_rejected(self):
        blob = subprocess.check_output(['git', '-C', str(self.repo), 'hash-object', '-w', '--stdin'], input=b'source.cs')
        self.git('update-index', '--add', '--cacheinfo', '120000', blob.decode().strip(), 'link.cs')
        self.git('commit', '-qm', 'test: synthetic link')
        (self.repo / 'link.cs').write_text('source.cs')
        self.head = self.git('rev-parse', 'HEAD').strip()
        result = self.snapshot()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('links/submodules', result.stderr)

    def test_actual_docker_digest_algorithm_matches(self):
        bash = shutil.which('bash')
        if not bash:
            self.fail('Bash required to test the Dockerfile sha256sum algorithm.')
        result = self.snapshot()
        self.assertEqual(result.returncode, 0, result.stderr)
        packet = json.loads(result.stdout)
        # Execute only native hashing over synthetic files, never the Dockerfile or runtime.
        command = "find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1"
        check = subprocess.run([bash, '-c', command], cwd=self.destination / 'source', capture_output=True, text=True)
        self.assertEqual(check.returncode, 0, check.stderr)
        self.assertEqual(check.stdout.strip(), packet['tree'])

    def test_concurrent_head_change_rejected(self):
        spec = importlib.util.spec_from_file_location('snapshot_fixture', SNAPSHOT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        original = module.git
        reads = 0
        def changed_git(repo, *args):
            nonlocal reads
            result = original(repo, *args)
            if args[:2] == ('rev-parse', 'HEAD'):
                reads += 1
                if reads > 1:
                    return b'f' * 40 + b'\n'
            return result
        with mock.patch.object(module, 'git', changed_git):
            with self.assertRaisesRegex(ValueError, 'changed'):
                module.create_snapshot(self.repo, self.destination, self.head)
        self.assertFalse(self.destination.exists())

    def test_concurrent_dirty_change_rejected(self):
        spec = importlib.util.spec_from_file_location('snapshot_dirty_fixture', SNAPSHOT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        original = module.git
        def changed_git(repo, *args):
            result = original(repo, *args)
            if args[-3:-1] == ('archive', '--format=tar'):
                (self.repo / 'source.cs').write_text('concurrent writer')
            return result
        with mock.patch.object(module, 'git', changed_git):
            with self.assertRaisesRegex(ValueError, 'Dirty'):
                module.create_snapshot(self.repo, self.destination, self.head)
        self.assertFalse(self.destination.exists())

    def test_prebuild_digest_matches_node_and_rejects_mutation(self):
        result = self.snapshot()
        self.assertEqual(result.returncode, 0, result.stderr)
        packet = json.loads(result.stdout)
        checker = ROOT / 'container/context-digest.mjs'
        for expected in (packet['tree'], 'c' * 64):
            check = subprocess.run(['node', str(checker), str(self.destination / 'source'), expected], capture_output=True, text=True)
            self.assertEqual(check.returncode == 0, expected == packet['tree'], check.stderr)
        (self.destination / 'source/source.cs').write_text('changed after export')
        check = subprocess.run(['node', str(checker), str(self.destination / 'source'), packet['tree']], capture_output=True, text=True)
        self.assertNotEqual(check.returncode, 0)


if __name__ == '__main__':
    unittest.main(verbosity=2)
