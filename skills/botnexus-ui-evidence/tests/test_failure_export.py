"""Mocked native CLI regression: failed evidence is diagnostic only and exported before removal."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from test_focused_sidebar import powershell


class FailureExport(unittest.TestCase):
    def test_nonzero_exports_only_private_allowlist_before_cleanup_and_still_fails(self):
        with tempfile.TemporaryDirectory(prefix='evidence-failure-') as area:
            calls = Path(area) / 'calls.json'
            body = """
$calls = [Collections.Generic.List[object]]::new()
$fake = {
    param($Runtime, $Arguments, [switch]$AllowFailure)
    $calls.Add(@($Arguments))
    $code = if ($Arguments[0] -eq 'start') { 7 } else { 0 }
    [pscustomobject]@{ ExitCode = $code; Output = '' }
}
try {
    Invoke-UiEvidenceContainer -Runtime fake -Image image -ContainerName owned -OutputVolumeName volume -ScenarioJson '{}' -OutputDirectory 'ACCEPTED' -FailureDirectory 'PRIVATE' -RuntimeInvoker $fake
} finally {
    ConvertTo-Json -InputObject @($calls) -Depth 10 | Set-Content -LiteralPath 'CALLS'
}
""".replace('ACCEPTED', str(Path(area) / 'accepted')).replace('PRIVATE', str(Path(area) / 'private')).replace('CALLS', str(calls))
            result = powershell(body)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('exited 7', result.stderr)
            invocations = json.loads(calls.read_text(encoding='utf-8-sig'))
            copied = [args for args in invocations if args[0] == 'cp']
            self.assertEqual([args[1] for args in copied], ['owned:/evidence-output/private-failure/failure.json', 'owned:/evidence-output/private-failure/failure.png'])
            self.assertFalse(any('accepted' in arg for args in copied for arg in args))
            removal = next(index for index, args in enumerate(invocations) if args[0] == 'rm')
            self.assertTrue(all(invocations.index(args) < removal for args in copied))

    def test_failure_copy_error_does_not_skip_cleanup_or_become_success(self):
        body = """
$calls = [Collections.Generic.List[string]]::new()
$fake = {
    param($Runtime, $Arguments, [switch]$AllowFailure)
    $calls.Add($Arguments[0])
    if ($Arguments[0] -eq 'start') { return [pscustomobject]@{ExitCode=124;Output=''} }
    if ($Arguments[0] -eq 'cp') { return [pscustomobject]@{ExitCode=1;Output='missing'} }
    [pscustomobject]@{ExitCode=0;Output=''}
}
try { Invoke-UiEvidenceContainer -Runtime fake -Image image -ContainerName owned -OutputVolumeName volume -ScenarioJson '{}' -FailureDirectory 'PRIVATE' -RuntimeInvoker $fake }
catch { if ($calls -notcontains 'rm') { throw 'Cleanup skipped' }; throw }
"""
        with tempfile.TemporaryDirectory(prefix='evidence-failure-') as area:
            result = powershell(body.replace('PRIVATE', str(Path(area) / 'private')))
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('exited 124', result.stderr)
            self.assertNotIn('Cleanup skipped', result.stderr)


if __name__ == '__main__':
    unittest.main(verbosity=2)
