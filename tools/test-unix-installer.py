"""Exercise the Unix installer with isolated command boundaries; safe on Linux and macOS."""
import argparse
import json
import os
import pathlib
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest

parser = argparse.ArgumentParser()
parser.add_argument('--bash', default='/bin/bash')
args = parser.parse_args()
INSTALLER = pathlib.Path(__file__).resolve().parents[1] / 'scripts/install.sh'
SETUP = INSTALLER.parents[1] / 'setup.sh'


class SetupOptionTests(unittest.TestCase):
    def test_an_option_cannot_be_used_as_a_missing_serial_or_version(self):
        for option in ('--serial', '--version'):
            with self.subTest(option=option):
                result = subprocess.run([args.bash, str(SETUP), option, '--dry-run', '--help'],
                                        capture_output=True, text=True, timeout=5)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(f'{option} needs a value', result.stderr)

    def test_valid_serial_and_version_are_accepted(self):
        result = subprocess.run([args.bash, str(SETUP), '--serial', 'fixture-device', '--version', '0.9.10', '--help'],
                                capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('Options:', result.stdout)


class InstallerTests(unittest.TestCase):
    def test_missing_source_installer_serial_preserves_following_options(self):
        for flags in (('--serial', '--apk', '--help'), ('--serial', '', '--help'), ('--serial',)):
            with self.subTest(flags=flags):
                result = subprocess.run([args.bash, str(INSTALLER), *flags],
                                        capture_output=True, text=True, timeout=5)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertEqual(result.stderr.strip(), '--serial needs a value')
                self.assertEqual(result.stdout, '')

    def run_installer(self, serial=None, fail=False, newer_sources=False):
        with tempfile.TemporaryDirectory(prefix='phonebox installer ') as directory:
            root = pathlib.Path(directory)
            repo = root / 'repo'
            (repo / 'scripts').mkdir(parents=True)
            (repo / 'dist').mkdir()
            shutil.copyfile(INSTALLER, repo / 'scripts/install.sh')
            apk = repo / 'dist/PhoneBox-fixture.apk'
            apk.write_bytes(b'installer test fixture')
            if newer_sources:
                android = repo / 'android'
                source = android / 'app/src/main/AndroidManifest.xml'
                source.parent.mkdir(parents=True)
                source.write_text('<manifest/>', encoding='utf-8')
                os.utime(apk, (1, 1))
                (android / 'gradlew').write_text(
                    '#!/bin/sh\n'
                    'mkdir -p app/build/outputs/apk/release\n'
                    'printf %s built > app/build/outputs/apk/release/app-release.apk\n',
                    encoding='utf-8',
                )
                apk = android / 'app/build/outputs/apk/release/app-release.apk'

            commands = root / 'bin'
            commands.mkdir()

            def executable(name, body):
                path = commands / name
                path.write_text('#!/bin/sh\n' + body, encoding='utf-8')
                path.chmod(0o755)

            executable('uname', 'case "$1" in -s) echo Darwin;; -m) echo arm64;; *) exit 1;; esac\n')
            # These boundaries must never reach the real machine's agents or services.
            for name in ('pkill', 'nohup', 'codesign', 'systemctl', 'launchctl'):
                executable(name, 'exit 0\n')
            executable('dotnet', '''
test "$1" = publish || exit 2
while [ "$#" -gt 0 ]; do
  if [ "$1" = -o ]; then shift; output="$1"; fi
  shift
done
mkdir -p "$output"
printf '%s\n' '#!/bin/sh' 'printf "%s\\n" "$PHONEBOX_TEST_INSTALL_DIR"' > "$output/PhoneBoxAgent"
chmod +x "$output/PhoneBoxAgent"
''')
            recorder = root / 'record-adb.py'
            recorder.write_text('''import json, os, pathlib, sys
with pathlib.Path(os.environ['PHONEBOX_TEST_CALLS']).open('a') as stream:
    stream.write(json.dumps(sys.argv[1:]) + '\\n')
if 'install' in sys.argv[1:] and os.environ.get('PHONEBOX_TEST_FAIL') == '1':
    print('Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]', file=sys.stderr)
    sys.exit(1)
print('Success')
''', encoding='utf-8')
            executable('adb', f'exec {shlex.quote(sys.executable)} {shlex.quote(str(recorder))} "$@"\n')
            calls_path = root / 'adb.jsonl'
            env = dict(os.environ, PATH=str(commands) + os.pathsep + os.environ['PATH'],
                       DOTNET_ROOT=str(root / 'runtime'), TMPDIR=str(root),
                       PHONEBOX_TEST_INSTALL_DIR=str(root / 'installed'),
                       PHONEBOX_TEST_CALLS=str(calls_path), PHONEBOX_TEST_FAIL='1' if fail else '0')
            command = [args.bash, str(repo / 'scripts/install.sh'), '--apk']
            if serial is not None:
                command += ['--serial', serial]
            result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=20)
            calls = [json.loads(line) for line in calls_path.read_text().splitlines()] if calls_path.exists() else []
            return result, calls, str(apk)

    def test_explicit_serial_targets_only_that_phone(self):
        result, calls, apk = self.run_installer(serial='fixture-device')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(calls, [
            ['-s', 'fixture-device', 'install', '-r', apk],
            ['-s', 'fixture-device', 'shell', 'am', 'start', '-n', 'net.oxog.phonebox.deskpanel/.MainActivity'],
        ], result.stdout + result.stderr)

    def test_no_serial_installs_and_opens_the_phone_app(self):
        result, calls, apk = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(calls, [
            ['install', '-r', apk],
            ['shell', 'am', 'start', '-n', 'net.oxog.phonebox.deskpanel/.MainActivity'],
        ], result.stdout + result.stderr)

    def test_newer_android_sources_are_built_before_install(self):
        result, calls, apk = self.run_installer(newer_sources=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('Building the APK', result.stdout)
        self.assertEqual(calls[0], ['install', '-r', apk])

    def test_failed_update_preserves_app_without_uninstall_or_launch(self):
        result, calls, apk = self.run_installer(serial='fixture-device', fail=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('INSTALL_FAILED_UPDATE_INCOMPATIBLE', result.stderr)
        self.assertEqual(calls, [['-s', 'fixture-device', 'install', '-r', apk]])


if __name__ == '__main__':
    unittest.main(argv=[sys.argv[0]], verbosity=2)
