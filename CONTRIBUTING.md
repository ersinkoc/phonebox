# Contributing to PhoneBox

Thanks for helping. PhoneBox runs on someone's computer all day, can start programs and talks to a phone over the
network, so reviews focus on what a change lets the phone (or anyone else) do, and on what leaves the machine. This
page lists what reviews keep asking for; following it gets a pull request merged much faster.

## Before you start

- **Small fixes** (a bug, a wrong label, a missing check): open a pull request straight away.
- **New widgets, integrations, providers or anything that changes how the phone and the agent talk**: open an issue
  first and describe what it does for the user. It saves you from building something that has to be reshaped.
- **One change per pull request.** Keep it reviewable: as a rough guide, under about 800 changed lines outside tests.
  A large feature can be a series of pull requests, each one working and tested on its own. Unrelated changes (a new
  widget plus a refactor plus a dependency bump) go in separate pull requests.

## The security model, in short

- The agent listens on `127.0.0.1` (the phone reaches it through `adb reverse`). The optional Wi-Fi port serves only
  the paths on the allow-list in `agent/PhoneBox.Agent/Lan.cs`, over TLS pinned to the paired certificate, with the
  paired key. Settings, pairing and tests stay local. **Adding a path to that allow-list is a security decision**:
  say in the pull request why the phone needs it.
- **The phone sends ids, never commands, paths or URLs.** Shortcuts, command widgets, apps and calendars are defined
  on the computer; the phone asks for one by id, and the agent looks it up in its own settings.
- **Anything that controls the computer goes through a per-widget option** (like Docker's `control` or the processes
  widget's `allowKill`) that the agent checks with `Widgets(enabledPagesOnly: true)`, so removing or hiding the
  widget turns it off. New features that start programs, record or send data are off by default.
- **Secrets never reach the phone.** `AgentConfig.ToPublic()` is what the phone gets; tokens, keys, calendar URLs
  and commands stay out of it (including inside Smart Stack children). Don't log them either.
- **Processes**: start them with `ProcessStartInfo.ArgumentList`, never through a shell and never with text from a
  request. Kill them on timeout and cancel.
- **Panel and settings UI**: build elements with `textContent`; no `innerHTML` with data from the agent, a widget
  option or the network. Custom HTML widgets stay sandboxed.
- Report vulnerabilities privately, see [SECURITY.md](SECURITY.md).

## Privacy

- PhoneBox has no telemetry, accounts or servers of its own, and that stays so.
- **Every new outbound connection** (a public API, a cloud provider, a download) needs a row in
  [PRIVACY.md](PRIVACY.md): the host, what is sent, and how to turn it off. Sending the user's data (audio, text,
  location, files) somewhere must be opt-in and say so where it is switched on.
- "Local" means this computer or the local network. Don't label something local when it can reach the internet.

## Settings, widgets and the phone app

- **A new setting** goes in `Config.cs` (with a default and normalization of bad values), in all three
  `config.default*.json` files, and in the settings page. Decide whether the phone needs it; if not, keep it out of
  `ToPublic()`.
- **A new widget** needs its entry in `agent/PhoneBox.Agent/widgets.json`, the widget in
  `android/app/src/main/assets/www/widgets/`, an icon in `WIDGET_ICONS`, the README count (`tools/check-release.py`
  checks it) and, if it calls the agent, a mock in `tools/pages/demo.js` so the web demo and the UI tests can render
  it. Outside the app (no native bridge) it must show an empty state, not throw.
- **The Android app** targets Android 5.0 (API 21) and later and runs the panel in the system WebView, which is
  updated through the Play Store, so its version doesn't follow the Android version. The panel runs from Chromium 83
  (`compat.js` fills in the newer DOM APIs) and scales widgets fully from Chrome 105 (container query units); below
  that, `index.html` shows an update notice from a plain script that runs even when the panel's modules don't. The
  last WebView for Android 6 is 106, so widgets scale fully on Android 6; for Android 5 it is 95, so widgets there don't
  scale as well. Calls above API 21 need a version check (`./gradlew lintRelease` reports them as NewApi). Changing `minSdk` needs `./gradlew lintDebug` to pass and a note on the devices
  and WebView versions you tried.
- **Don't change behaviour users rely on** (removing a choice, renaming a setting, changing a default) without
  migrating existing settings and saying so in the pull request.

## Tests

Run what CI runs, and say in the pull request what you ran and what you tried by hand:

```sh
dotnet test agent/PhoneBox.Agent.Tests
cd tools/ui-tests && npm ci && npx playwright install chromium && npx playwright test
python tools/check-release.py
cd android && ./gradlew lintDebug assembleDebug     # when the Android app changes
```

- A bug fix comes with a test that fails without the fix, where that's practical.
- Tests must not depend on the machine they run on (ports, clocks, the user's real settings or install folder).
- For UI changes, a screenshot at a small and a large widget size helps a lot.

## Pull requests

- Describe what changes for the user, why, and how you tested it. Link the issue.
- Write code, comments, UI text and commit messages in English. Comments explain why, not what.
- Leave `CHANGELOG.md` and the version numbers to the maintainer; they are updated when the change is merged and
  released.
- Contributions written with AI tools are welcome. You are still the author: read the diff, run the tests, and make
  sure the description matches what the code does.
- An automated reviewer (CodeRabbit) comments on every pull request. Fix what is right, reply where it is wrong;
  conversations must be resolved before merging.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
