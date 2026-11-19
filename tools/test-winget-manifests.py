"""Exercise release manifest generation without writing a real version."""
import pathlib
import shutil
import subprocess
import sys
import tempfile

GENERATOR = pathlib.Path(__file__).resolve().parents[1] / 'packaging/winget/make-manifests.py'

with tempfile.TemporaryDirectory() as tmp:
    generator = pathlib.Path(shutil.copyfile(GENERATOR, pathlib.Path(tmp, GENERATOR.name)))
    version = '0.0.1'
    subprocess.run([sys.executable, str(generator), version, 'a' * 64], check=True)
    directory = pathlib.Path(tmp, 'manifests/e/ersinkoc/PhoneBox', version)
    locale = (directory / 'ersinkoc.PhoneBox.locale.en-US.yaml').read_text(encoding='utf-8')
    installer = (directory / 'ersinkoc.PhoneBox.installer.yaml').read_text(encoding='utf-8')
    notes = f'ReleaseNotesUrl: https://github.com/paulgeorge92/phonebox/releases/tag/v{version}'
    assert notes in locale.splitlines(), 'WinGet default locale must carry the release notes URL'
    assert notes not in installer.splitlines(), 'WinGet installer has no release notes field'
    print('PASS: WinGet release notes live in the default locale')
