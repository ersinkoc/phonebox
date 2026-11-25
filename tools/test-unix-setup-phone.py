"""Exercise the one-step Unix setup with synthetic release, phone, and agent."""
import hashlib
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import unittest
from zipfile import ZipFile

SETUP = Path(__file__).resolve().parents[1] / 'setup.sh'


class SetupPhoneTests(unittest.TestCase):
    def install(self, mode, root, expected_code=0, options=()):
        bin_dir = root / 'bin'
        bin_dir.mkdir()
        agent = root / 'PhoneBox/app'
        archive = root / 'PhoneBoxAgent-0.0.1-linux-x64.zip'
        apk = root / 'PhoneBox-0.0.1.apk'
        apk.write_bytes(b'fixture APK')
        with ZipFile(archive, 'w') as bundle:
            bundle.writestr('PhoneBoxAgent', '''#!/bin/sh
if [ "$2" = --write-ignored-usb ]; then
    case "$PHONEBOX_TEST_MODE" in
      unreadable) exit 1 ;;
      ignored|two-devices) printf '%s\n' fixture-phone > "$3" ;;
    esac
fi
printf '%s\n' "$PHONEBOX_TEST_AGENT"
''')
        (root / 'SHA256SUMS-0.0.1.txt').write_text(
            ''.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n'
                    for p in (archive, apk)),
            encoding='utf-8',
        )

        def stub(name, command):
            executable = bin_dir / name
            executable.write_text('#!/bin/sh\n' + command + '\n', encoding='utf-8')
            executable.chmod(0o755)

        downloader = root / 'curl.py'
        downloader.write_text("""
import os, pathlib, shutil, sys
args = sys.argv[1:]
url = args[-1]
if url == 'http://127.0.0.1:8765/api/info':
    sys.exit(0)
root = pathlib.Path(os.environ['PHONEBOX_TEST_ROOT'])
source = root / url.rsplit('/', 1)[-1]
dest = pathlib.Path(args[args.index('-o') + 1])
if source.suffix == '.apk' and os.environ['PHONEBOX_TEST_MODE'] == 'missing':
    sys.exit(22)
mode = os.environ['PHONEBOX_TEST_MODE']
if (source.suffix, mode) in (('.apk', 'corrupt'), ('.zip', 'corrupt-agent')):
    dest.write_bytes(b'damaged download')
else:
    shutil.copyfile(source, dest)
""", encoding='utf-8')
        stub('curl', f'exec {shlex.quote(sys.executable)} {shlex.quote(str(downloader))} "$@"')
        stub('uname', 'case "$1" in -s) echo Linux;; -m) echo x86_64;; -r) echo fixture;; esac')
        stub('id', 'echo 1000')
        stub('adb', '''case "$1" in
  devices) printf 'List of devices attached\nfixture-phone\tdevice\n'
           if [ "$PHONEBOX_TEST_MODE" = two-devices ]; then printf 'other-phone\tdevice\n'; fi ;;
  -s) case "$3:$4" in
        shell:getprop) echo 34 ;;
        install:-r) printf '%s' "$2" > "$PHONEBOX_TEST_ROOT/phone-installed"; echo Success ;;
      esac ;;
esac''')
        stub('nohup', 'touch "$PHONEBOX_TEST_ROOT/agent-started"')
        for name in ('gh', 'systemctl', 'pgrep', 'ldd'):
            stub(name, 'exit 1')
        for name in ('pkill', 'sleep'):
            stub(name, 'exit 0')
        env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ['PATH'],
                   HOME=str(root), XDG_CONFIG_HOME=str(root / '.config'), TMPDIR=str(root), PHONEBOX_TEST_ROOT=str(root),
                   PHONEBOX_TEST_AGENT=str(agent), PHONEBOX_TEST_MODE=mode,
                   DISPLAY='', WAYLAND_DISPLAY='')
        result = subprocess.run(
            ['/bin/bash', str(SETUP), '--yes', '--no-autostart', '--no-extras', '--version', '0.0.1', *options],
            env=env, capture_output=True, text=True, timeout=20,
        )
        self.assertEqual(result.returncode, expected_code, result.stdout + result.stderr)
        self.assertEqual((agent / 'PhoneBoxAgent').exists(), expected_code == 0)
        self.assertEqual((root / 'agent-started').exists(), expected_code == 0,
                         result.stdout + result.stderr)
        return result

    def test_download_and_verification_failures_keep_desktop_running(self):
        for mode in ('missing', 'corrupt'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                result = self.install(mode, root)
                self.assertIn('Phone app skipped', result.stdout)
                self.assertIn('PhoneBox is running', result.stdout)
                self.assertFalse((root / 'phone-installed').exists())

    def test_unverified_agent_still_blocks_installation(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.install('corrupt-agent', Path(directory), expected_code=1)
            self.assertIn('the agent download could not be verified', result.stderr)

    def test_verified_apk_still_reaches_the_phone(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.install('valid', root)
            self.assertTrue((root / 'phone-installed').exists())

    def test_ignored_device_is_never_installed_even_with_an_explicit_serial(self):
        for options in ((), ('--serial', 'fixture-phone')):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                result = self.install('ignored', root, options=options)
                self.assertIn('Skipping the phone app', result.stderr)
                self.assertFalse((root / 'phone-installed').exists())

    def test_other_device_can_still_be_installed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.install('two-devices', root)
            self.assertEqual((root / 'phone-installed').read_text(), 'other-phone')

    def test_failed_policy_keeps_desktop_setup_working(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result = self.install('unreadable', root)
            self.assertIn('could not read the USB ignore settings', result.stdout)
            self.assertFalse((root / 'phone-installed').exists())

    def test_old_agent_cannot_validate_an_existing_configuration(self):
        for policy in ('{"ignoredDeviceSerials":["fixture-phone"]}',
                       r'{"ignoredDevice\u0053erials":["fixture-phone"]}', '{'):
            with self.subTest(policy=policy), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                config = root / '.config/PhoneBox/config.json'
                config.parent.mkdir(parents=True)
                config.write_text(policy, encoding='utf-8')
                result = self.install('valid', root)
                self.assertIn('could not read the USB ignore settings', result.stdout)
                self.assertFalse((root / 'phone-installed').exists())


if __name__ == '__main__':
    unittest.main()
