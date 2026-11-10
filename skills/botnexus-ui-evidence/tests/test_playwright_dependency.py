"""Source regressions for the image's explicit Playwright driver dependency."""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = 'skills/botnexus-ui-evidence/container/PlaywrightDriver.csproj'


class PlaywrightDependency(unittest.TestCase):
    def test_driver_restore_precedes_copy_without_warm_cache(self):
        docker = (ROOT / 'container/Dockerfile').read_text()
        restore = f'dotnet restore {PROJECT} --packages /root/.nuget/packages'
        self.assertIn(restore, docker, 'Driver must be explicitly restored; gateway/extension graph does not supply it.')
        self.assertLess(docker.index('test "$actual" = "$SOURCE_CONTEXT_DIGEST"'), docker.index(restore))
        self.assertLess(docker.index(restore), docker.index('cp -a /root/.nuget/packages/microsoft.playwright/'))
        # Empty cache model: production graph cannot supply a browser-only package.
        available = set()
        for instruction in docker.split('RUN '):
            if restore in instruction:
                available.add('Microsoft.Playwright')
            if 'cp -a /root/.nuget/packages/microsoft.playwright/' in instruction:
                self.assertIn('Microsoft.Playwright', available)
        self.assertNotIn('npm install', docker)
        self.assertNotIn('COPY /root/.nuget', docker)

    def test_helper_inherits_central_pin_and_no_product_dependencies(self):
        project = ET.parse(ROOT / 'container/PlaywrightDriver.csproj').getroot()
        references = project.findall('.//PackageReference')
        self.assertEqual(len(references), 1)
        self.assertEqual(references[0].attrib, {'Include': 'Microsoft.Playwright'})
        self.assertEqual(project.findall('.//ProjectReference'), [])
        for property_name in ('TargetFramework', 'ManagePackageVersionsCentrally', 'Nullable', 'ImplicitUsings'):
            self.assertEqual(project.findall('.//' + property_name), [], property_name)
        repo = ROOT.parents[1]
        central = ET.parse(repo / 'Directory.Packages.props').getroot()
        version = next(item.attrib['Version'] for item in central.findall('.//PackageVersion') if item.attrib.get('Include') == 'Microsoft.Playwright')
        self.assertEqual(version, '1.44.0')
        docker = (ROOT / 'container/Dockerfile').read_text()
        images = re.findall(r'FROM mcr.microsoft.com/playwright/dotnet:v([^\s]+)', docker)
        self.assertEqual(images, [version + '-jammy', version + '-jammy'])
        self.assertIn(f'microsoft.playwright/{version}/.playwright/package/', docker)
        self.assertIn(f'microsoft.playwright/{version}/.playwright/node/linux-x64/node', docker)


if __name__ == '__main__':
    unittest.main(verbosity=2)
