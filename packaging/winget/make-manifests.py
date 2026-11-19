#!/usr/bin/env python3
"""Writes the winget manifests for a release:  python packaging/winget/make-manifests.py 0.6.3 <sha256 of the win-x64 zip>

They land in packaging/winget/manifests/e/ersinkoc/PhoneBox/<version>/ (the layout of microsoft/winget-pkgs). Check them
with `winget validate --manifest <folder>` and submit them with `wingetcreate submit <folder>` (or a pull request to
microsoft/winget-pkgs). The release workflow runs this script and attaches the result to the release.
"""
import sys
from pathlib import Path

if len(sys.argv) != 3:
    sys.exit(__doc__)
version, sha = sys.argv[1], sys.argv[2].upper()
pkg = 'ersinkoc.PhoneBox'
out = Path(__file__).resolve().parent / 'manifests' / 'e' / 'ersinkoc' / 'PhoneBox' / version
out.mkdir(parents=True, exist_ok=True)
schema = '1.6.0'
header = '# yaml-language-server: $schema=https://aka.ms/winget-manifest.{kind}.' + schema + '.schema.json\n'

(out / f'{pkg}.yaml').write_text(header.format(kind='version') + f"""
PackageIdentifier: {pkg}
PackageVersion: {version}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: {schema}
""".lstrip('\n'), encoding='utf-8')

(out / f'{pkg}.installer.yaml').write_text(header.format(kind='installer') + f"""
PackageIdentifier: {pkg}
PackageVersion: {version}
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: PhoneBoxAgent.exe
  PortableCommandAlias: phonebox
Dependencies:
  PackageDependencies:
  - PackageIdentifier: Google.PlatformTools
Installers:
- Architecture: x64
  InstallerUrl: https://github.com/paulgeorge92/phonebox/releases/download/v{version}/PhoneBoxAgent-{version}-win-x64.zip
  InstallerSha256: {sha}
ManifestType: installer
ManifestVersion: {schema}
""".lstrip('\n'), encoding='utf-8')

(out / f'{pkg}.locale.en-US.yaml').write_text(header.format(kind='defaultLocale') + f"""
PackageIdentifier: {pkg}
PackageVersion: {version}
PackageLocale: en-US
Publisher: ersinkoc
PublisherUrl: https://github.com/paulgeorge92
PackageName: PhoneBox
PackageUrl: https://github.com/paulgeorge92/phonebox
License: MIT
LicenseUrl: https://github.com/paulgeorge92/phonebox/blob/main/LICENSE
ReleaseNotesUrl: https://github.com/paulgeorge92/phonebox/releases/tag/v{version}
ShortDescription: Turn an old Android phone into a desk panel for your PC.
Description: |-
  PhoneBox has two parts: an Android app that runs full screen on an old phone, and this agent, which lives in the
  tray and feeds it over USB or Wi-Fi: widgets for the PC, AI coding sessions and quotas, music, calendars,
  Home Assistant, OBS, Docker, webhooks and more. After installing, start PhoneBox, plug the phone in and use
  Settings › General › Install the phone app.
Tags:
- android
- dashboard
- desk-panel
- stream-deck
- widgets
- claude-code
ManifestType: defaultLocale
ManifestVersion: {schema}
""".lstrip('\n'), encoding='utf-8')
print(f'winget manifests written to {out}')
