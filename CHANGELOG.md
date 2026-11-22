# Changelog

## Unreleased

**Widgets**
- Discord: a button switches Discord between **voice activity** and **push to talk**, keeping your push-to-talk key,
  delay and sensitivity. When push to talk is on but Discord has no key set (so nobody hears you), the widget says so.
  It follows the widget's **Control** option like the other buttons and uses the voice scope PhoneBox already asks
  for (#356, [AlberBM](https://github.com/AlberBM)).

**Agent**
- Phone app updates: a phone that cannot take a streamed install (`splice failed: EINVAL`, seen with Android 15 on a
  3.10 kernel) is tried once more without streaming, and a phone whose app was updated another way no longer keeps
  "Update available" on until the agent restarts (#353, [MU74NTLabs](https://github.com/MU74NTLabs)).
- Discord: a speaking ring can no longer stay on after someone stops talking when you switch channels; speaking
  events count only once both of the channel's subscriptions are in place (#354,
  [M00n-of-saturn](https://github.com/M00n-of-saturn)).
- Display brightness: the slider keeps the level it was set to. It used to jump back to the old level for up to
  30 s, until the agent read the monitor again (#360, [andrii-pavlykivskyi](https://github.com/andrii-pavlykivskyi)).
- macOS: the CPU and RAM process lists show kernel_task and system processes such as WindowServer, locked against
  ending them like other protected processes. kernel_task's memory is the system's wired memory, an estimate rather
  than its own measurement (#388, [ajaybali9](https://github.com/ajaybali9)).

**Android app**
- A request to the agent that fails (a timeout, a dropped cable) now closes its connection at once instead of
  leaving it for the garbage collector. On Android 6 each one also held two pipes until then, and the system's TLS
  code stops the app once 1024 are open; this may be behind the crashes in #389
  ([d3j4n](https://github.com/d3j4n)).
- A phone whose system can't start the Android System WebView (seen on an OPPO with ColorOS 3.0 on Android 5.1) now
  shows a message saying so instead of crashing at every launch (#385, [w364439](https://github.com/w364439)).

**Panel**
- The Spectrum bars and App console widgets stop asking the PC while nobody can see the panel (screen off, another
  app in front): the spectrum no longer keeps the PC capturing its sound, polling every 40 ms once the live stream was
  closed, and the console no longer asks which app is in front.
- Night screen while the computer is unreachable: an option on the phone, off by default, dims to the night screen
  after the agent has answered on no route, USB or Wi-Fi, for a set time (1 to 30 minutes, 5 by default; for
  example the computer is off or asleep). It ends when the agent answers again, unless the scheduled night screen is
  on anyway. The **Wake the PC** button stays reachable over it (#347, #357,
  [CrippledHelix](https://github.com/CrippledHelix)).

**Tools**
- `tools/demo-server.py` refuses a chunked request body (411) and closes the connection, instead of answering without
  the body and reading it as the next request.

**Languages**
- The weather line no longer shows stray English ("weather condition", "weather forecast") after the condition, and
  the weather conditions read right in Arabic, Czech, Hindi, Indonesian, Japanese, Korean, Thai, Traditional Chinese
  and Ukrainian ("Clear" no longer reads as "Delete", "Showers" as a bathroom shower). A few other broken strings in
  Hindi, Japanese, Korean, Russian, Thai and Ukrainian are fixed, and the release check now catches English left-overs,
  raw bytes and empty translations (#358, [Yura-Kondratiuk](https://github.com/Yura-Kondratiuk)).
- Arabic: a native speaker went over the whole translation, and the calendar's weekday headings now follow the
  panel's language in every language instead of always being English (#376,
  [ahmedalnimr](https://github.com/ahmedalnimr)).

## 0.9.27 — 2026-11-21

The Discord login works again, nine new languages (23 in all, Arabic right to left), Discord shows who is speaking,
news feeds get categories and OPML import, and Antigravity chats can show in the AI sessions widget.

**Languages**
- Nine new languages, for 23 in all: Japanese, Korean, Indonesian, Thai, Ukrainian, Hindi, Czech, Arabic (right to
  left) and Traditional Chinese. The Android pairing dialog and the Windows tray menu are now translated too, in every
  language, and each language has its own README.
- Right to left (Arabic): pages turn as in other languages, widgets stay where they were saved, and the overview map,
  charts, playback timelines, meters and command output keep their left-to-right drawing (#346, #352,
  [ahmedalnimr](https://github.com/ahmedalnimr)).

**Widgets**
- News feeds: each feed can have a **category**, and a News feeds widget can show only one category (**Only this
  category**). **Import feeds (OPML / bulk)** in Settings › General › News feeds reads an OPML export (Feedly,
  Inoreader, NetNewsWire and others; folders become categories) or pasted `Category | Name | URL` lines, skips feeds
  that are already in the list, and keeps to the 30-feed limit (#337, [hariharen9](https://github.com/hariharen9)).
- Discord: while you are in a voice channel or call, the member who is talking gets Discord's green ring. It uses the
  voice scope PhoneBox already asks for; the widget asks the agent twice a second only while you are in a channel and
  the widget is on screen (#348, #226, [M00n-of-saturn](https://github.com/M00n-of-saturn)).

**Agent**
- Antigravity chats in the AI sessions widget, from the IDE and the CLI: model, last tool, token counts and whether a
  turn is running. Off by default (**Settings › General › Antigravity sessions**), and chat titles reach the phone only
  with their own switch on. The agent reads the local transcripts for their step metadata only; prompts, replies and
  tool arguments are never kept or sent (#341, [blx10](https://github.com/blx10)).
- Discord: the login no longer fails with "Redirect URI cannot be used in the RPC OAuth2 Authorization flow". The
  request to the Discord app asks for a code (`response_type: code`) instead of naming the redirect, which Discord
  allows only over its WebSocket transport. `http://127.0.0.1` has to be the first redirect of the Discord application
  (#338, #343, [NAWAF-Techpro](https://github.com/NAWAF-Techpro), [xynnpg](https://github.com/xynnpg)).

**Setup**
- Windows: setup adds PhoneBox to the Start Menu, so it can be started again after **Quit** without the autostart
  shortcut; uninstalling removes it (#342, [hariharen9](https://github.com/hariharen9)).

## 0.9.26 — 2026-11-21

Two fixes: Turn off the monitor no longer makes the display go off and on again after a Modern Standby PC wakes,
and phones with Android 5 or 6 stay connected over USB.

**Agent**
- USB on Android 5 and 6: the panel is opened once per connection and stays connected, instead of being reopened about
  every ten seconds. The agent no longer asks these phones for their `adb reverse` rules (their adb answers with a
  protocol fault and drops the phone, and the phone's own USB dialog covered the panel); it remembers the rule it set,
  and sets it again when the phone is plugged in again (#334, [binhnguyenbinh](https://github.com/binhnguyenbinh)).
- Turn off the monitor on a Modern Standby PC: after the wake the display no longer goes off and on again for about a
  minute. The agent now sends the command once, to a hidden window of its own, instead of to every window (#336, #335,
  [nosmile199x](https://github.com/nosmile199x)).

## 0.9.25 — 2026-11-21

Brazilian Portuguese for the panel and the settings page, OpenCode and Codex permission prompts on the phone, Copilot
CLI status, an Immich album for the photo frame, Steam friends widgets, clipboard steps and `if:` conditions in
shortcut macros, long-press and cycling keys, and the trackpad, On air and camera rules on macOS.

**Languages**
- Brazilian Portuguese (pt-BR): the panel, the settings page, the widget catalog and the Android pairing dialog. A phone
  set to pt-BR picks it on its own, and other Portuguese phones keep Portugal's Portuguese (#313,
  [ThalesAugusto0](https://github.com/ThalesAugusto0)).
- About 170 texts the widgets wrote in English (empty states, button names, metric labels, toasts) now go through a
  translation table, translated into pt-BR. Other languages show them in English as before until someone fills in
  their part of the table (#328, [RedZinnm](https://github.com/RedZinnm)).

**Widgets**
- Shortcut macros: `clipboard:<text>` puts text on the computer's clipboard, `paste:<text>` pastes it through the
  clipboard and then puts back the text that was copied, and `{clipboard}` in a text, clipboard or paste step (or a
  *Type text* key) is replaced by what is on the clipboard. Pasted text is kept out of Windows' clipboard history and
  cloud clipboard (#308, [Vasilispapg](https://github.com/Vasilispapg)).
- Photo frame: an **Immich album** as the source, next to a folder on this computer (Settings › General › Photo
  frame: the Immich address, a read-only API key, **Load albums**, pick one). The agent fetches Immich's preview-size
  pictures and serves them to the phone like the folder's; the address and the key stay on the computer. Tested with
  Immich 3.4.1 and 3.4.2 (#280, #279, [snachodog](https://github.com/snachodog)).
- A **Wake the PC** button on the panel while the paired computer is unreachable: the phone sends the Wake-on-LAN
  packet itself, only on the computer's own network (a network holding one of the addresses it was paired with), to the
  MAC addresses the computer gave it when Wi-Fi pairing is on. Not offered on Android 5.0 (#303,
  [Vasilispapg](https://github.com/Vasilispapg)).
- Shortcut keys: a **Long press runs** option on each key. Tapping the key on the phone runs it as before; holding it for
  half a second runs another key you choose (hold *Mute* to open the mixer). The phone only says the key was held, so
  the long press cannot point at anything but a key from your own settings (#307,
  [Vasilispapg](https://github.com/Vasilispapg)).
- Shortcut keys: a new **Cycle through other keys** kind. Each tap runs the next key of its list and starts again after
  the last, and the key shows the face of the key that ran. Where it stands is kept until the agent restarts (#310,
  [Vasilispapg](https://github.com/Vasilispapg)).
- Shortcut macros: `if:` … `else` … `endif` runs steps only when a condition holds: `running:<program>`, `muted`,
  `on:<shortcut id>` (a toggle that is on) or `push:<key>` (a pushed value that is on), and `!` in front turns it
  around. Each macro is checked before its first step, so a missing `endif` stops it before anything runs (#309,
  [Vasilispapg](https://github.com/Vasilispapg)).
- News feeds on Windows: an article whose link has square brackets after the host, such as Franceinfo's
  `#xtor=RSS-3-[lestitres]`, opens in the browser instead of File Explorer's Documents folder (#312,
  [Emsaco64](https://github.com/Emsaco64)).
- **Steam friends** and **Steam friend activity** widgets: friends' avatars, online state, last seen time and current
  game, and a short feed of friends coming online, starting a game or going offline. They need a Steam Web API key,
  which stays on the computer: the agent asks Steam, about once a minute and only while one of the widgets is on an
  enabled page, and the phone never contacts Steam. The activity feed lives in memory only. See
  [docs/STEAM.md](docs/STEAM.md) (#244, [Sawaid-hub](https://github.com/Sawaid-hub)).
- Pomodoro: an optional sound when a step ends, off by default and silent on the night screen, at a share of the
  phone's media volume (#305, [abdulah-x](https://github.com/abdulah-x)).
- Trackpad on macOS: the widget now moves the pointer, clicks and scrolls on a Mac, and three- and four-finger swipes,
  spread and pinch do what the Mac's own Trackpad settings say (Mission Control, App Exposé, moving between
  full-screen apps, Show Desktop, Apps). It needs Accessibility for PhoneBox. New **Haptics** options set how strongly the phone vibrates;
  a tick while scrolling is off by default, and gestures vibrate only where the computer acts on them (#300,
  [Vasilispapg](https://github.com/Vasilispapg)).
- On air and the `mic-in-use` / `camera-in-use` rules work on macOS: the agent reads which apps use the microphone
  (macOS 14.2 and later; before that, only whether one does) and whether a camera is running. The camera part is
  untested on a Mac with a built-in camera; see the guide (#301, [Vasilispapg](https://github.com/Vasilispapg)).

**Agent**
- OpenCode on the phone, through the local API of its background service: permission prompts with **Allow**,
  **Allow always** (only when OpenCode says which pattern it saves, and after a short countdown) and **Deny**, and
  the agent's questions with their choices or a typed answer. Its sessions can show in the Sessions widget. Each part
  has its own switch in **Settings › General**, and all of them are off by default: phone answers, sessions, session
  titles, and OpenCode inside WSL. WSL is looked at only for distributions that are already running, at most once a
  minute, so a stopped distribution is never started. The service's password stays on the computer (#319,
  [Haruki1707](https://github.com/Haruki1707)).
- Copilot CLI status on the panel: a guide and a small bridge script ([docs/COPILOT-CLI.md](docs/COPILOT-CLI.md))
  send whether a GitHub Copilot CLI session is working, has finished its turn or needs attention through the webhook
  API, also from WSL2. Only the state is sent, never prompts, paths or output (#331, #317,
  [quasimagia](https://github.com/quasimagia)).
- Codex permission prompts on the phone: install the Codex hook in **Settings › General** (next to Claude Code's), trust
  it once in Codex, and Codex's approval requests arrive on the phone with **Allow** and **Deny**, like Claude Code's.
  The hooks never go through a proxy (#321, #254, [akpinarserdar](https://github.com/akpinarserdar)).
- Live Codex quotas without a running Codex session: an opt-in setting (**Settings › General › AI quotas & CodexBar**,
  off by default) reads them through the Codex CLI's own app-server every few minutes while a page shows Codex
  quotas, backing off after failures (#290, #298, [wahackr](https://github.com/wahackr)).
- Shortcuts on Windows: a program a shortcut starts (Spotify, say) opens in front instead of behind the window in use,
  as the App dock's apps already do (#322, [adamjorgkaizen-oss](https://github.com/adamjorgkaizen-oss)).
- Discord: the login no longer fails with `invalid_request: Missing "redirect_uri"`. The authorization request now
  names the same `http://127.0.0.1` redirect as the token exchange (#316, [Sune2you](https://github.com/Sune2you)).
- Wake-on-LAN: a **Wake a computer** shortcut (Settings › Shortcuts) turns on another machine on your network from its
  MAC address, also while the phone is away on Tailscale. It sends a magic packet on the local network only; see the
  guide for the BIOS and adapter settings the machine needs (#302, [Vasilispapg](https://github.com/Vasilispapg)).
- Now Playing on macOS and Linux is read every second only while a panel or a `media-playing` rule asks for it, and
  every 10 seconds otherwise, so an idle computer no longer starts `nowplaying-cli` or `playerctl` every second (#299,
  [EgglezosHub](https://github.com/EgglezosHub)).

## 0.9.24 — 2026-11-20

A security fix for phones connected over USB: the USB connection now answers only the PhoneBox app, so **update the
agent and the phone app together**. Also: audio mixers show one control per app on Windows, Microphone and Output
selection fit side by side, device batteries refresh faster, and a phone that is plugged in can be left as it is.

**Security**
- The USB connection answers only the PhoneBox app. `adb reverse` now ends on a loopback port of its own instead of the
  agent's local port, over TLS that the app pins to the agent's certificate, and every request must carry a key the
  app makes. The agent reads that key and hands over the certificate's fingerprint over adb, through a place only the
  adb shell can reach, after checking that it belongs to the PhoneBox app. Over USB the phone reaches the same paths as
  over Wi-Fi, plus its pairing data and the feed proxy; the settings, shortcut tests and pairing controls stay on the
  computer. **Update the agent and the phone app together**: an older app can't use USB with this agent, and the
  settings page offers its update. If an app you don't trust was ever on the panel phone, pair Wi-Fi again and make a
  new webhook key, and renew the Home Assistant, Discord and other tokens in the settings
  ([GHSA-j6qc-pm3p-fhg7](https://github.com/ersinkoc/phonebox/security/advisories/GHSA-j6qc-pm3p-fhg7)).

**Widgets**
- Audio mixers on Windows: one control per app instead of one per Windows audio session. An app that plays on two
  outputs, or that you moved to another output, no longer shows up twice. Apps get the name Windows' own mixer shows
  (MSI Center, not `DCv2`), and the phone lists only apps that are playing, plus System sounds. Volume and mute reach
  every session of the app; ducking, the sleep timer and audio profiles still see every session (#292,
  [RawreeUX](https://github.com/RawreeUX)).
- Microphone and Output selection: a **Layout** option (horizontal, grid or the vertical list as before), so several
  devices fit side by side on a phone, and **Device labels** to show a short name or an emoji for a device
  (`Speakers (USB Audio CODEC) = 🔊`, one per line, matched by its exact name). Switching still uses the device itself,
  and the full name stays in the button's tooltip (#294, [RawreeUX](https://github.com/RawreeUX)).
- Audio mixers: mute is a compact speaker icon under the volume instead of a wide Mute / Unmute button, so many apps
  fit without overlapping (#293, [RawreeUX](https://github.com/RawreeUX)).
- Device batteries on Windows: a paired Bluetooth device that has disconnected (a controller or headset turned off) is
  no longer shown with its last battery reading (#295, [RawreeUX](https://github.com/RawreeUX)).
- Device batteries: the Razer Barracuda Pro, read through its 2.4 GHz receiver, also while Razer Synapse runs (#296,
  [RawreeUX](https://github.com/RawreeUX)).
- Device batteries refresh every 20 seconds while the widget is shown, instead of once a minute; macOS stays at once a
  minute, because reading headphones there takes seconds (#297, [RawreeUX](https://github.com/RawreeUX)).

**Agent**
- A new **Settings › Display › Open PhoneBox when the phone is plugged in** option (on by default, so nothing
  changes until you turn it off). Off, a phone that connects over USB, or reconnects after adb restarts, is left as it
  is instead of being woken with the panel brought to the front, for a phone that also runs app tests over adb (#291,
  [snackncode](https://github.com/snackncode)).

## 0.9.23 — 2026-11-20

On air works again on current Windows 11, the Spectrum bars reach full height on monitors and surround outputs, a
waiting update shows in the settings page's header, and Claude quotas on macOS read the Keychain past a stale
credentials file.

**Widgets**
- Spectrum bars at full height on every output device. On outputs that apply the Windows volume in software (HDMI and
  DisplayPort monitors, S/PDIF), the captured sound was as quiet as the slider, so a monitor at 28 % left the bars
  nearly flat; the agent now undoes that volume (up to +40 dB) as the sound arrives. Stereo on a surround output (a
  4-channel controller, 5.1 / 7.1) is no longer lowered 6–12 dB. With no output device to capture, the log says so
  once instead of every second (#263, [joshua-jebadurai](https://github.com/joshua-jebadurai)).

**Settings page**
- A waiting update shows in the header, so it is seen from every section instead of only in the Updates card far
  down General: **Update to 0.9.x** installs it, like the card's Update now. It waits while there are unsaved changes,
  because the agent restarts, and says so while the update runs.

**Agent**
- Claude quotas on macOS: a `~/.claude/.credentials.json` left behind by another tool or an older Claude Code no
  longer hides the sign-in Claude Code renews in the Keychain. Once the file's token is missing or expired, the agent
  reads the Keychain too and uses the sign-in that expires later, instead of showing "Token expired" for good (#277,
  [sonvc94](https://github.com/sonvc94)).
- On air works again on current Windows 11. Recent builds (26200 with the October 2026 updates, 26300) stopped
  updating the privacy record the agent read, so the widget and the microphone and camera rules stayed off during
  calls. The agent now asks Windows directly: running recordings on any microphone, and cameras that are streaming
  (Windows 10 2004 and later). The old record is the fallback where these can't be read (#282,
  [ridhampansara27](https://github.com/ridhampansara27)).

## 0.9.22 — 2026-11-19

Greek, a Trackpad widget, Claude Code session details and chat titles, CPU and GPU temperature on Apple silicon Macs,
a choice of weather model, stroke icons everywhere, closer media sync, much less CPU for blinking status lights, and a
macOS Accessibility permission that survives updates.

**Languages**
- Greek (Ελληνικά): the panel, the settings page, the widget and template catalog, the Android pairing dialogs and a
  Greek README. Pick it under Settings › General › Language, or let the phone's language choose it (#262,
  [Vasilispapg](https://github.com/Vasilispapg)).

**Widgets**
- CPU and GPU temperature on Macs with Apple silicon (macOS 14 and later), read from the SMC without root or extra
  tools: the CPU temp metric and the GPU widget's temperature now work on macOS. The keys follow macmon's rule (CPU:
  Tp/Te/Ts, GPU: Tg). Intel Macs and older macOS versions still show no temperature (#272,
  [sonvc94](https://github.com/sonvc94)).
- Trackpad: move, click and scroll the computer's mouse from the phone, with tap to click, two fingers to right-click
  and scroll, hold to drag and an optional edge scroll bar. Mouse control is off until you turn on **Allow controlling
  the mouse from the phone** in the widget's options; Windows, and Linux with `xdotool` under X11 (#250, #251,
  [Prajjwal2404](https://github.com/Prajjwal2404)).
- Weather: Settings › General › Weather has a temperature unit, Celsius or Fahrenheit, for the clock's weather line
  and the Forecast widget (#256, [haydn9000](https://github.com/haydn9000)).
- Weather: Settings › General › Weather can pin the Open-Meteo forecast model (GFS, ECMWF, ICON, UKMO, GEM, JMA,
  Météo-France or CMA) instead of Open-Meteo's automatic best match. Only Automatic and GFS supply every value; other
  models may leave out the UV index or the chance of rain, and the panel then just leaves those out. Météo-France
  and CMA forecast about 4 and 5 days, and the Forecast widget shows only the days there are (#271,
  [sonvc94](https://github.com/sonvc94)).
- Quota ring: provider list includes Cursor and Grok (Grok uses its own mark), and offers every provider currently
  reported by CodexBar (#260, [ajaybali9](https://github.com/ajaybali9)).
- AI sessions: Cursor agent chats from `~/.cursor/projects/*/agent-transcripts` can show alongside Claude Code and Codex
  (off by default in Settings › General › Cursor sessions). Only the last message's role is kept; chat text is never
  shown, stored or sent. Project names use the folder basename from Cursor's `workspaceStorage`
  (#259, [ajaybali9](https://github.com/ajaybali9)).
- Sessions widget: tap a Claude Code session to see what it is doing. The details list its recent steps (tool name and
  file, with how long a running one has taken), the last exchange, and a scrollable list of the last 30 messages. Your
  prompts and Claude's replies leave the computer only when you turn on Settings › Claude Code › **Show the
  conversation on the phone**, which is off by default (#266,
  [Vasilispapg](https://github.com/Vasilispapg)).

**Panel**
- Less CPU, so a panel on battery lasts longer: the clock's colon, the session status dots, the OBS light, the on-air
  sign, the dictation microphone and the radio equalizer no longer run endless CSS animations, which made the phone
  draw a frame on every display refresh (120 a second on some phones) while they were on screen. On a Galaxy S25 Ultra
  the clock page went from about 18 % to 9 % of a core. The colon blinks as before; the other lights pulse in two steps
  instead of fading (#273, #274, [Vasilispapg](https://github.com/Vasilispapg)).
- Media: the position bar is a slider; hold it to see where the song will continue, and it seeks when you let go. The
  song time follows the computer more closely, also with Spotify on macOS, which reports no position of its own, and
  no longer steps back and forth.
- Volume and media changes reach the phone at once instead of at the next second, and on macOS a volume change takes
  milliseconds instead of up to a second and a half. While the screen is off or another app is in front, the panel
  stops asking the agent for updates and catches up when it is back (#268,
  [Vasilispapg](https://github.com/Vasilispapg)).
- Pomodoro: the Vietnamese example for your own sequence uses the words the timer understands (`25 focus, 5 break`);
  copied as shown, the translated words were not understood.
- Clock widget: a new option chooses which AI quota providers its status line shows; clocks saved before it show
  all of them, as before (#225, #228, [Seyamalam](https://github.com/Seyamalam)).
- The computer switch buttons (overview and Computers widget) and the session details close button draw stroke icons
  instead of an emoji and a text X, so they look the same on older phones (#252,
  [Seyamalam](https://github.com/Seyamalam)).
- Sessions widget: with many Claude Code chats in one folder every row read the same. Settings › Claude Code › **Show
  Claude Code chat titles on the phone** (off by default) shows each chat's title, the name you gave it or the one
  Claude Code wrote, with the folder on the second line. The title is read from the end of the chat's own file on this
  computer and, with the switch off, is not read or sent at all (#261, [Vasilispapg](https://github.com/Vasilispapg)).
- Sessions: claude-mem's background sessions (the short Claude Code runs in `~/.claude-mem/observer-sessions` that
  write its memory notes) no longer show up, so they no longer pop up a "finished" message every few minutes.
  Settings › General › Display › **Show a message when a session finishes** turns that message off for every session;
  the "needs input" message still shows (#275, [TheRedViperV](https://github.com/TheRedViperV)).
- Session details show Folder, Status, Started and Last activity in one compact row (two rows on a narrow screen)
  instead of four lines.
- Every remaining emoji in the panel and the settings page (weather, battery, widget and page icons, Home Assistant
  and rule icons, media and timer buttons, the moon phase) is drawn as the same Lucide stroke icon, so the panel looks
  the same on every phone. Emoji saved in your configuration keep working and are shown as the matching icon; one
  without an icon, or any text, is shown as typed. Icon-only buttons have translated names for screen readers (#265,
  [Vasilispapg](https://github.com/Vasilispapg)).

**Settings page**
- Clearer and calmer: section icons and a highlighted current section and page, long introductions folded to two lines
  (click to open), larger buttons and switches, hover and keyboard-focus states, a spinner on Save and an "Unsaved
  changes" label, toasts with a mark that screen readers announce, and Escape closes dialogs. Motion stays off with the
  system's reduce-motion setting (#264, [Vasilispapg](https://github.com/Vasilispapg)).

**Agent**
- macOS: the Accessibility permission (shortcuts that press keys) carries over to later updates. The agent is signed
  with PhoneBox's own certificate instead of ad hoc, whose signature changed with every build, so macOS dropped the
  permission after every update. Updating from an older version needs it once more: remove PhoneBoxAgent under
  System Settings › Privacy & Security › Accessibility with − and add it again. The setup script and the message on
  the phone say so (#269, [nejcbat-x](https://github.com/nejcbat-x)).
- Shortcuts that press keys can use the keypad: `numpad0` to `numpad9`, `numpadenter`, `numpadadd`, `numpadsubtract`,
  `numpadmultiply`, `numpaddivide` and `numpaddecimal` (also written `num3`, `kp3` or `KP_3`), sent as the keypad's own
  keys, so a shortcut bound to Ctrl+Alt+Numpad3 in Discord or a game is matched (#270,
  [adrianhermida](https://github.com/adrianhermida)).

## 0.9.21 — 2026-11-18

Security and reliability fixes from an audit of the agent, the panel, the phone app and the installers, Big buttons
that show a picture, rules that notice when a pushed value stops arriving, and clearer Wi-Fi setup. Each audit fix came
with a test that failed before it, kept as a regression test (#234, #235, #236, #237).

**Security**
- Turning Wi-Fi off in Settings closes the LAN API and pairing at once, not after a restart, and a webhook token can no
  longer pose as a paired phone.
- A control widget (command, OBS, Home Assistant, Docker and others) on a hidden page, or one that was removed, no
  longer allows its action, including when the settings change while the action runs.
- The feed proxy, the radio directory and the Linux/macOS artwork loader reject every private or reserved IPv6 form;
  the artwork loader no longer reads `file://` addresses or serves SVG/HTML as artwork.
- The phone app no longer follows redirects, which over Wi-Fi would have opened a connection without the paired PC's
  pinned certificate.
- With `--config`, the LAN certificate, the shortcut icon uploads, the pairing and the Discord login stay inside that
  config folder. **If you run the agent with `--config`, pair your phone and sign in to Discord once more.**

**Panel**
- Big button: a new option fills the key with the shortcut's uploaded picture, and another hides its label (#233,
  [dhiya13](https://github.com/dhiya13)).
- With the panel in Turkish, built-in English widget titles are uppercased with English rules: NOTIFICATIONS, not
  NOTİFİCATİONS (#240, [menes97](https://github.com/menes97)).
- Typing in the Note editor no longer triggers panel shortcuts, and Escape (or Back on Android) closes the editor.
- A late answer from the agent no longer overwrites a newer one, so settings, sessions and pushed values don't jump
  back. A note saved from the phone isn't undone by a reload that was already under way.
- Widgets that aren't on screen stop asking the agent; timers and countdowns keep their deadline while the tab sleeps;
  countdown targets can include seconds.
- The panel waits for the agent's settings before asking for the weather, so the default location is never sent.

**Settings**
- Turning on Wi-Fi now says right away that it needs Save and **Restart to apply**; the line used to keep saying
  "Off" until then. The README and the setup guide list the Wi-Fi steps one by one (#239,
  [alkeshkhajindar27-max](https://github.com/alkeshkhajindar27-max)).
- A save that can't reach the agent says so and keeps the draft. Control Deck fields keep an edit even when Save is
  clicked without leaving the field, and the two language selectors stay in step.

**Agent**
- Claude quota widget: with only the Claude Desktop app signed in, it says that the desktop app doesn't share its
  sign-in and that the `claude` command-line tool is needed, instead of "Not signed in to Claude Code" (#247,
  [hassanxs](https://github.com/hassanxs)).
- Linux: the default Calculator, Settings and System Monitor shortcuts start the desktop's own apps on KDE, Xfce,
  MATE and others when GNOME's are missing (#238, #246, [sateesh2020](https://github.com/sateesh2020)).
- Rules: a pushed value can trigger a rule when it has no update for N seconds (or isn't there at all), and values
  can be compared with `>=` and `<=` (#242, #245, [menes97](https://github.com/menes97)).
- Home Assistant locks and vacuums read their state again before a toggle and do nothing when it is unknown.
- Panic mute wins over push-to-talk: a microphone it closed (or failed to close) can't be reopened by a hold.
- OBS, Docker, Spotify and Discord handle disconnects, account changes and malformed messages without stale data.
- "While active" rules no longer flip a toggle that was already on, and Undo restores the mute shortcut.
- Smaller fixes across commands, GPU and CPU temperature readings, Codex sessions, calendars, the photo frame, charge
  guard, approvals and updates.

**Phone app**
- The live connection no longer reports a late close after it was stopped, and isn't restarted in the background.

**Installers**
- Windows: a failed `-Apk` step no longer leaves the agent stopped.
- Linux/macOS: `--serial` and `--version` no longer take the next option as their value.

## 0.9.20 — 2026-11-18

Notes edited from the phone, a Home Assistant entity editor, a translated settings page, Antigravity quotas with
newer IDEs and Intel GPU load on Linux.

**Panel**
- The Note widget can be edited from the phone: tap it, write, save (#218, [cavalei](https://github.com/cavalei)).
- News feeds widget: the list scrolls, so every headline is reachable, not just the ones that fit; an article opened
  from the phone loses its accent bar; and a new option shows each headline over 2 or 3 lines (#108,
  [Vampie](https://github.com/Vampie)).
- Home Assistant widget: an entity editor in Settings. Each entity gets its own row with an optional name and icon,
  and its own choice of whether the phone may switch it; entity ids autocomplete from Home Assistant. Widgets that list
  their entities as text keep working (#109, #178, [sateesh2020](https://github.com/sateesh2020)).

**Settings**
- Saving after uploading or removing a shortcut's custom icon no longer fails with "The settings were changed in
  another tab or window". The agent sent the settings version as a number too large for the page to keep exactly, so
  the page sent back a slightly different one; it is sent as text now.
- A freshly opened settings page no longer offers Save and Revert before anything was changed, and Revert gets back
  to a clean page again. The feeds, cameras, console, Discord and ping editors filled in their defaults after the page
  had noted what was saved (#229).
- The settings page is translated: section text, the pages editor, rule conditions, appearance options, the connection
  and update cards and the webhook examples, complete in Vietnamese. The webhooks section no longer stops rendering
  part-way, which a translation helper shadowed by a variable had caused (#219, [sonvivu](https://github.com/sonvivu)).

**Agent**
- Antigravity quotas show up with newer Antigravity IDE versions (reported with 2.32), whose language server answers
  over HTTPS on a port it picks at start-up: the agent tries the loopback ports that process listens on, and accepts
  its self-signed certificate only for this computer (#227, [goutam-prusty](https://github.com/goutam-prusty)).
- Linux: Intel GPUs (`i915` and `xe`, such as the Arc 130V) show their load, not just the clock. Like nvtop, the agent
  adds it up from the GPU usage counters of the running programs; without root, other users' programs aren't counted
  (#224, [comptechguy2009](https://github.com/comptechguy2009)).

## 0.9.19 — 2026-11-17

A smoother Camera widget, and the app on Android 5.0 and 6.0.

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
