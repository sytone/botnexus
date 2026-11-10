"""Synthetic host-contract tests only; never invoke a real runtime or browser."""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / 'BotNexusUiEvidence.psm1'


def powershell(body):
    return subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command',
                           "$ErrorActionPreference = 'Stop'; Import-Module '" + str(MODULE) + "' -Force; " + body],
                          capture_output=True, text=True, timeout=30)


class FocusedSidebarContract(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='focused-sidebar-fixture-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        png = b'\x89PNG\r\n\x1a\nsynthetic contract fixture, not browser evidence'
        self.captures = []
        for theme in ('light', 'dark'):
            for width, height in ((1440, 900), (600, 844)):
                for section in ('chat', 'agents'):
                    for state in ('populated', 'scrolled'):
                        name = f'{theme}-{width}x{height}-{section}-{state}'
                        (self.root / (name + '.png')).write_bytes(png)
                        owner = '.focused-chat-scroll' if section == 'chat' else '[data-testid="focused-section-content"]'
                        metadata = {'theme': theme, 'section': section, 'viewport': {'width': width, 'height': height},
                                    'scrollOwner': {'selector': owner, 'geometry': {'scrollTop': 0 if state == 'populated' else 100,
                                                                                 'scrollHeight': 2000, 'clientHeight': 500}}}
                        sidecar = json.dumps(metadata).encode()
                        (self.root / (name + '.json')).write_bytes(sidecar)
                        targets = ['overflow', 'fixed-picker', 'fixed-document', 'outer-owner', 'populated', 'theme', 'desktop-route', 'no-browser-errors']
                        if section == 'chat':
                            targets += ['fixed-controls', 'peer-groups', 'custom-member', 'built-in-menus']
                        if state == 'scrolled':
                            targets += ['wheel-moved', 'keyboard-moved', 'keyboard-focus']
                        self.captures.append({'name': name, 'viewport': metadata['viewport'],
                                              'screenshot': {'path': name + '.png', 'sha256': hashlib.sha256(png).hexdigest()},
                                              'metadata': {'path': name + '.json', 'sha256': hashlib.sha256(sidecar).hexdigest()},
                                              'assertions': [{'kind': 'browserState', 'target': name + ':' + t, 'passed': True, 'detail': 'synthetic fixture'} for t in targets]})
        self.manifest = {'schemaVersion': '1.0', 'source': {'commit': 'a' * 40, 'tree': 'b' * 64},
                         'scenario': {'name': 'focused-sidebar', 'promptKey': 'SLOW_STREAM'},
                         'viewport': {'width': 1440, 'height': 900}, 'captures': self.captures,
                         'screenshot': self.captures[0]['screenshot'],
                         'assertions': [{'kind': 'browserState', 'target': 'scenario-completed', 'passed': True, 'detail': 'synthetic'}],
                         'accessibility': {'focusedSidebar': True},
                         'timestamps': {'startedUtc': '2026-01-01T00:00:00Z', 'capturedUtc': '2026-01-01T00:00:01Z', 'completedUtc': '2026-01-01T00:00:02Z'},
                         'cleanup': {key: True for key in ('gatewayStopped', 'browserClosed', 'temporaryDataRemoved', 'containerRemoved')}}

    def validate(self, valid=True):
        (self.root / 'evidence.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        result = powershell("Test-UiEvidenceOutput -OutputDirectory '" + str(self.root) + "'")
        self.assertEqual(result.returncode == 0, valid, result.stdout + result.stderr)

    def test_complete_focused_contract(self):
        self.validate()

    def test_requested_scenario_and_source_cannot_be_substituted(self):
        self.validate()
        for arguments in ("-ExpectedScenario active-stream", "-ExpectedCommit " + 'c' * 40):
            result = powershell("Test-UiEvidenceOutput -OutputDirectory '" + str(self.root) + "' " + arguments)
            self.assertNotEqual(result.returncode, 0)

    def test_missing_duplicate_and_wrong_matrix(self):
        original = copy.deepcopy(self.captures)
        for captures in (original[:-1], original[:-1] + [original[0]], []):
            with self.subTest(count=len(captures)):
                self.manifest['captures'] = captures
                self.validate(False)

    def test_each_required_assertion_is_required(self):
        original = copy.deepcopy(self.captures[1]['assertions'])
        for assertion in original:
            with self.subTest(target=assertion['target']):
                self.captures[1]['assertions'] = [a for a in original if a != assertion]
                self.validate(False)

    def test_false_or_string_assertions_fail(self):
        for value in (False, 'true', 1, None):
            with self.subTest(value=value):
                self.captures[0]['assertions'][0]['passed'] = value
                self.validate(False)

    def test_hash_missing_corrupt_and_metadata_contract(self):
        capture = self.captures[0]
        sidecar = self.root / capture['metadata']['path']
        original = sidecar.read_bytes()
        for content in (b'', b'not-json', b'{}', original + b' '):
            with self.subTest(content=content[:20]):
                sidecar.write_bytes(content)
                self.validate(False)
        sidecar.write_bytes(original)
        metadata = json.loads(original)
        for key, value in (('theme', 'wrong'), ('section', 'agents'), ('viewport', {'width': 1, 'height': 1}),
                           ('scrollOwner', {'selector': '.wrong', 'geometry': {'scrollTop': 0, 'scrollHeight': 10, 'clientHeight': 10}})):
            mutated = {**metadata, key: value}
            data = json.dumps(mutated).encode()
            sidecar.write_bytes(data)
            capture['metadata']['sha256'] = hashlib.sha256(data).hexdigest()
            self.validate(False)
        sidecar.unlink()
        self.validate(False)

    def test_cleanup_and_unknown_scenario_fail(self):
        for key in self.manifest['cleanup']:
            self.manifest['cleanup'][key] = False
            self.validate(False)
            self.manifest['cleanup'][key] = True
        self.manifest['scenario']['name'] = 'unsupported'
        self.validate(False)

    def test_missing_corrupt_manifest_and_image_fail(self):
        result = powershell("Test-UiEvidenceOutput -OutputDirectory '" + str(self.root) + "'")
        self.assertNotEqual(result.returncode, 0)
        (self.root / 'evidence.json').write_text('not json')
        result = powershell("Test-UiEvidenceOutput -OutputDirectory '" + str(self.root) + "'")
        self.assertNotEqual(result.returncode, 0)
        (self.root / self.captures[0]['screenshot']['path']).write_bytes(b'wrong image')
        self.validate(False)


class MockedRuntime(unittest.TestCase):
    def run_mock(self, start=0, removal=0, listing=0, remains=False):
        body = """
$fake = {
    param($Runtime, $Arguments, [switch]$AllowFailure)
    $code = 0
    $text = ''
    if ($Arguments[0] -eq 'start') { $code = START }
    if ($Arguments[0] -eq 'rm') { $code = REMOVAL }
    if ($Arguments -contains 'ls') { $code = LISTING; $text = 'REMAINS' }
    [pscustomobject]@{ ExitCode = $code; Output = $text }
}
Invoke-UiEvidenceContainer -Runtime fake -Image image -ContainerName owned-exact -OutputVolumeName owned-volume -ScenarioJson '{}' -RuntimeInvoker $fake | ConvertTo-Json
""".replace('START', str(start)).replace('REMOVAL', str(removal)).replace('LISTING', str(listing)).replace('REMAINS', 'owned-exact' if remains else '')
        return powershell(body)

    def test_success_requires_verified_absence(self):
        result = self.run_mock()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)['ContainerRemoved'])

    def test_cleanup_failure_survival_and_inspection_error_fail(self):
        for params in ({'removal': 1}, {'listing': 1}, {'remains': True}):
            with self.subTest(params=params):
                result = self.run_mock(**params)
                self.assertNotEqual(result.returncode, 0, result.stdout)

    def test_native_timeout_and_failed_start_fail(self):
        for code in (124, 137, 7):
            with self.subTest(code=code):
                result = self.run_mock(start=code)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(str(code), result.stderr)

    def test_native_pid1_timeout_and_scenario_dispatch(self):
        docker = (ROOT / 'container/Dockerfile').read_text()
        self.assertIn('ENTRYPOINT ["timeout","--signal=TERM","--kill-after=10s","1490s","node","/app/entrypoint.mjs"]', docker)
        entry = (ROOT / 'container/entrypoint.mjs').read_text()
        self.assertIn("'./capture-sidebar.mjs'", entry)
        self.assertTrue((ROOT / 'container/capture-sidebar.mjs').is_file())


if __name__ == '__main__':
    unittest.main(verbosity=2)
