# Changelog

## Unreleased

**Panel**
- Camera widget: smoother. 5 pictures a second by default, and 1 to 10 per camera in Settings › General › Cameras;
  the next picture is decoded before it replaces the one on screen, so it no longer flickers black between frames, and
  an unchanged picture isn't sent again (#209, [Hoangminhfcb](https://github.com/Hoangminhfcb)).

**Android app**
- The app installs on Android 5.0 and 6.0 too (it was Android 7.0). On Android 6 widgets scale fully, as its WebView
  updates go up to version 106. On Android 5 the last WebView is 95, so the panel runs but widgets don't scale as well
  and the WebView notice stays. Tried on a Galaxy Tab S with Android 6.0.1 and WebView 106: pairing over Wi-Fi, every
  widget and page swiping work. Not yet tried on an Android 5 device (#184, #194,
  [phongnguyen2277](https://github.com/phongnguyen2277)).
- A WebView too old to run the panel at all (before Chromium 80, as Android 5 and 6 ship with) now says so on the phone
  and in Settings, with how to update it, instead of leaving the screen black.

## 0.9.18 — 2026-11-17

A Camera widget for IP cameras, the App dock bringing a running app back as it was, and more of the panel in every
language.

**New widget**
- **Camera**: a live picture from an IP camera, a couple of frames a second. Add the camera in Settings › General ›
  Cameras with its RTSP address (rtsp://user:password@…); the computer pulls it with ffmpeg, which you install (or
  point PhoneBox at). A camera is pulled only while a Camera widget for it is on the phone's screen, and stops a few
  seconds after it leaves. The address and its password stay on the computer; the phone only gets the pictures (#209,
  asked for by [Hoangminhfcb](https://github.com/Hoangminhfcb)).

**Panel**
- The Sessions, Processes and Memory widgets and their empty states are in every language (#210,
  [cavalei](https://github.com/cavalei)).

**Agent**
- Windows: a tap on a running app in the App dock brings its window to the front as it was. A maximized window stays
  maximized instead of shrinking to its normal size, and the focus no longer fails with "Windows could not focus the
  running application" when another app is in front (#213, [arrowssuhail-oss](https://github.com/arrowssuhail-oss)).

## 0.9.17 — 2026-11-17

Shortcut keys with real icons, the settings page and widget catalog in Vietnamese, and a few panel fixes.

**Panel**
- A shortcut key shows a real icon instead of only an emoji: the program's own icon when the command is a program on
  the computer, or a PNG, WEBP or SVG you upload for it in Settings › Shortcuts. The emoji stays as the fallback.
  Uploads stay on the computer, and only the settings page can change them (#202, #205,
  [ThalesAugusto0](https://github.com/ThalesAugusto0); asked for by [arrowssuhail-oss](https://github.com/arrowssuhail-oss)).
- The red problem box at the bottom of the panel no longer appears for a cover, photo or icon that fails to load (for
  example a song's cover when its tab closes while the cover loads); the widgets already handle those. It still
  reports the panel's own scripts and styles, and a tap puts it away (#200,
  [Muhammadrizo04](https://github.com/Muhammadrizo04)).
- Discord widget: server tiles drop their border and background card, showing just the icon, name and channel count,
  so more servers fit per row (#206, [ThalesAugusto0](https://github.com/ThalesAugusto0)).
- Vietnamese: the settings page, the widget and template catalog, the weather conditions, session states and the
  phone's pairing dialog are translated too. The weather, session and status texts are translated in every language;
  the rest of the settings page stays English in the other languages for now (#198,
  [sonvivu](https://github.com/sonvivu)).

**Agent**
- macOS: music from the YouTube Music desktop app shows "YouTube Music" as its source in Now playing (#207,
  [Necwolf](https://github.com/Necwolf)).

## 0.9.16 — 2026-11-17

A fix for Vietnamese: choosing it in the settings now sticks.

**Agent**
- Settings › Display › Language: choosing Tiếng Việt sticks. The agent didn't know the new language yet and saved
  it as "Follow the phone", so a phone set to another language stayed in that one (#198,
  [sonvivu](https://github.com/sonvivu)).

## 0.9.15 — 2026-11-17

Four new widgets: Dictation types what you say on the phone into the PC, Photo frame shows a slideshow from a folder,
App console gives the app in front its own keys and dial, and Spectrum bars shows what the PC is playing. Also a Quick
Settings tile, Vietnamese, controls at the top of the screen, and a choice of the adapters the network meters count.

**New widget**
- **Spectrum bars** (Windows): a live spectrum analyzer of the sound the PC is playing, with LED segments and falling
  peak caps in four colour styles. The agent analyzes the default output device in memory and sends the phone only 48
  loudness levels per frame; audio is never stored or sent. It captures only while the widget is on the phone's screen
  (not under the night screen or in a hidden Smart Stack card) and stops a few seconds after it leaves (#195, #196,
  [joshua-jebadurai](https://github.com/joshua-jebadurai)).
- **Dictation**: tap the microphone and speak, and the text is typed into the app in front on the PC. The phone's own
  speech recognizer does the listening (on most phones Google's, which may use its servers; the widget's "Recognize on
  the phone only" option uses Android 12's on-device recognizer, which keeps the audio on the phone, and refuses on
  older Android instead of falling back). The app asks for the
  microphone the first time the button is tapped, listens for at most a minute and never while the widget is out of
  sight. The agent types only for a widget whose "Type the text on the computer" option is on, turns line breaks into
  spaces so nothing is sent or run by accident, and presses Enter only with the widget's "Press Enter after the text"
  option. Windows, macOS and Linux (xdotool or wtype). Idea and first version in #51 by [blx10](https://github.com/blx10).
- **Photo frame**: a slideshow of the photos in a folder on the PC; a synced OneDrive, Dropbox or iCloud folder works too
  (photos only in the cloud are skipped). Choose the folder in Settings › General. On Windows and macOS the agent
  scales large JPEG and PNG photos down before they go to the phone; other photos go as they are up to 6 MB, and
  bigger ones are skipped. The phone fetches one photo at a time by id, and the slideshow only runs while its page is
  showing. First version in #47 by [blx10](https://github.com/blx10).
- **App console** (Windows): keys and a dial that follow the app in front. Add an app in Settings › General › App
  console with its process name and keys (Undo = ctrl+z; the dial turns and presses too), and the widget shows that
  app's keys while it is focused. Before every key the agent checks that the app in front still matches the profile
  and stops if it doesn't; a focus change in the instant between that check and the key press can't be ruled out.
  An agent that isn't running as administrator doesn't send to an app that is. Off until the widget's "Send keys to
  the computer" option is on,
  and the phone only ever sends which key was tapped. macOS and Linux show that it needs Windows. Idea and first
  version in #48 by [blx10](https://github.com/blx10).

**Agent**
- Wi-Fi pairing: on a PC whose network goes through a Hyper-V external switch, the pairing link carries the PC's
  address again instead of an empty host list (#175, #176, [Seyamalam](https://github.com/Seyamalam)).
- Windows: no "adb.exe - Application Error (0xc0000142)" box at shutdown. The agent no longer starts adb once
  Windows is ending the session (#179, [bakhus99](https://github.com/bakhus99)).
- macOS and Linux: Restart and a self-update bring the agent back when it runs as the setup.sh login item (launchd)
  or a systemd service. It now leaves the restart to the service manager instead of starting a copy that launchd
  stopped with it (#182, [shobhit-lyzr](https://github.com/shobhit-lyzr)).
- PRIVACY.md: on macOS the Claude quota reads the token from the Keychain (the default search list, normally the login Keychain) when
  `~/.claude/.credentials.json` doesn't exist (#183, [shobhit-lyzr](https://github.com/shobhit-lyzr)).
- Settings › General › Ping: choose which network adapters the network meters count. With a VPN that tunnels the same
  traffic, or a driver that reports it twice, the meters showed double; tick only the adapter you use (#190,
  [h2r2me453](https://github.com/h2r2me453); also a way around #169). None ticked keeps the automatic choice.
- Windows: the CPU temperature also works on older Intel processors that LibreHardwareMonitor reports only per core
  ("CPU Core #1"…): the hottest core of the CPU is shown when there is no package reading (idea and first version in
  #164, [anandyakshit](https://github.com/anandyakshit)).
- Windows: wireless Razer mice, keyboards and headsets on a 2.4 GHz receiver show up in Device batteries, without
  Razer Synapse (#158, [MarsOldboy](https://github.com/MarsOldboy)). Only known Razer products are asked, only for
  the battery and charging state, and the BlackShark V2 Pro headset only while Synapse isn't running.

**Android app**
- A Quick Settings tile: add PhoneBox in the phone's Edit tiles, and a tap brings the panel to the front (#180,
  [arrowssuhail-oss](https://github.com/arrowssuhail-oss)).
- Wi-Fi pairing page: on Android, when "Open in PhoneBox" opens a store instead of the app (some browsers do that,
  common on LineageOS), a second link opens the pairing link directly (#175, [hassanxs](https://github.com/hassanxs)).

**Panel**
- Vietnamese: the panel and settings in Tiếng Việt, and a Vietnamese README (#177,
  [dung997tb](https://github.com/dung997tb)).
- Settings › Display › "Page dots and the Allow / Deny buttons at the top of the screen", for a panel that stands below
  the screen, where the bottom edge is hard to reach (#181, [Muhammadrizo04](https://github.com/Muhammadrizo04)).
- Smart Stack: a rotation interval of 0 turns automatic rotation off, so the stack changes only when you swipe or tap
  a dot; the longest interval is now 10 minutes (#171, [MrKatsuragi](https://github.com/MrKatsuragi)).
- The Spotify queue widget shows what is playing now and what comes next, with covers; tap a track to play it. Covers
  come from Spotify's image server, which the phone contacts only while the widget's "Show covers" option is on
  (idea from #65, [Sawaid-hub](https://github.com/Sawaid-hub)).
- Pause page switching: tap the pause button next to the page dots, or hold the dots, to keep the current page. While
  paused, neither idle return nor rules switch away; swiping to another page ends the pause (#162,
  [blx10](https://github.com/blx10)).
- The panel's own buttons and marks (close, waiting chip, offline, toasts, the WebView notice, pause rules) are small
  built-in SVG icons instead of emoji, so they look the same on old WebViews too (#163, [Seyamalam](https://github.com/Seyamalam)).
- Discord widget: with several servers wrapping into rows, a row no longer gets shorter than its content and covers
  the next one (#166, [ThalesAugusto0](https://github.com/ThalesAugusto0)).

**Settings**
- The settings page draws its buttons and marks (export, import, order arrows, home, warnings) as small built-in SVG
  icons instead of emoji, so they look the same on every system (#161, [Seyamalam](https://github.com/Seyamalam)).

## 0.9.14 — 2026-11-16

A new News feeds widget for your RSS and Atom feeds; the weather says when it can't be fetched instead of loading for
good; and Apply JSON in the settings copes with partial configs.

**New widget**
- **News feeds**: headlines from your RSS and Atom feeds; with the widget's "Open articles on the PC" option on, tap
  one to open the article in the computer's browser (#108, asked for by [Vampie](https://github.com/Vampie)). Add the
  feeds in Settings › General. The agent fetches each at most every 15 minutes and only while a page shows the widget;
  the addresses stay on the computer, and a feed on the internet can't send the agent into your local network.

**Panel**
- When the forecast can't be fetched, the weather says "Weather unavailable" and tries again after 2 minutes, instead
  of "Loading…" until the next 15-minute refresh; an error answer is no longer kept as the forecast (#159).
- After a weather location change, the panel no longer shows the previous place's forecast while the new one loads or
  fails, and the clock's weather line clears the old temperature when there is no forecast.
- The Docker widget no longer stays on "Loading…" when its filter matches nothing; with "stopped" hidden and every
  match stopped it says so (#152, [blx10](https://github.com/blx10)).

**Settings**
- Apply JSON accepts a partial config: missing sections and fields take the agent's defaults instead of breaking the
  editor, and anything that isn't a JSON object is refused (#150, [Seyamalam](https://github.com/Seyamalam)).
- Widgets pasted through Apply JSON or imported from a page file without a grid position are placed on the grid, so
  moving one no longer makes the agent refuse the whole save (#154, [Seyamalam](https://github.com/Seyamalam)).

## 0.9.13 — 2026-11-16

Two new widgets, Discord and All apps (macOS); the panel runs on phones whose WebView is stuck on Chromium 83; and
some 40 fixes from an outside review: calendars, pushed values, settings, setup and the panel.

**Agent**
- A calendar feed with a recurring event whose `INTERVAL` is huge keeps its other events. Before, such a rule could
  run past the year 9999 and the whole feed failed, showing only its last good copy (#106,
  [Seyamalam](https://github.com/Seyamalam)).
- On a WorkBuddy permission request, the card shows the name WorkBuddy gave the workspace instead of its timestamp
  folder name (#111, [4gui](https://github.com/4gui)). The guide explains how to add the hook to WorkBuddy.
- Calendars follow RFC 5545 more closely:
  - a monthly rule with both a weekday and a month day ("Friday the 13th", a Mother's Day rule) keeps only the days
    that match both, instead of every such weekday (#119);
  - an alarm's `DURATION` or `SUMMARY` no longer replaces the event's own (#123);
  - a local time that happens twice when the clocks go back means the first one (#126). All by [rudycelekli](https://github.com/rudycelekli).
- A rule's time window with a typo (`08:30-typo`, three parts, a day-long end) is rejected instead of firing for a
  minute (#114, [rudycelekli](https://github.com/rudycelekli)).
- Pushed values: one damaged record in `data.json` no longer breaks the panel's state or discards the other values
  (#115), and versions keep increasing under concurrent writes (#121). By [rudycelekli](https://github.com/rudycelekli).
- The Antigravity quota falls back to the older status call when the summary can't be read (#122, [rudycelekli](https://github.com/rudycelekli)).
- The network speed reads each adapter on its own, so a dock, a Wi-Fi reconnect or a counter reset no longer shows a
  spike or a zero (#124, [rudycelekli](https://github.com/rudycelekli)).
- More fixes by [rudycelekli](https://github.com/rudycelekli):
  - pushed values received just before a restart are saved instead of lost (#139);
  - a daily, weekly or monthly calendar rule with `BYMONTH` ("every Monday in December") stays in those months (#140),
    and one event with an impossible `DURATION` no longer drops the whole feed (#142);
  - GitHub CLI calls time out after 20 s and a cancelled one is ended with its child processes (#141);
  - `--config` with a bare file name works; it crashed at startup (#144);
  - a settings tab that loaded before the config file was deleted can't overwrite it afterwards (#116).

**Panel**
- **Discord** widget: the voice channel you are in, mute, deafen, leave and join, the voice channels of your servers
  and your Discord notifications. It talks to the Discord app on the computer, needs your own Discord application
  (see [docs/DISCORD.md](docs/DISCORD.md)), is off by default, and the controls and notifications are each a widget
  option (#112, [bhuwan1321](https://github.com/bhuwan1321)).
- The panel works on phones whose system WebView is stuck on Chromium 83 (it stayed black), and the permission prompt
  shows there too. An approval that arrives while the live stream reconnects is no longer lost, an answer that
  didn't reach the computer can be given again, and the prompt is shown above the night screen (#110,
  [4gui](https://github.com/4gui)).
- **All apps** widget (macOS): browse the computer's applications with their icons and open one with two taps. Opening
  works only while the widget is on a page that is shown (#63, [d4rk-lucif3r](https://github.com/d4rk-lucif3r)).
- The Agenda's window ends at local midnight also on days the clocks change, including where midnight itself is
  skipped (#135); Time Progress stays right on those days (#133); a Countdown with an impossible date such as
  February 31 shows a dash instead of a date in March (#132); Air quality with no index shows "unavailable" instead of
  0 (#134). All by [rudycelekli](https://github.com/rudycelekli).
- Exchange rates show a dash instead of "NaN %" for a currency the feed doesn't have (#145); Air quality follows a new
  weather location without a restart (#146); the Media volume slider no longer stays stuck after a cancelled drag,
  which also blocked page swipes (#147). All by [rudycelekli](https://github.com/rudycelekli).
- Nine settings texts that were still English in every language but Turkish are translated, and a test checks that
  every language has every text (#136, [Seyamalam](https://github.com/Seyamalam)).
- A chart whose metrics are all unknown shows the CPU instead of an error card (#125,
  [Seyamalam](https://github.com/Seyamalam)).
- A Smart Stack's timer and alarm keep running and ring while the stack shows another card or another page is on
  screen (#130, [rudycelekli](https://github.com/rudycelekli)).

**Install and packaging**
- `scripts/install.sh --apk` rebuilds the APK when the Android sources are newer than `dist/` (#113), and Linux
  and macOS autostart files quote paths with spaces and special characters (#118).
- `scripts/install.ps1` builds into a temporary folder and stops the running agent only after the build succeeded
  (#120); `scripts/build.ps1` removes its per-platform staging folders (#127).
- `scripts/uninstall.sh --help` prints its usage instead of removing PhoneBox, and a mistyped option stops before
  changing anything (#138, [rudycelekli](https://github.com/rudycelekli)).
- Setup starts the agent even when the optional phone-app download fails, on Linux and macOS (#143, [rudycelekli](https://github.com/rudycelekli)) and on
  Windows (#149); the phone app is reported as skipped.
- The setup guide has step-by-step instructions for LibreHardwareMonitor (#148), and the development notes name both
  the SDK the app is built with and the Android version it runs on (#137,
  [anandyakshit](https://github.com/anandyakshit)).
- The winget release-notes link moved to the locale manifest, where winget reads it (#128).
- The docs demo keeps a layout saved while its catalog loads (#129). All by [rudycelekli](https://github.com/rudycelekli).

## 0.9.12 — 2026-11-16

Pages can follow the app in front, the AI quotas load on networks with broken IPv6, and the Claude quota check asks
Anthropic less often and shows fewer errors.

**Agent**
- A new rule condition, "an app is in front", holds while a named app owns the foreground window (for example
  `Code|obs64|Safari`). With "show the page" and "while active", the phone shows the page for the app you are using
  and goes back when you switch away (#103, #104, [Seyamalam](https://github.com/Seyamalam)). It works on Windows, on
  macOS with the Accessibility permission, and on Linux under X11 with `xdotool`; Wayland has no way to ask which app
  is in front.
- The AI quotas load on networks that announce IPv6 but don't route it. The quota requests try the IPv4 addresses
  first, then the others, within one 10-second connect budget. Before, the Claude quota failed with "Socket is not
  connected" (#105, [sateesh2020](https://github.com/sateesh2020)).
- The Claude quota check spends fewer of Anthropic's rate-limited requests and shows fewer errors:
  - a restart (an update) or a second agent on the computer uses a reading under 3 minutes old, and still waits
    out a `429`'s Retry-After, instead of asking again;
  - an expired token is not sent, and a token Anthropic refused (`401`) is tried again only after 30 minutes; the
    token Claude Code renews is used within a minute, instead of after a back-off of up to 15 minutes;
  - when the network isn't back yet (after sleep), the last reading stays without an error and the check is
    retried after 30 seconds, up to three times;
  - a reply PhoneBox doesn't recognize (a captive portal, a changed format) keeps the last reading instead of
    emptying the card.
- The Codex quota skips a reading cut off mid-write for the complete one before it, instead of showing a JSON error,
  and a session log that is busy for a moment keeps the last reading.

## 0.9.11 — 2026-11-16

PhoneBox now runs on Android 7 and 8 phones. Shortcut keys work in apps that read raw input, such as Discord, the
default Terminal shortcut works on Windows 10 and on Linux outside the Debian family, and setup explains a Smart App
Control block instead of stopping.

**Android**
- The app runs on Android 7.0 and later (it was Android 9), as first proposed by
  [Sawaid-hub](https://github.com/Sawaid-hub) in #65. Two features need newer Android, so they are guarded:
  before Android 8 the tap vibrations use the older vibration call, and drawing into the display cut-out starts at
  Android 9 as before. Tested on an Android 7.1.1 phone with Chrome 119 as its WebView by
  [anandyakshit](https://github.com/anandyakshit) (#73). `setup.ps1` and `setup.sh` now accept these phones.

**Agent**
- `display.language` is checked like the other settings: a code PhoneBox has no translation for (or an empty one) falls
  back to `auto`, which follows the phone or the browser. It is also in the three default configuration files.
- Shortcut keys on Windows press the left Shift, Ctrl and Alt and send real scan codes. Before, they sent the generic
  modifier keys with no scan code, and apps that read raw input, such as Discord, could take Shift for Right Shift
  (#98).
- The default Terminal shortcut works where its program is missing. Windows 10 has no Windows Terminal (`wt.exe`), so
  PowerShell opens instead. Outside the Debian family there is no `x-terminal-emulator`, so `$TERMINAL` or the first
  installed common terminal (GNOME Console, Konsole, Alacritty, kitty, xterm …) opens (#73).

**Panel and settings**
- Changing the language rebuilds the widgets on the phone, so they switch language right away instead of after the
  next layout change. Texts the first translation pass missed (loading and "unavailable" messages, the Pomodoro
  steps, the breathe widget, the hook button in the settings) are translated too (#96, Seyamalam).

**Setup**
- When Smart App Control blocks the unsigned agent, `setup.ps1` no longer stops with "An Application Control policy
  has blocked this file". It says that PhoneBox is installed but can't run until the agent is signed (#56), and it
  no longer claims the release agent runs with Smart App Control on (#69). A block by AppLocker or another policy on
  a managed computer points to its administrator instead.

## 0.9.10 — 2026-11-16

PhoneBox now speaks 11 languages. This is also a reliability release: updates are checked against the release
checksums and no longer get stuck on a hung or refusing phone, and notifications sent while the agent restarts now
reach the phone.

Most of the fixes come from an evidence-led audit. Each one was reproduced on the unchanged code before the fix
(#88, #89, #90, #91).

**Languages**
- The panel and the settings page are translated into English, Türkçe, Deutsch, Français, Español, Italiano,
  Português, Nederlands, Polski, Русский and 中文.
- By default PhoneBox follows the language of the phone (panel) or the browser (settings).
- To choose a language yourself: Settings › General › Display › Language (`display.language`).
- The README is restructured around a short quick start, with translations in `docs/README.<language>.md`.
- New guides: [setup](docs/SETUP.md), [user guide](docs/GUIDE.md), [development](docs/DEVELOPMENT.md).

**Agent**
- **Updates:**
  - Downloaded updates are checked against the release's SHA-256 checksums before they are installed. A copy that
    does not match is deleted and fetched again, and refused if it still does not match. Before, a damaged copy kept
    in the updates folder could be used again and again.
  - A phone that refuses the app update (another signing key, or "Install via USB" off on Xiaomi) no longer keeps the
    other phones, or the agent itself, from updating.
  - A stuck `adb install` (a hung phone or cable) no longer blocks updates until the agent restarts. adb now has a
    3-minute limit and is stopped when an update is cancelled.
  - On Linux and macOS, the agent now updates itself when the home folder is a symbolic link (for example, moved to
    another disk).
- **USB:**
  - A phone that refuses to become the home app no longer stops USB setup for the other phones, or the keep-awake
    setting.
  - An `adb reverse` rule left by an agent on another port is pointed back at this one. Before, the panel stayed offline
    over USB until the cable was plugged in again.
  - On Windows, adb is found when its folder is on PATH in quotes.
- **Wi-Fi pairing:** a pairing request without the key whose body is not JSON gets the usual refusal (403). Before,
  anyone on the network got a 500 and added an error line to `agent.log` with every such request.
- **Radio directory:** a mirror that answers with something other than the station list is skipped like a failing one.
  Before, one bad mirror broke "Load countries" and "Import stations".

**Android app**
- Notifications, page switches and permission prompts that the agent queued while it restarted (after saving
  settings, an update or a crash) now reach the phone. Before, the panel skipped them because the restarted agent
  counted its events from 0 again (#86, thanks to Seyamalam).
- Page switching:
  - **Stay** on a rule's "Back to …" card now keeps the page.
  - A rule that ended while you were on another page no longer undoes that page when you open it yourself later.
  - A notification whose `countdown` is not a number (`"soon"`, `1e9`, an object) no longer freezes page switching or
    the events after it. The countdown is limited to 0–30 seconds, like every other countdown.

**Setup**
- `setup.sh` no longer sets up a login service that systemd refuses to start (an install path with a quote, an
  apostrophe or a backslash). It says to use the desktop's startup applications instead.
- With several phones connected, skipping the phone choice in `setup.sh` no longer ends setup with PhoneBox stopped.

## 0.9.9 — 2026-11-15

Install PhoneBox with one command on Windows, Linux and macOS. The installer checks the download and sets up what is
missing. This release also adds OLED burn-in protection and Pomodoro sequences with long breaks.

**Android app**
- OLED burn-in protection (#79), in **Settings › General › OLED burn-in protection**, both off by default:
  - **Pixel shift**: the panel moves by up to 2 px every 2 minutes, and the night clock drifts around the screen.
  - **Idle dim**: after a set time without a touch, a dark layer covers the panel. The touch that brightens it does
    not press anything under it. A page switch, a notification, a permission prompt, waking on plug-in and the
    proximity sensor brighten it too.
- Pomodoro (#82):
  - **Long breaks**: a long break after every N focus sessions.
  - **Your own sequence**: for example `25 focus, 5 break, 25 focus, 5 break, 25 focus, 15 long`. It advances by
    itself, can repeat a number of rounds, and then stops with a message.
  - **Display**: the widget shows "Focus 2/3" or "Long break", in blue. ↺ restarts the step; pressed again it
    restarts the sequence.
  - **Presets**: put several Pomodoro widgets on your pages, each with its own sequence (study, reading, deep work).
  - **Compatibility**: existing widgets keep working as before, including a timer that is running during the update.

**Setup**
- One-step installers: `irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex` on Windows,
  `curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash` on Linux and macOS. They
  download the latest release and check its checksum (and its build provenance with a signed-in GitHub CLI). They
  install adb and the optional helpers when missing, after asking, then the agent and start at login. They can also
  put the app on a phone over USB, with step-by-step help for USB debugging, and finally start PhoneBox. Warnings
  cover the unsigned build, antivirus, USB debugging, firewall, Linux USB permissions and macOS prompts. Running
  setup again updates or repairs PhoneBox and keeps the settings; `-DryRun` / `--dry-run` changes nothing. A CI workflow runs
  both installers on Windows, Linux x64/ARM64 and macOS every week.

**Docs**
- The README quick start is rewritten around setup: what it does, its options and warnings, connecting the phone
  over USB or Wi-Fi only, other ways to install, and how to uninstall.

## 0.9.8 — 2026-11-15

Pair a phone over Wi-Fi without a USB cable: the computer shows a QR code, the phone scans it with its camera and
pairs after you compare a check code. The Claude quota keeps its last reading across restarts.

**Agent**
- Pair a phone over Wi-Fi without USB: Settings › General › Wi-Fi & Tailscale › **Pair a phone over Wi-Fi** shows a
  QR code to install the app and one to pair. The pairing link carries the computer's addresses, the certificate
  fingerprint and a one-time code (5 minutes, single use); the phone trades the code for the Wi-Fi key over the
  pinned TLS connection, so the key is never in the QR code.
- Claude quota: the last successful reading is kept on disk, so after a restart (an update) or while Anthropic
  rate-limits the checks, the panel shows the last values and their age instead of an empty quota.

**Android app**
- Opens pairing links from the computer's QR code: it shows the computer's name, addresses and a check code to compare
  with the computer's pairing window, and pairs only after you confirm. It accepts only private network and Tailscale
  addresses, and pairing a computer never replaces another one with the same name.

**Docs**
- A pairing page at `/pair/` on the project site, which the QR code opens: it checks the link, shows the check code
  and hands the link to the app (or to the download page when the app is missing). The pairing details stay in the
  part of the link that browsers never send to a server. The README and the privacy policy describe both ways to pair.

## 0.9.7 — 2026-11-15

The agent no longer needs .NET installed: each release is one self-contained program. The Claude quota shows again
after Anthropic rate-limits it, Open on phone works over Wi-Fi, and there is a contributing guide.

**Agent**
- Claude quota: when Anthropic's usage endpoint answers 429 (it is rate limited per account, and Claude Code uses it
  too), the agent now waits as long as `Retry-After` asks and backs off on repeated errors (1, 2, 4 … 15 minutes)
  instead of asking again every minute, which could keep the limit in place so the quota never showed. The panel says
  when it will try again and keeps the last values.
- The release agent is a single self-contained program on every system: unzip and run, no .NET runtime to install
  (Windows needed the .NET 10 Desktop Runtime before). The zips are larger (Windows about 65 MB, Linux and macOS
  about 45 MB) and updates download that size. After updating, the agent removes the framework-dependent files the
  updater leaves behind; source installs (`install.ps1`, `install.sh`) stay framework-dependent.
- Settings: **Open on phone** works over Wi-Fi and Tailscale too. It shows the page selected in the editor on the phone
  (over USB it also wakes the phone, as before), and says "No phone connected" instead of claiming it worked; a page that is not saved yet asks to save first (#62, #64,
  thanks to d4rk-lucif3r).

**Docs**
- [CONTRIBUTING.md](CONTRIBUTING.md), pull request and issue templates, and review rules for the automated reviewer:
  scope, the security model, privacy and the tests to run.

## 0.9.6 — 2026-11-15

A Computers widget to switch between paired computers, a privacy policy and code signing policy, fewer outbound
requests (weather only when shown, a switchable ping), owner-only settings files on macOS / Linux, and a live stream
that keeps up under heavy load on macOS (thanks to d4rk-lucif3r).

**Panel**
- Computers widget: the computers this phone is paired with over Wi-Fi, one tap to switch, as in the page overview.
  The buttons wait while a USB cable is plugged in, since USB always wins (#58).
- The weather is fetched only while a page has a widget that uses it (weather, forecast, sun & moon, a clock with its
  weather line, Custom HTML); before, the phone asked Open-Meteo every 15 minutes whatever was on screen.

**Agent**
- On macOS and Linux `config.json`, its backup and the pushed data are written readable only by your user (0600), and
  an existing config is tightened at start; before, they followed the umask (often 0644), so other local users could
  read the Wi-Fi, webhook and Home Assistant keys.
- Settings › General › Ping: the host pinged every 5 seconds for the ping metric and the "ping high" rule (1.1.1.1 by
  default); empty turns pinging off.
- macOS / Linux: the live stream no longer waits on `osascript`, `wpctl` or `pactl` for the volume. Under heavy CPU
  load those took seconds, `/api/state` and the stream stopped answering, and the phone kept reconnecting with
  "computer not connected"; the volume is now read in the background, and volume controls still act on a fresh
  reading (#60, thanks to d4rk-lucif3r).
- Settings: the badge says "Phone connected · Wi-Fi" for a phone on Wi-Fi or Tailscale instead of "No phone
  connected", and "· USB" over the cable (#60).

**Docs**
- A privacy policy ([PRIVACY.md](PRIVACY.md)) lists every connection the agent and the app make and how to turn each
  one off; the README no longer says that nothing else leaves the machine.
- Code signing policy in the README and on the demo site, linked under every release: the Windows agent is to be
  signed through SignPath Foundation (application pending, #56).

## 0.9.5 — 2026-11-14

AMD and Intel graphics cards with no vendor tool, a GPU details widget, internet radio in Now Playing with station
cards, 12-hour times, font size, and 18 community pull requests (thanks to blx10).

**Panel**
- 12-hour times: Settings › Display › Time format shows 2:05 PM instead of 14:05 on the clock, world clocks, night
  screen, agenda, Next up, countdown, sunrise/sunset, quota resets and "rules paused until" (#31), or follows the
  phone's own 24-hour setting (#50). 24-hour by default.
- World clocks with many zones in a narrow widget no longer run their times into each other.
- Smart Stack: a pause button next to the dots (or a long press on the dots) stops the rotation (#22).
- GPU details widget: one graphics card in depth (load, VRAM or, for an integrated GPU, its shared memory, power,
  temperature against the card's limit, core and memory clocks, fan, driver and where the data came from). It can show
  the second GPU of a PC, typically the processor's integrated one.
- New metrics for tiles, gauges and charts: GPU hot spot, GPU power and GPU clock; VRAM is now on tiles and charts, the
  GPU and CPU temperatures on charts. The GPU tile shows the temperature and power of AMD and Intel cards too.
- Memory & disks shows a bar for every GPU.
- Font and font size (#42, #44): Settings › Appearance picks a font (original, sans-serif, rounded, condensed, serif,
  monospace) and a size from 50 to 200 %, for the whole panel, a page or one widget. The clock also scales on older
  WebViews without container queries.
- An outdated Android WebView (older than Chromium 105, which breaks widget scaling) is flagged on the panel and in
  Settings › Phone connection, with how to update it (#45).
- When the phone has no internet of its own, the weather, air quality, Hacker News, exchange rate and crypto widgets
  fetch through the agent over USB (#38). Only those five HTTPS feeds are allowed; the proxy is not reachable over Wi-Fi.
- AI quotas / CodexBar: every provider CodexBar reports (Cursor, Copilot, Gemini, z.ai, OpenRouter and more) with
  pace and reset notes (#23); an optional cost overview card with the spend and tokens of the last 30 days and the
  top models (#24, #25), off by default; the widget's provider list offers the providers CodexBar currently reports and
  keeps saved ones that are offline (#26).
- Internet radio in Now Playing (#17): the station playing on the computer shows as the track, with its play time;
  ⏮ / ⏭ switch stations and ⏯ stops and resumes. Starting a station pauses the music that was playing, and resuming
  that music stops the radio (#29).
- Radio widget (#20): station cards with logos (from the station's own logo URL, or, if allowed in Settings, looked up
  in public radio directories) or a sliding card row; a tap on a playing station stops it.
- A tap on a CPU or RAM tile, gauge or chart, or on Memory & disks, opens the Processes page sorted by that resource;
  the Processes widgets show the total CPU and RAM use, and app icons are cached instead of loaded on every refresh (#35).
- Radio stations can be switched off and put in order in Settings › Control Deck; ⏮ / ⏭ skip switched-off
  stations (#27). Stations can be imported by country from radio-browser.info, and the configured streams checked (#28).

**Agent**
- AMD and Intel GPUs (#49). On Windows the agent reads every GPU the way Task Manager does, with no vendor tool: its
  name, load, VRAM and shared memory, temperature, memory clock, power share and driver, through the graphics kernel
  (D3DKMT) and the GPU performance counters. LibreHardwareMonitor's web server, when it runs, adds the hot spot,
  memory temperature, board power, fan and clocks. On Linux, AMD cards come from `amdgpu` sysfs (load, VRAM, GTT,
  temperatures, power, fan, clocks) and Intel ones show their clock. NVIDIA stays on `nvidia-smi`, which now also reports
  every card in a multi-GPU PC, the memory clock and the driver version.
- The state carries `pc.gpus`, all GPUs with the main one first (a graphics card before an integrated GPU);
  `pc.gpu` stays the main GPU, so older panels keep working.
- CodexBar's cost scan (`codexbar cost --days 30`) runs only while a quota widget shows the cost overview, every
  20 minutes and apart from the usage refresh; its figures expire after an hour without a successful run (#24).
- Quota rules keep firing on the last known limits while a provider reports an error; providers CodexBar stops
  reporting no longer keep stale limits (#23).
- macOS and Linux: cover art that arrives late is picked up, and the art of a finished track no longer lingers (#34).
- Radio streams in HLS (`.m3u8`) play on Windows, on the chosen output device (#18). On Linux and macOS, sound and
  radio controls play through `ffplay`, `mpv` or `vlc`, whichever is installed (#33).
- Radio directory imports refuse stream addresses inside a home network (`router`, `nas.lan`, `192.168.1.1.nip.io`);
  logo downloads and stream checks only connect to public addresses and never follow a redirect from https to http.

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
