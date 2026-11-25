# Releasing PhoneBox

1. Update `agent/PhoneBox.Agent/PhoneBox.Agent.csproj`, Android `versionName` and its increasing `versionCode`.
2. Add dated release notes to `CHANGELOG.md`; update README and demo catalog counts when widgets/templates change.
3. Run `python tools/check-release.py`, `./gradlew lintDebug assembleDebug` in `android`, the agent tests, `npm test` in `tools/ui-tests`, and
   `pwsh tools/test-install-apk.ps1` on Windows. Build and verify the release-signed APK.
4. Commit the reviewed scope, push `main`, and wait for the build and Pages workflows to pass.
5. Run the **release** workflow on that commit. Use a draft for artifact review, or publish after the gates pass.

The release workflow reuses the build checks, then tests and smoke-runs the agent on native Windows x64/ARM64,
Linux x64/ARM64, and macOS ARM64/Intel runners. It builds the signed APK, signs the macOS agent with PhoneBox's own certificate, creates
provenance attestations and SHA-256 checksums, and publishes the release notes from the changelog.
The reusable artifact contract also checks the real upload/download boundary with transfer fixtures: exact release
filenames and hashes, exclusion of debug intermediates, and the actual demo's Pages tar format. It never publishes
a release or deploys Pages. Changes to Pages deployment still need a successful Pages workflow on `main`.
Each macOS leg also builds `PhoneBox-<version>-<rid>.dmg`: the same signed agent in `PhoneBox.app` (`tools/make-macos-dmg.sh`), signed
with the same certificate and identifier, so its designated requirement equals the zip's. `tools/check-macos-dmg.sh` mounts the image,
verifies the signature, compares the two requirements and smoke-runs the agent from the app; `build.yml` runs the same steps with its
throwaway certificate on every change. The DMG is not notarized (that needs an Apple Developer ID), so a copy downloaded to another Mac
is blocked the first time and needs System Settings > Privacy & Security > Open Anyway (right-click > Open only works up to macOS 14).

The Windows x64 leg also builds `PhoneBox-<version>-win-x64-setup.exe` (Inno Setup, `packaging/windows/PhoneBox.iss`,
`tools/make-windows-installer.ps1`) around the same `PhoneBoxAgent.exe` the zip has, signed or not as the zip's is.
`tools/test-windows-installer.ps1` installs it silently on the runner, runs the installed agent through `smoke-agent.py`,
upgrades over it while the agent runs, checks the Startup shortcut follows the autostart task, and uninstalls it; `build.yml` runs
the same on every change. It installs into `%LOCALAPPDATA%\PhoneBox\app`, so the agent's self-update keeps working. The `AppId`
in the `.iss` must never change. Inno Setup is pinned in `tools/install-inno-setup.ps1`: bump it there and the next run tests it.
The installer file itself is not Authenticode-signed (SignPath signs `PhoneBoxAgent.exe`).

Published releases update the Scoop bucket and generate winget manifests in a follow-up commit.
Winget manifests are prepared for submission; generation alone does not mean the package is accepted into winget-pkgs.

An agent running from `PhoneBox.app` updates itself from the release's DMG (`UpdateService.UpdateBundleAsync`): it downloads
`PhoneBox-<version>-<rid>.dmg`, checks it against `SHA256SUMS-<version>.txt`, copies the new app out of the image, verifies its signature,
swaps it for the running app (two renames in the same folder) and restarts. So a release must carry a DMG for every macOS `rid`, named
`PhoneBox-<version>-<rid>.dmg`, or apps on that `rid` see no update; a folder the user cannot write to (an admin-only /Applications)
makes the update fail with a message instead.

The agent zips are self-contained single files: each one carries its own .NET runtime, so .NET security fixes
reach users only through a PhoneBox release. When .NET ships a fix for Kestrel, TLS or the runtime, publish a patch
release (`setup-dotnet` with `10.0.x` picks up the newest runtime). Single-file bundles are not compressed on
purpose: the zip is barely smaller and a compressed bundle costs about 90 MB more memory in a process that runs all day.

Required GitHub Actions secrets are `PHONEBOX_KEYSTORE_BASE64`, `PHONEBOX_STORE_PASSWORD`, `PHONEBOX_KEY_ALIAS`
and `PHONEBOX_KEY_PASSWORD`. Keep the original Android signing key: an incompatible APK must fail the update
while preserving the installed app and its data. Never commit the key or its passwords.

The macOS agent is signed with a self-signed certificate made once with `tools/make-macos-signing-cert.sh`
(secrets `MACOS_SIGNING_P12_BASE64` and `MACOS_SIGNING_P12_PASSWORD`; the release stops without them). Every release
then has the same designated requirement, `identifier "net.oxog.phonebox.agent"` plus that certificate, so macOS keeps
the Accessibility permission across updates. Keep the certificate: a new one, a new identifier or the hardened runtime
(library validation would refuse the native libraries next to the agent) makes every Mac user grant it again. The
build workflow runs the same signing step with a throwaway certificate on every change.

`PhoneBoxAgent.exe` in the Windows zip is Authenticode-signed through [SignPath Foundation](https://signpath.org)
while the repository variable `SIGNPATH_SIGNING_POLICY` is `release-signing` (the API token is the
`SIGNPATH_API_TOKEN` secret). The release then waits, up to an hour, for the signing request to be approved in
SignPath; the signature is checked on Windows before the smoke test, and the zip, checksums, attestation and package
manifests all cover the signed file. Without the variable the agent is released unsigned. The **signing test**
workflow runs the same step on its own with the test certificate, without releasing anything.

After publishing, verify the tag points to the intended commit, every asset matches `SHA256SUMS-<version>.txt`,
and `gh attestation verify <asset> --repo ersinkoc/phonebox` succeeds for the APK and agent archives.
Extract downloaded archives and run `python tools/smoke-agent.py <executable> --version <version>` on available
native machines. Separate CI proof, downloaded-binary checks and physical-device tests in the release report.

`scripts/build.ps1` is a local packaging helper. `scripts/release.ps1` dispatches the gated GitHub release workflow.
This release process does not submit packages to external registries automatically.
