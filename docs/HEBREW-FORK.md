# Using the Hebrew fork

The settings editor and Android panel support Hebrew. Select עברית in the settings language selector and save.
The Android app bundles its own UI: installing the Windows agent alone does not update the phone's translation.

## Windows updates

New configurations and blank update repositories default to `avibroner/phonebox`.
Explicit repositories in existing configurations are preserved. To migrate an installed upstream copy:

1. Quit PhoneBox from its tray menu. Close any settings editor tabs to avoid saving stale settings.
2. Back up `%APPDATA%\PhoneBox\config.json`.
3. Edit that file and change only `updates.repo` from `ersinkoc/phonebox` to `avibroner/phonebox`.
4. Start PhoneBox again. Restarting also clears any previously discovered upstream release cached in memory.

For example, the existing updates section can read:

```json
"updates": {
  "auto": false,
  "repo": "avibroner/phonebox"
}
```

Do not replace the entire configuration with this fragment. Turning automatic installation off is optional;
the repository determines where both manual checks and automatic updates look.
When the fork has no published releases, GitHub's latest-release request returns 404. The current updater may
show an update-check error; it does not fall back to the upstream repository. Actions artifacts are not releases.
The defaults in this branch take effect only after building and installing it; editing the installed configuration
works immediately after restarting even with the previously built agent.

## Android

Download `phonebox-apk` from a successful build of this fork, extract it, and install `app-debug.apk` on the phone.
CI debug builds use a different signing certificate from the original project's release APK. Android will not
install one over the other. If Android reports a signature conflict, preserve anything needed from the phone app
before uninstalling the original; uninstalling deletes its local preferences and pairing data, so pair again.
The computer's layouts and settings are separate. For ongoing updates, use a stable private signing key rather
than relying on a new CI runner's temporary debug key. Never commit a signing key to the repository.
Native pairing dialogs follow the Android device language.

## Bringing in upstream changes

Keep upstream integration separate from application updates. Add `https://github.com/ersinkoc/phonebox.git` as
an `upstream` remote, fetch it, and merge `upstream/main` into a review branch based on this fork's main.
Review the diff and conflicts, especially localization and the update repository, run tests, then build again.
Merging source code does not update an installed application and does not publish a release.
