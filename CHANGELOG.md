# Changelog

## Unreleased

**Web kiosk**
- New `/kiosk` address that shows the phone's pages in a browser, for devices without the phone app (a tablet, a
  spare monitor, a Raspberry Pi screen, Fully Kiosk Browser): the same panel, with the phone's API over Wi-Fi.
- Older browsers get `/kiosk/lite/`: the same panel built for browsers without container queries or modern
  JavaScript, down to Firefox 68 on Android 4.4 (`tools/kiosk-lite`; a small runtime fills in container query units,
  `min()` / `max()` / `clamp()`, `color-mix()`, `@container`, `:has()` and `aspect-ratio`).
- Opened with its own key in `?token=`, separate from the phone and webhook keys. Off by default; turned on and its
  address copied in Settings › General › Web kiosk. Served on the Wi-Fi & Tailscale HTTPS port (and on `127.0.0.1`
  for a browser on the PC).

**Linux**
- `packaging/appimage/build-appimage.sh` builds a self-contained AppImage of the agent (the .NET runtime included).
  Restarting from the settings page starts the AppImage file again instead of its temporary mount point.

## 0.9.4 — 2026-11-11

A full audit of the agent, the panel, the settings page and the tooling. Every fix below has a regression test or a
recorded proof.

**Panel**
- After the agent restarts (an update, a reboot) the panel no longer ignores its notifications, rule page switches
  and Claude Code permission prompts until the new agent has sent as many events as the old one.
- Timer and Pomodoro alarms go off on whatever page is showing, not only when their own page is open.
- Control Deck widgets have their padding back (titles were cut off at the card's corner), faders show their level
  on the track, and a fader keeps following the computer after a tap that did not move it. Ducking to 0 % works.
- World clocks show the right time as soon as their page is visible again; a daily counter and the Pomodoro's
  "today" count start over at midnight while the panel stays open; a counter, gauge or On air sign without saved
  options behaves as the settings page shows it.
- Web widgets (rates, prices, Hacker News, air quality) retry within a minute after a failed load. Back closes the
  session details dialog. "now ago" reads "just now".
- The phone no longer sends a slow POST (a container restart, a Home Assistant switch) a second time over Wi-Fi.

**Agent**
- Calendar: recurring events follow more of RFC 5545 (weekdays in a daily rule, "last Thursday of November",
  week start, BYSETPOS, leap-day birthdays, escaped backslashes) and keep their local time across daylight saving.
  Calendars that share a name no longer multiply their events while a feed is unreachable.
- A quota window that has already reset is no longer shown as used up. CodexBar texts use a decimal point on every
  Windows locale. A leftover Claude Code session file whose process ID was reused is not shown as a live session.
- Rules: a pushed "OFF" / "False" / `{"value":"off"}` turns a push rule off, and "18:00-24:00" lasts all evening.
- Battery care keeps trying to switch the power back on after a failed attempt.
- A phone without the app is offered "Install the phone app" again, and no longer blocks the phones after it.
- The settings page can save again after `config.json` was left unreadable by a hand edit; a `null` entry in the
  settings is answered with a message instead of an error page. Pushed values reach `data.json` even while a
  script pushes every second. Page, shortcut and rule ids keep the Turkish capital İ.
- Control Deck: a folder shortcut is not mistaken for a running app; toggle shortcuts run their "off" command; a
  task that cannot start says so; a sleep timer can be cancelled after its player closed and fades Store apps such
  as Spotify; a Spotify timeout is reported instead of failing the request.
- The Command widget's Linux / macOS command can be edited in the settings.

**Settings page**
- Multi-select options (chart series, quota providers, shortcut keys) keep every tick, not only the last one.
- Text options of a widget inside a Smart Stack can be typed normally. Templates and stack children are saved with
  the defaults the inspector shows. "Add a widget" finds room it used to miss. A rule switched to "show the page"
  stores the page it displays.

**Web demo and tooling**
- The [live demo](https://ersinkoc.github.io/phonebox/) shows all 30 templates and answers like the agent: the
  phone-facing settings without commands, working volume, media, process and Control Deck controls, the same
  refusals and error texts. The landing page takes its widget and template counts and the version from the sources.
- `install.ps1` stops when the build fails instead of starting the previous version; the APK installer reports
  adb's real answer under Windows PowerShell 5.1.

## 0.9.3 — 2026-11-11

- Support Podman for the containers widget ([#12](https://github.com/ersinkoc/phonebox/pull/12)). Parse Podman's JSON
  array and NDJSON container/stats output (`Id`, `Names` arrays, object labels, numeric CPU/memory), hide pod infra
  containers, and fall back between `docker` and `podman` when one is unavailable. The first request no longer reports
  "Docker is not running" before the cache is filled, and start/stop/restart waits for an in-flight poll so the panel
  shows the new state.
- Match Activity Monitor's "Memory Used" on macOS: app memory (anonymous minus purgeable) plus wired and compressed
  pages ([#11](https://github.com/ersinkoc/phonebox/pull/11)).
- Hide CodexBar's own `claude` usage probe from the sessions widget, matching only its exact
  `CodexBar/ClaudeProbe` directory so real projects with similar names stay visible
  ([#13](https://github.com/ersinkoc/phonebox/pull/13)).
- Swipe past the last page to wrap to the first, and past the first to wrap to the last
  ([#14](https://github.com/ersinkoc/phonebox/pull/14)). The jump is instant, and drags that start on sliders,
  buttons, the volume bar or lists never flip pages.

## 0.9.2 — 2026-11-10

- Fix webhook JSON payloads with nonnumeric `ttl` fields causing an unhandled exception. Only integer payload TTLs
  set an expiration; other field values remain ordinary data, and an explicit request TTL still takes priority.
- Enforce the 64 KB webhook body limit in bytes even for chunked requests without Content-Length, including
  multibyte UTF-8 text. Oversized bodies are rejected before any pushed value is stored.
- Add eight regression cases covering TTL types, explicit overrides and ASCII/multibyte body boundaries.

## 0.9.1 — 2026-11-10

- Fix stale Windows media cover art after switching YouTube/Chromium tracks ([#9](https://github.com/ersinkoc/phonebox/issues/9)).
  Refresh delayed thumbnails and media-property changes, periodically recheck missed updates, and keep the artwork ID
  stable when the image is unchanged. Clear artwork when the media session closes.
- Recover the four affected default icons from known ANSI/UTF-8 double-encoded forms ([#10](https://github.com/ersinkoc/phonebox/issues/10)).
  Repair only page/shortcut icon fields, preserve the original configuration in the normal backup, and escape saved
  icon values so ANSI-based editors cannot corrupt them. Other user text and commands remain unchanged.

## 0.9.0 — 2026-11-10

- Add optional global, page and widget appearance settings with Original, Rounded cards, Flat, Content only and
  Seamless panel presets. Configure corners, grid spacing, widget/page padding, borders and shadows independently.
- Preview unsaved appearance changes in the real panel from General settings; page/widget inspectors support
  overrides and resetting inheritance. Smart Stack children inherit their stack appearance.
- Preserve the original look for existing layouts, keep explicit zero/false values, bound imported settings and
  retain device safe areas. Page sharing and duplication preserve appearance.


## 0.8.0 — 2026-11-10

- Add 58 Control Deck widget types and six templates, bringing the catalog to 113 widgets and 30 templates.
- Add native Windows application/device audio faders, channel balance, meters, microphone controls, audio-role
  selection, captured profiles, disconnect-safe push-to-talk, ducking and opt-in PC audio listening.
- Add media source/transport/seek/mode controls, Spotify PKCE connection with private token storage, queue/library/
  playlist actions, computer-side soundboard/radio and a sleep fade/pause timer.
- Add application dock/focus, configured keys/macros/snippets/projects, window/display profiles, desktop switching,
  monitor brightness, power plans and hold-confirmed power controls.
- Add OBS mixer/scenes/sources/recording/stream/replay/transition/virtual-camera panels, meeting shortcuts and timers,
  tracked build/dev-server jobs, service controls and GitHub workflow bindings. Existing Docker/HA controls remain.
- Add optional SoundVolumeView routing, measured Voicemeeter controls, MIDI pads and OSC messages. External programs,
  accounts and hardware require setup; Windows native features do not imply Linux/macOS parity.
- Add desktop binding/profile setup, dependency feedback and ID-only phone control requests. Prevent an ambiguous
  submitted control POST from being automatically retried over Wi-Fi.


## 0.7.3 — 2026-11-10

- Fix the launch crash on Android 9 and 10 (API 28–29), reported in
  [issue #8](https://github.com/ersinkoc/phonebox/issues/8) by [0z-zy](https://github.com/0z-zy).
  Full-screen mode now uses legacy immersive sticky flags on these versions; Android 11+ retains the window-insets path.
- Run full Android debug lint in CI and classify `NewApi` findings as fatal, so unsupported framework calls block
  both CI and release APK builds. Before the fix, lint reproduced all five unguarded API-30 calls in `hideSystemUi`.

## 0.7.2 — 2026-11-10

Native Antigravity quotas on Windows, Linux and macOS. Existing layouts and pairing settings are retained.

- Add native Antigravity quota and plan monitoring, adapted from [PR #7](https://github.com/ersinkoc/phonebox/pull/7)
  by [Biswajit9609](https://github.com/Biswajit9609). Support flat and nested measured quota fractions; skip unknown
  or invalid values instead of showing unused quota. Preserve measured zero/full usage.
- Discover legacy Windows/Linux language-server names and extension-server ports with the matching CSRF token.
  Reject unrelated editor processes, try other candidates after a failed probe, bound local requests and macOS
  process reads, and fall back to measured model quotas from older IDE servers without inventing weekly limits.
- Add a version-gated `agy -p /usage --output-format json` fallback for signed-in CLI installations, including the
  standard per-user Windows path. Parse its snake-case group/window fields, disable CLI auto-updates during probes,
  and keep conversation identifiers and token counters out of the panel. Live Windows validation with agy 1.5.3
  returned all four Gemini/Claude-GPT quota windows through the agent API without a model turn.
- Preserve the dependency updates from 0.7.1 when adapting the contribution.
- Make the widget integration fixture independent of the local night schedule; late-night test runs no longer
  activate the night overlay over controls being tested.
- Wait for the demo layout before calendar-test navigation and use delete-on-close smoke-test logs so a transient
  inherited Windows handle cannot fail otherwise successful artifact checks.

## 0.7.1 — 2026-11-09

A compatibility and release-maintenance patch. Existing layouts and pairing settings are retained.

- Update Playwright to 1.65.0, Microsoft.NET.Test.Sdk to 18.11.0 and the xUnit Visual Studio adapter to 4.0.1.
- Update seven pinned GitHub Actions and verify release-artifact transfer and Pages archive compatibility in CI.
- Limit public release assets to the signed APK, five agent archives, build provenance and SHA-256 checksums;
  debug APKs and intermediate build files are excluded.
- Fix `scripts/install.sh --apk` on Bash 3.2 when no device serial is specified. The empty target array was treated
  as unset under `set -u`, stopping the installer before ADB ran. Adapted from PR #2, with installer regressions
  for default device selection, explicit serials and failed updates that preserve the existing app.

## 0.7.0 — 2026-11-09

Smart Stack, richer AI session details and native macOS desk features. Existing layouts and pairing settings are
preserved. Add the new widgets or the CodexBar template from the editor when you want them.

- `--port` and `--config` can run an isolated development agent beside the installed instance. The local URL,
  singleton mutex and USB reverse target follow the selected port, and restarts preserve the launch arguments.

- Smart Stack: combine up to 12 widgets in one grid slot, reorder and configure them in the editor, disable individual
  cards, and switch with dots or a vertical swipe. Rotation follows the visible panel's clock. Command widgets stay
  directly on a page; their scripts are never included in the stack's public configuration.
- Optional CodexBar quota source for Claude, Codex and Antigravity. Primary, secondary and additional measured limits
  are retained; unavailable and synthetic windows are distinguished from real zero usage. Local Claude/Codex refresh
  continues independently and takes over when a CodexBar provider fails or becomes stale.
- CodexBar partial failures retain other providers' successful payloads even when the CLI exits nonzero. Tertiary
  quota windows are included alongside primary, secondary and supplemental windows.
- The background-command regression fixture now terminates only its own recorded process, preserving unrelated
  processes and allowing the timeout tests to run concurrently on macOS.
- The Windows installer refuses an APK with a different signing key and reports failed installs. It no longer
  uninstalls the existing app as a fallback, preserving pairing and widget data.
- Codex session details show model, reasoning effort, token counters, context window, lifecycle and recent tool name.
  Resumed sessions are discovered in older directories; malformed counters and partially appended records do not hide
  the session. Prompt text, responses and tool arguments are not sent to the phone.
- macOS system media and artwork through optional `nowplaying-cli`, with Spotify/Music fallback; selected-output
  volume capability reporting and optional read-only display volume through `m1ddc`.
- Opt-in macOS lock sync over ADB. Commands have deadlines, unsuccessful devices are retried, and setup and polling
  share a serialized state reconciler. Secure phone keyguards still require a manual unlock.

Contributions adapted from [PR #1](https://github.com/ersinkoc/phonebox/pull/1) by
[blx10](https://github.com/blx10) and [PR #2](https://github.com/ersinkoc/phonebox/pull/2) by
[akpinarserdar](https://github.com/akpinarserdar), preserving the 0.6.9 fixes.

Validated on Windows and an Apple M4 MacBook Air: 175 agent tests per platform, 29 browser tests, signed Android
update with application data preserved, real USB lock/wake, media metadata/artwork and pause/resume, audio control,
Apple GPU/RAM readings and live quota fallback. Secure phone keyguards still need a manual unlock.

## 0.6.9 — 2026-11-09

- A question from Claude Code (the "ask the user" tool) no longer shows up on the phone as an Allow / Deny prompt with
  a block of JSON. The panel lists the question and its choices as text with a single **Got it**; the question itself
  is still answered in the terminal on the PC, where Claude Code keeps waiting for it.
- macOS: the GPU widgets work on Apple Silicon (load and the memory the GPU holds, read from `ioreg`). A GPU without
  temperature or power sensors shows its memory instead of "0°C · 0 W".
- The RAM meter shows used / total in GB.
- Clock, date and weather text keep a sensible size on older Android System WebViews that do not know the `cqh` unit.
- `scripts/install.sh`: finds the .NET runtime of a Homebrew install for the autostart service, and signs the agent
  ad hoc on macOS.

## 0.6.8 — 2026-11-08

The last fixes from the code audit; each was reproduced first and verified after.

- A settings tab that was opened earlier can no longer silently undo changes saved since (another tab, a script):
  its save is refused with "changed in another tab or window — reload". This once turned the night screen back on.
- All-day calendar events show on their own date wherever the phone is (west of UTC they showed a day early, east of
  UTC they ran into the next morning).
- A command widget whose command leaves a process running in the background (`start /b …`, `… &`) no longer stays
  "running" until that process ends; it shows its output right after the command itself finishes.

## 0.6.7 — 2026-11-08

A security and reliability release from a code audit; every fix below was reproduced first and verified after.

**Security**
- Other web sites can no longer reach the local API with GET requests (browsers send no Origin header on those). A page
  you visited could make the agent run your command widgets' commands; the agent now refuses any browser request from
  another site, except following a link to the settings page.
- Script in a Custom HTML widget can no longer reach the agent API through the phone's proxy (it could end processes,
  switch Home Assistant entities and more). The proxy now only forwards the panel's own requests.
- Script in a Custom HTML widget can no longer call the phone's native bridge (forget or switch paired computers, pin
  the screen, take over the home screen…). Every bridge call needs a key only the panel itself gets.

**Fixes**
- Ending a process from the Processes widget no longer ends protected processes, other users' processes or the agent
  among its child processes (it used to end the whole tree unchecked, e.g. the adb server started from a terminal).
  The ones left running are counted in the reply.
- A pushed value with a TTL now also disappears from the phone when it expires (the phone was never told and kept
  showing it), and expired values no longer count against the 200-key limit.
- A "while it lasts" rule with a rest time comes back at once after you edit it or resume paused rules (its condition
  still held, but it waited out the rest time first).
- The old agent no longer starts the same update a second time while it restarts into the new version (it logged
  "access denied" on a *.old file).

## 0.6.6 — 2026-11-08

- Updates download from the GitHub API when github.com's download links answer with an error (a 504 held up the
  0.6.5 update for a while).
- A failed update check is retried after 15 minutes instead of 6 hours.
- When GitHub answers anonymous API requests with 503 / 504 (it did for hours on 2026-11-08), update checks and
  downloads are repeated with the GitHub CLI's token, if `gh` is signed in. The token is sent to api.github.com only.
- **Fix:** Settings › General › *Install the phone app* hung for good (and blocked every later update until a restart)
  when the last update check had failed.
- Download fallbacks also kick in when GitHub does not answer at all (a timeout), not only on an error status.

## 0.6.5 — 2026-11-08

- **When the phone drops off USB**, Settings › General › Phone connection (and the log) say why, from the phone's own
  view of its USB link: a cable or port that only charges, USB debugging turned off, or a port the computer lost.
- The Next up widget no longer squeezes its title into the time line when it is only two rows high (Deep work
  template); it drops its heading and keeps the title on one line instead.
- The web demo includes the Weekend and Deep work templates.
- `tools/demo-server.py` serves the web demo from the sources instead of its own copy of the made-up data, so it
  has everything the live demo has (rules, OBS, battery care, permission prompts); webhooks sent to it with `curl`
  reach the open tabs. The panel is at `/panel/` now.
- New tour video and hero image for the README; `tools/ui-tests/media.js` makes the screenshots, the tour (MP4 + GIF)
  and the hero from the demo.

## 0.6.4 — 2026-11-08

**Calm page switching.** Rules, webhooks and the idle return no longer jump between pages. The phone shows
*"Claude needs me — switching to AI in 3…"* with **Stay** and **Go now**, then fades over. It waits while you are
using the panel (20 s after the last touch by default), keeps at least 15 s between two automatic switches, and merges
rules that fire one after another: the higher priority goes first, a rule that ends before its turn is dropped, a
lower rule does not take over a higher rule's page, and when the higher one ends the waiting rule goes straight to its
page instead of bouncing back first. All of it is set in Settings › General › Page switching.

**Back home after a while.** Choose the home page (where the idle return and the Back key go), and per page how long
it may stay without a touch (never, 30 s … 1 hour). A rule keeps its page at most 30 minutes by default; then the idle
return takes over. The return home counts down too.

**Richer rules.** Each rule has a priority, *only on these days* and *only between these hours*, *act when it lasts N
seconds*, a rest time before it may act again, and its own countdown. New conditions: an AI quota above a
percentage, a GitHub review requested, CI failing on your pull request, a device battery low, a slow or unreachable
network, a pushed value above / below / equal to a value, and *always* (for schedules). New action: run a shortcut
(a toggle switches back off when the rule ends). A condition has to be off for 4 s before a rule ends, so a
flickering condition no longer flaps. Pause all rules for an hour from the phone's overview, the Rules widget or the
settings.

**Battery care.** Keeps the phone's battery between two levels (50–80 % by default) by switching its power through
a Home Assistant smart plug or two shortcuts (for example `uhubctl` for a USB hub with switchable ports); phones cannot
stop charging by themselves without root. The phone reports its battery to the agent every half minute.

**Keep awake.** Over USB the agent turns off Android's screen saver (which starts on the charger) and keeps the phone
awake while plugged in.

**New widgets:** Rules, Battery care, Next up (the next calendar event with a countdown), Moon (drawn to its phase),
Word clock. **New templates:** Nightstand, Planner, Automation, Weekend, Deep work.

## 0.6.3 — 2026-11-08

**Claude Code permission prompts on the phone.** When Claude Code asks for permission, the phone shows the request
with Allow and Deny (through a `PermissionRequest` hook that the settings page installs). If the phone is not
connected or nobody answers in time, Claude Code asks in the terminal as usual. Optionally only when you are away.

**Live connection.** The phone keeps one server-sent-events stream open (`/api/stream`) instead of polling: page
switches, notifications and permission prompts arrive at once. Polling remains as the fallback.

**Linux and macOS.** The agent runs on Linux (x64, arm64) and macOS (Apple silicon, Intel): system readings, now
playing (MPRIS / Spotify and Music), volume, processes and app icons, shortcuts, command widgets (bash / zsh),
Claude token from the macOS Keychain. `scripts/install.sh` sets it up as a systemd user service or a launchd agent.

**Rules.** Settings › Rules: when an AI session needs you, music plays, an app runs, the CPU is busy, you are away,
at certain times or when a pushed value is on, show a page, a notification or push a value, optionally only while
it lasts.

**Live preview in the layout editor.** Switch the canvas to "Live preview" to run the real panel (served by the
agent at `/panel/`) with your unsaved changes and live data; it follows the page you select.

**Several computers.** The phone remembers every computer it was paired with; the page overview shows them and
switches between them over Wi-Fi. A USB cable always wins, and the computer you plug into becomes the active one.

**Several phones.** Phones identify themselves to the agent; Settings › General › Phones lists them, and each can
get a name and its own pages (a desk phone with the clock and music, a tablet with everything).

**Portrait.** Settings › Display › Orientation: landscape, portrait or follow the phone. Pages are still designed in
landscape; in portrait the panel arranges the widgets by itself, top to bottom in a 6-column grid.

**Import and export.** Export all pages or one page to a file and import pages from a file (added or replacing
the current ones) to share layouts or move them to another computer; Advanced can download and load the whole
settings file.

**Themes.** Settings › Display: Default, AMOLED black, Midnight blue, Warm and Graphite, an accent colour for the
background glow and page dots, and whether the album art tints the background.

**Custom HTML widget.** Write your own widget in HTML, CSS and JavaScript; it runs in a sandbox (no access to the
panel, the agent or the phone) and gets the PC's data through `phonebox.on('state', …)`; it can run shortcuts.

**Keys and macros.** A shortcut key can also press a key combination (`ctrl+shift+m`, `win+d`, `f13`,
media keys), type text or run a macro of several steps; on / off keys show their state on the phone (the mute key
by itself, others from a pushed value). Windows uses SendInput, Linux xdotool or wtype, macOS System Events.

**Device batteries.** A widget with the battery levels of wireless headphones, mice, keyboards and controllers
(Windows: the Bluetooth battery that Settings shows; Linux: UPower; macOS: Magic devices and AirPods) and the phone.

**OBS Studio.** Connect to OBS's WebSocket server (Settings › General); the OBS widget shows live and recording
state with timers, the current scene and scene buttons, and can start or stop the stream and recording, switch
scenes and mute the mic. The On air widget and rules also know when OBS is live.

**Home Assistant.** Add the address and a long-lived access token in Settings › General; the Home Assistant widget
shows the entities you list (lights, switches, sensors, doors, locks, covers, scenes…) and switches them with a tap.
The token stays on the computer and the phone can only reach the entities its widgets list.

**Docker.** A Docker widget lists Docker or Podman containers with their state, health, CPU and memory; with
"Allow start / stop" turned on in its settings, tap a container to start, stop or restart it.

**Calendars.** Add iCalendar feeds (Google's secret iCal address, Outlook's published calendar, iCloud, any ICS
or `webcal://` link) in Settings › General; the new Agenda widget lists the coming days or, when small, the current or
next event. Recurring events, time zones and moved or cancelled occurrences are handled. A rule can react to "a
calendar event is on" (e.g. show an on-air sign during meetings). The feed addresses never leave the computer.

**Distribution.** A Scoop bucket in the repository (`scoop bucket add phonebox https://github.com/ersinkoc/phonebox`),
winget manifests ready for winget-pkgs, and a release workflow that builds everything on GitHub, publishes it with
checksums and updates both. Agents installed this way put the app on the phone from Settings › General ›
*Install the phone app*.

**Engineering.** Agent unit tests (`agent/PhoneBox.Agent.Tests`: config normalization and migration, Wi-Fi path rules
and tokens, webhook keys, calendars, rules, Home Assistant and OBS against fake servers, key parsing) and browser tests
of the panel and settings page against the web demo (`tools/ui-tests`, Playwright, including every widget in the
catalogue), both in CI. The panel's 2,800-line `app.js` is now ES modules: `main.js` and `widgets/*.js`.

**On air.** A new widget lights up while the computer's microphone or camera is in use (Windows: the same data
as the taskbar's privacy icons; Linux: running capture streams and open video devices), or while a pushed value is on.
Rules can react to the microphone and the camera too.

**Phone sensors and kiosk.** Brightness can follow the room (light sensor), the night screen can come on whenever
the room is dark, and a hand near the phone wakes it (proximity sensor). Screen pinning keeps Home and Recents on
the panel, and the panel can become the phone's home app, so it starts by itself after a reboot; neither needs root.

**Fixes.** macOS reads processes from one `ps` snapshot (the list stayed empty with many processes); the phone no
longer opens a second live stream after a quick pause and resume; `install.ps1 -Apk` rebuilds the APK when the app's
sources are newer than the one in `dist\` (new `-BuildApk` forces it).

**Live demo.** <https://ersinkoc.github.io/phonebox/> runs the real panel and layout editor in the browser with
made-up data.

## 0.6.2 — 2026-11-07

**Widgets.** Pages are now 12 × 6 grids of widgets instead of fixed screens.

- 41 widget types: clocks (digital, analog, world), calendar, time progress, countdown, pomodoro, quick timer,
  stopwatch, weather, forecast, sun & moon, air quality, now playing, volume, metric tiles, gauges, charts,
  memory & disks, processes, top processes, AI quotas, quota ring, AI sessions, GitHub, shortcut grid, big button,
  phone battery, note, checklist, counter, breathe, quote, title, image / webcam, Hacker News, exchange rates,
  crypto, command output, pushed values and notifications.
- 18 page templates: Home, AI quotas, Sessions, System, Processes, Shortcuts, Music, Clock, Focus, World,
  Dashboard, DevOps, Feeds, Ambient, Markets, Daily, Status, Calm.
- Visual layout editor in the settings page: drag to move, drag the corner to resize, per-widget options,
  a searchable widget palette grouped by category, and a template picker.
- Existing 0.6.1 settings migrate automatically (page order, home tiles, shortcut columns, kill permission).

**Getting around.** Tap the page dots or long-press a page for an overview of all pages; the phone's volume keys
flip pages (can be turned off); the page title flashes when it changes.

**Webhooks.** The agent accepts data as well as serving it: `POST /api/push/<key>` (shown by "Pushed value"
widgets), `/api/notify` (toast, vibration, notifications widget), `/api/goto/<page>` and `/api/refresh`.
Other machines on the network or Tailscale use a separate webhook key that never opens the phone API.

**Command widgets** run a PowerShell command on the PC every N seconds and show its output; the phone never sees
the command.

**Tools.** `tools/demo-server.py` runs the panel and the settings page in a desktop browser with made-up data.

## 0.6.1

- Automatic updates from GitHub releases: the agent installs the new APK on the phone over USB and replaces
  itself.

## 0.6.0

- Wi-Fi & Tailscale mode: pair once over USB, then HTTPS to the PC with a pinned certificate and a secret key.

## 0.5.0

- First public release: home, AI quotas, sessions & GitHub, system, processes and shortcut pages; settings page;
  tray agent; night mode.
