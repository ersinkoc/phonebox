# User guide

[Back to PhoneBox](../README.md) · [Installation and connections](SETUP.md) · [Development](DEVELOPMENT.md)

[Appearance](#appearance) · [Control Deck](#control-deck) · [Pages & templates](#pages--templates) · [Widgets](#widgets) · [Layout editor & settings](#layout-editor--settings) · [Power standby](#power-standby) · [AI agent permission prompts](#ai-agent-permission-prompts) · [Webhooks](#webhooks) · [Rules](#rules) · [Security & privacy](#security--privacy)

## Appearance

Set corner radius, widget spacing, inner padding, page padding, borders and shadows in **Settings → General →
Appearance**. Choose Original, Rounded cards, Flat, Content only or Seamless panel, then customize individual values.
Pages and individual widgets can override global defaults. Live preview shows unsaved changes before Save.
Existing layouts keep their original appearance. See the [appearance guide](APPEARANCE.md).

**OLED burn-in protection** (since 0.9.9), under **Settings → General → OLED burn-in protection**:

- move the panel by a few pixels every 2 minutes (and the night clock around the screen);
- dim the panel after a set time without a touch. The touch that brightens it again does not press anything.

The AMOLED black theme and the night screen help too.

## Control Deck

Turn a phone into a touch control deck with application audio faders, microphone controls, device selection,
push-to-talk, audio profiles, media controls, soundboard, window and system controls, OBS, meeting shortcuts,
development tasks, MIDI and OSC. Six new templates provide starting layouts; existing layouts are retained.

Open **Settings → General → Control Deck** to define computer-side bindings and capture audio profiles. Add the
matching widgets in Pages. Executable shortcuts and macros are configured in Shortcuts first; phone requests contain
only configured IDs. PC audio listening and hold-to-confirm power buttons require separate desktop opt-ins.

The audio device list, microphone controls, per-application volume, balance and switching the default device use
Windows Core Audio, so for now they work only on Windows; on Linux and macOS the deck controls the system output's
volume and mute and the media player, and those widgets say "only on Windows for now".

**Emergency mute** mutes the computer's outputs and microphones. **Mics off, camera off** mutes only the microphones
and stops the OBS virtual camera; the speakers stay as they are. It needs OBS connected for the camera part, and tells
you on the phone when the camera could not be turned off. A physical webcam is not turned off. After either one, an
**Unmute microphones** button appears on the widget: it turns back on only the microphones that were on before the
mute, so a microphone you keep muted stays muted, and the speakers are left as they are. Changing a microphone's mute
by hand takes it off that list, and the list is forgotten when the agent restarts.

Spotify queue, library and playlist controls need a Spotify Developer Client ID and a connection through the
settings screen. OBS needs obs-websocket and widget control permission. Application routing uses an installed
SoundVolumeView executable; virtual-mixer controls use the installed Voicemeeter Remote DLL. MIDI requires an
output port. Missing integrations display their setup state. See the [setup and platform guide](CONTROL-DECK.md).

## Pages & templates

Every page is a 12 × 6 grid. Start a page from one of the 30 templates (or from scratch) and change anything.

| | |
|---|---|
| **Home** — clock, weather, now playing, CPU / GPU / RAM / network tiles | **Dashboard** — gauges, clock, media and a few shortcut keys |
| <img src="screenshots/home.png" alt="Home"> | <img src="screenshots/dashboard.png" alt="Dashboard"> |
| **AI quotas** — Claude, Codex and Grok limits, reset times, "at this pace: full in 1 d 5 h" | **Sessions** — Claude Code / Codex / Grok / Cursor / OpenCode / Antigravity / Pi sessions and GitHub |
| <img src="screenshots/ai.png" alt="AI quotas"> | <img src="screenshots/work.png" alt="Sessions"> |
| **System** — CPU, GPU, memory, disks, network and ping charts | **Processes** — heaviest apps; tap one to end an instance or all of them |
| <img src="screenshots/system.png" alt="System"> | <img src="screenshots/procs-sheet.png" alt="Processes"> |
| **Status** — quota rings, phone battery, top CPU, volume, sessions | **Ambient** — analog clock, sun & moon, air quality, quote, forecast |
| <img src="screenshots/status.png" alt="Status"> | <img src="screenshots/ambient.png" alt="Ambient"> |
| **Markets** — exchange rates, crypto, Hacker News | **Feeds** — values and notifications pushed by webhooks |
| <img src="screenshots/markets.png" alt="Markets"> | <img src="screenshots/feeds.png" alt="Feeds"> |
| **Daily** — checklist, time progress, timer, counter, stopwatch | **World** — world clocks, hourly forecast, calendar |
| <img src="screenshots/daily.png" alt="Daily"> | <img src="screenshots/world.png" alt="World"> |
| **Music** — a big now-playing card | **Shortcuts** — Stream Deck–style keys |
| <img src="screenshots/music.png" alt="Music"> | <img src="screenshots/deck.png" alt="Shortcuts"> |
| **Planner** — next up, month, agenda, checklist | **Automation** — rules, on air, pushed values, device batteries, notifications |
| <img src="screenshots/planner.png" alt="Planner"> | <img src="screenshots/automation.png" alt="Automation"> |
| **Nightstand** — word clock, moon, battery care, next hours | **Deep work** — pomodoro, next up, sessions, quota ring, rules |
| <img src="screenshots/nightstand.png" alt="Nightstand"> | <img src="screenshots/deepwork.png" alt="Deep work"> |

More templates: **Clock**, **Focus** (pomodoro + note), **Calm** (breathing), **DevOps** (command widgets) and
**Weekend** (forecast, music, moon, analog clock, quote).

**Getting around.** Swipe; press the phone's **volume keys** to flip pages; tap the page dots (or long-press an empty
spot) for an overview of all pages and jump straight to one. Webhooks and rules can switch pages too; they count down
first (*"switching to AI in 3…"*, with Stay and Go now) and never pull a page away while you are using the panel.
Settings › Display › Page navigation can also show a page bar with each page's name, or hide the dots. The panel
returns to the home page when idle (each page can stay longer, or for good), can dim to a clock at night and keeps the
screen on (unless [power standby](#power-standby) is on).

<p align="center"><img src="screenshots/overview.png" width="600" alt="Page overview"></p>

## Widgets

| Category | Widgets |
|---|---|
| **Time** | Clock (with status line and weather) · Analog clock (any time zone) · **Word clock** · World clocks · Calendar · **Next up** · Time progress (day / week / month / year) · Countdown · Pomodoro · **Focus history** · Quick timer · Stopwatch |
| **Weather & sky** | Weather · Forecast (hourly / 7 days) · Sun & moon (daylight left, UV, moon phase) · **Moon** · Air quality (European AQI, PM2.5, PM10, O₃, NO₂) |
| **Computer** | **Rules** (which hold, pause) · **Battery care** · On air (microphone / camera / OBS) · OBS Studio (live, recording, scenes) · **Dictation** (speak on the phone, typed on the PC; the app asks for the microphone on first use) · **App console** (Windows: keys and a dial for the app in front, set per app in Settings › General; use the focused app's process name from Task Manager's Details tab; packaged Store apps can all show up as `ApplicationFrameHost` and terminal tabs as `WindowsTerminal`, so a profile for those names covers all of them) · **Steam library** (installed games with covers, most recently played first; starting a game only when the widget allows it) · Device batteries (headphones, mice, keyboards) · Docker / Podman (containers, start / stop) · Metric tile · Gauge · Chart · GPU details · Memory & disks · Top processes · Processes · Now playing · Radio · Volume · **Spectrum bars** (Windows: a live spectrum of what the PC is playing, like the classic LED bar visualizers) · Shortcut grid · Big button (optionally filled with the shortcut's uploaded picture) · **Command** (PowerShell / bash / zsh output, refreshed every N seconds) · **Trackpad** (control PC cursor, tap to click, hold to drag, scroll) |
| **AI & code** | AI quotas · Quota ring · AI sessions (Claude Code, Codex, Cursor, OpenCode, Antigravity, Pi) · GitHub |
| **Personal** | **Agenda** (Google / Outlook / iCloud calendars) · Note (edit it from the phone) · Checklist (resets daily) · Counter · Breathe · Quote · Title · Phone battery · **Computers** (switch between paired computers) · **Photo frame** (a slideshow from a photo folder on the PC or an Immich album) |
| **Web & data** | **Custom HTML** (your own widget, sandboxed, or a web page by its address on Android 7 or later) · **Home Assistant** (entities with their own name and icon, tap to switch) · **Home Assistant dashboard** (a dashboard as it is, see [HOME-ASSISTANT.md](HOME-ASSISTANT.md)) · **Pushed value** · **Notifications** (both fed by [webhooks](#webhooks)) · **News feeds** (RSS and Atom feeds with categories, OPML/bulk import, and category filtering; with its "Open articles on the PC" option, a tap opens the article on the PC) · Hacker News · Exchange rates (ECB) · Crypto prices · Image / webcam · **Camera** (a live picture from an IP camera over RTSP, pulled by ffmpeg on the PC) |

Widgets scale to the size you give them. Weather, air quality, rates, crypto and Hacker News come straight from free
public APIs ([Open-Meteo](https://open-meteo.com), [Frankfurter](https://frankfurter.dev), [CoinGecko](https://www.coingecko.com),
[HN](https://github.com/HackerNews/API)) on the phone, without API keys, and keep their last data when offline.
If the phone has no internet, these five supported HTTPS feeds can fall back through the agent over USB.
The feed proxy is not available over Wi-Fi. See [PRIVACY.md](../PRIVACY.md) for the services contacted.

**Camera** shows an IP camera that streams over RTSP. Add it in **Settings › General › Cameras** with a name and its
RTSP address (`rtsp://user:password@192.168.1.20:554/…`; the camera's manual or app has the exact path), then pick it
in the widget. The agent pulls the stream with [ffmpeg](https://ffmpeg.org), which you install: `winget install
Gyan.FFmpeg` on Windows, `brew install ffmpeg` on macOS, your distribution's `ffmpeg` package on Linux. Restart
PhoneBox afterwards so it finds ffmpeg in PATH, or enter ffmpeg's full path in the same card. A camera runs only while
a Camera widget for it is on the phone's screen, at 5 frames a second by default; pick 1 to 10 next to the camera (smoother costs more CPU on the PC and more data to the phone), and expect a short delay. Choose
**UDP** for a camera that doesn't stream over TCP. At most 4 cameras play at the same time.

**News feeds** displays headlines from your RSS and Atom feeds. Configure feeds in **Settings › General › News feeds** individually or use **Import feeds (OPML / bulk)** to load feeds from an OPML export file (from Feedly, Inoreader, NetNewsWire, etc.) or paste text lines in `Category | Name | URL` (or `Name | URL`) format. Feeds can be organized into categories, and individual News feeds widgets can filter headlines by category using the **Only this category** option, or by feed name using **Only this feed**. With **Open articles on the PC** enabled, tapping a headline opens it in your computer's default browser.

**Trackpad** turns a tile into a laptop-style touchpad for the computer with precision acceleration, tap to click, two fingers to right-click, hold to drag, and two-finger scrolling or an optional edge scrollbar. For safety, mouse control is turned off by default: check **Allow controlling the mouse from the phone** in the widget's settings on an enabled page. Supported on Windows (native input), Linux (with `xdotool` under X11; Wayland sessions block synthetic mouse input) and macOS (Quartz events; the agent needs the Accessibility permission, the same one that sends keys).

On a Mac the Trackpad also has the trackpad's **multi-finger gestures**, and each Mac decides what they do from its own
**System Settings › Trackpad › More Gestures**, so a gesture that is off there does nothing here: swipe **up** with three
(or four) fingers for Mission Control, **down** for App Exposé, **left or right** to move between full-screen apps and
desktops (it presses the Mac's "Move left / right a space" shortcut as set in Keyboard Shortcuts, and does nothing while that
is off; or, set to three fingers, between pages), and with four or five fingers **spread** to show the desktop or
**pinch** to open Apps (Launchpad before macOS 26). A Mac that never saved the settings uses Apple's defaults: four fingers
for the swipes. Mission Control and Apps are opened as apps. App Exposé and Show Desktop cannot be opened that way, so the
agent sends the Dock the same notification its own gesture sends (an undocumented one): the default shortcut (Ctrl+Down, F11)
is pressed only if that call cannot be found on the Mac, and if a later macOS keeps it but the Dock stops reacting, those two
do nothing. These gestures need the phone app from the same release, and the Mac's Accessibility permission for PhoneBoxAgent
(moving between desktops and pages press keys). Some phones act on three fingers themselves, Samsung's screenshot swipe
(**Settings › Advanced features › Motions and gestures › Palm swipe to capture** and its three-finger variant) among them:
turn that off if a three-finger swipe down takes a screenshot instead of reaching the Mac.

The pad answers each touch with its own vibration: a tick for a click, two for a right click, a firmer one when a drag
starts and a longer one when a gesture fires (only for a Mac: another computer ignores gestures, so the phone does not
vibrate for them). **Tick while scrolling**, off by default, adds a light tick for each notch of a scroll. In the widget's
settings, **Haptic feedback** sets how strong they are (no vibration, light, normal or strong). The vibrations follow the
display's vibration switch and night mode like the panel's other ones.

**Steam library** shows the Steam games installed on this computer: the favorites you tick in the widget's options
first, in the order you ticked them, then the most recently played, with their covers, the game being played and update progress.
**Games shown** sets how many appear (8 by default), and **Show only the favorite games** hides the rest. The agent reads
Steam's own files, so the widget needs no Steam API key, no sign-in and no internet, and nothing is sent to Steam:
`libraryfolders.vdf` and the `appmanifest_*.acf` file of each game in every library folder, `localconfig.vdf` for when a
game was last played, covers from Steam's `appcache/librarycache`, and Steam's `RunningAppID` (the registry on Windows,
`registry.vdf` on Linux and macOS). It reads them only while the widget is on an enabled page, at most every two seconds
however many phones ask, and parses a file again only after it changed; each file has a size limit. Steam tools and
runtimes (Proton, Steamworks Common Redistributables…) are left out.

Starting a game from the phone is off by default: turn on **Let the phone start games** in the widget's options. Each
Steam library widget decides for itself: the agent checks the option of the widget a tap comes from. With
**Ask before starting** (on by default) the first tap marks the game and a second tap within four seconds starts it.
The phone sends only the game's app id; the agent starts it only if that id is installed right then, by handing
Steam's own `steam://rungameid/<id>` link to the system (Windows' URL handler, `xdg-open` on Linux, `open` on macOS).
Steam may still ask on the computer before a game starts, for example to install an update first.

**Smart Stack** combines up to 12 widgets in one slot. Add it from the Layout category, then drag widgets into it or
use its picker. Reorder cards, open their settings, or disable individual cards. Tap the dots or swipe vertically to
change cards; horizontal swipes still change pages. Focused controls pause rotation. Command widgets belong directly
on a page rather than inside a stack.

**CodexBar** is an optional source: install its `codexbar` CLI on the computer and the agent reads `usage --json`.
The CodexBar template and quota widget can show every provider reported by the CLI, with configurable provider rotation.
An optional cost overview shows the last 30 days of spend and tokens; it is off by default.
Local Claude, Codex, Antigravity and Grok sources keep refreshing when CodexBar is absent, fails, or reports stale data. General settings
provide defaults; a widget's own options override them, including an empty provider selection.
Install a compatible CLI from [CodexBar](https://github.com/steipete/CodexBar) if available for your system.
PhoneBox uses native local sources when no compatible CLI is installed.

**Which providers show** is set once in **Settings › General › AI quotas & CodexBar › Tracked AI services** (all on by
default). A Clock status line, an AI quotas widget and a CodexBar widget follow it, including widgets added later and
pages made from a template. To give one widget its own list, untick **Follow my AI providers** in its settings and pick
the providers there.
The Quota ring's provider picker offers only the providers in this list, plus Cursor. A ring keeps the provider it
already shows, so a ring set to Grok still asks for Grok quotas after Grok is turned off here.

**Claude quotas** need the sign-in of the Claude Code command-line tool (run `claude` and sign in). The Claude Desktop
app keeps its own sign-in to itself, so with only the desktop app the widget shows the plan and says so; its sessions
still show up in the Sessions widget.

**Grok quotas** need the Grok CLI sign-in (`grok login`). PhoneBox reads the token in `~/.grok/auth.json`
(`GROK_HOME` overrides that folder) and asks `cli-chat-proxy.grok.com` for the credit window, only while an enabled
page shows Grok quotas (a Quotas, CodexBar or Quota ring widget, or a Clock status line, that includes Grok). It does
not refresh the token, start `grok`, or send the token to the phone. The bar is the weekly or monthly included credits. A prepaid
balance is shown in dollars only when the reply includes one. The included credits stay a percentage.

**Codex live quotas** are off by default (normally PhoneBox reads Codex's local session logs without running `codex`).
Turn on **Read live Codex quotas with the Codex CLI** in **Settings › General › AI quotas & CodexBar** to query
account-wide limits through `codex app-server` even when no Codex session is active. It runs every few minutes while
Codex is selected in the provider list and fresh CodexBar data is unavailable. A custom widget reading `/api/quotas`
works without a built-in quota widget. On Windows the agent also looks for the CLI bundled with the Microsoft Store
Codex app when it cannot find the CLI on PATH. Failed polls back off.
Because it starts the Codex CLI, Codex may refresh its own sign-in in `~/.codex/auth.json`.

If **Claude Code quotas** stay stale because the CLI sign-in expired, run `claude auth status` under the same
user account and check whether the next quota poll picks up a fresh sign-in. Some Claude Code versions refresh
the sign-in during that command. If it reports that you are signed out, run `claude auth login`.
You can schedule `claude auth status` hourly under your user account if it refreshes the sign-in in your setup.
On Windows use Task Scheduler with the full path to the Claude CLI and select **Run only when user is logged on**.
Redirect or discard the command's output if you do not want account details recorded.

**Native Antigravity quotas** use a running app/hub's advertised HTTP port and CSRF token, the HTTPS port of newer IDE
servers (2.32 picks it at start-up, so the agent tries the loopback ports that server process listens on and accepts
its self-signed certificate only for loopback addresses), or a signed-in `agy` CLI (1.1.11 or later). The CLI fallback
runs `agy -p /usage --output-format json`, without a model prompt, and also finds `agy` when its folder is not on
PATH: in `%USERPROFILE%\.gemini\bin`, where the current Windows installer and its self-update put it, in
`%LOCALAPPDATA%\agy\bin` from older Windows installers, and in `~/.gemini/bin` on macOS and Linux. To sign in, run
`agy` in a terminal, by its full path when its folder is not on PATH (for example `%USERPROFILE%\.gemini\bin\agy.exe`
or `~/.gemini/bin/agy`). The agent reads measured session and weekly buckets, plus the plan when the local server
provides it; CLI reports without plan metadata keep the neutral Antigravity title. Older IDE servers can supply model
quotas through `GetUserStatus`; unavailable measurements are skipped, and weekly limits are never invented. Refresh
failures retain the last measurements with an error.

Tap an AI session to see its model, timing and token details. Token counters describe the local session rather than
the account allowance. Prompt/response text and tool arguments are not included in this panel data.

**Custom HTML** runs your own HTML, CSS and JavaScript in a sandbox. It cannot reach the panel, the agent or the
phone; it gets their data through `window.phonebox`: `phonebox.on('state' | 'push' | 'weather' | 'work' | 'quotas', fn)`
calls `fn` with each update (and `phonebox.data` holds the latest), `phonebox.goto('page')` switches the page,
`phonebox.run('shortcut-id')` runs a shortcut, and `phonebox.run('shortcut-id', 37)` sends it a number, for a key
that takes one ([Numbers for a shortcut](#numbers-for-a-shortcut)).

### Numbers for a shortcut

A seek bar, a volume slider or a page number needs a number, not just a tap. Give a shortcut a range in its settings
(**Shortcuts** › *Takes a number from a Custom HTML page or a script*). It is saved in `config.json` like this, with
0, 100 and 1 when a field is left out:

```json
{ "id": "seek", "label": "Seek", "cmd": "/usr/local/bin/seek-video", "args": "--percent {value}",
  "value": { "min": 0, "max": 100, "step": 0.5 } }
```

**A seek bar** in a Custom HTML widget then needs one key instead of one per position:

```html
<input id="bar" type="range" min="0" max="100" step="0.5" value="0" style="width:96%">
<script>
  bar.addEventListener('input', () => phonebox.run('seek', Number(bar.value)));
</script>
```

From a script or another program on the computer, the same run is `POST /api/run/seek?value=37`
(`curl -X POST "http://127.0.0.1:8765/api/run/seek?value=37"`). What happens with the number:

- **Checked on the computer.** The agent takes only a plain number: digits with an optional `-` and decimal part (up
  to 12 decimals), no `+`, exponent, spaces or thousands separators. It must be from the lowest to the highest
  number, on a step counted from the lowest one (with `min: 1, step: 2`: 1, 3, 5…). A range keeps to
  ±1,000,000,000,000 and 6 decimals, with a step of at least 0.000001.
- **Written by the agent.** It writes the number itself, with a dot for decimals on every computer and whole numbers
  without one (`37.50` becomes `37.5`, `40.0` becomes `40`), so only digits, a minus sign and a dot reach the command.
- **Its own argument.** A key with a range that names a program starts it directly, without the shell, with each
  argument passed on its own. The program is found by its path (a relative one from the key's working folder) or on
  the `PATH`; a bare name is looked up on the `PATH` only, not in the working folder. On Windows it is an `.exe` or
  `.com`. The number goes in place of each `{value}` in the arguments, or else at the end as one more argument.
  `{value}` must be an unquoted argument of its own, with a space or a tab between it and the next argument; it may
  start or end the arguments: `--percent {value}` works, while `--percent={value}` and `"at {value}"` are refused.
- **An address.** An address (`https:`, `shell:`, `ms-settings:`, a custom scheme…) opens through the system, which
  passes no arguments on, so it takes the number only in place of `{value}` in the address itself:
  `"cmd": "https://example.org/watch?t={value}"`. A key with a range whose command is neither a program found on the
  computer nor an address (a document, a folder, a `file:` address, a macOS `.app` bundle) refuses a number. To send
  one to an app, name a program or script that passes it on in the way the app understands (its command-line tool,
  or a script that controls it).
- **Refused with a reason.** A key without a range, an on / off key, a long press and the built-in keys (keys, text,
  macros, lock…) refuse a number with a 400, and the panel shows *The PC refused this number*. Run without a number
  (a tap on the key, a rule, a macro step), the key leaves `{value}` out and adds nothing.
- **At most 4 a second.** Each run can start a program, so a key takes at most 4 numbers a second; more answer 429.
  The page sends a dragged slider's newest number at most every 300 ms per key, and the last one when the slider
  stops. The panel sends one at a time per key, in order and at most every 300 ms even when several widgets drive
  the same key, and drops a number asked for before one it already sent. A number refused as too fast (another page or script used
  that key's 4 a second) is tried once more a second later, unless a newer one went out; refused again, the panel
  shows *The PC refused this number*.

A negative range gives arguments such as `-5`, which some programs read as an option: use a range from 0 when the
program would.

## Synced lyrics

The **Synced lyrics** widget shows the words of the song playing on the computer, the sung line lit and kept in the
middle, from [LRCLIB](https://lrclib.net), a free and open lyrics database. It isn't tied to Spotify: it uses the track
the agent shows in Now playing (title, artist, album, length and position), whichever player reports it.

Nothing is looked up until you turn on the widget's **Look up lyrics on LRCLIB** (off by default); each widget has its
own switch. Then the agent (never the phone) asks LRCLIB once per song, only for a widget with the option on while it
is on an enabled page, and sends the title, artist, album and length, nothing about you. Answers stay in memory, never on disk; after a failure the agent
leaves LRCLIB alone for a while (1 minute, doubling up to 30, or as long as LRCLIB asks).

Songs without timed lyrics show their plain lyrics, unless you turn that off. A player that reports no position (no
timeline in Now playing) gets the words as a page, with a note that the lines can't follow the song. *Timing offset*
moves the lines earlier (positive) or later when a song's timing is off.
*Timing offset per player* does the same for one player only, one `Name=ms` per line, where the name is the one **Now playing** shows for that player (for example `Spotify=-200` or `Apple Music=1000`): a song from that player uses its own value instead of the general offset, so a player that needs a lead does not shift the others. Names are not case sensitive; a player with no line, or a line without a number, uses the general offset. Values are held to ±5000 ms, like the general offset.

## Layout editor & settings

Double-click the tray icon on Windows (or open <http://127.0.0.1:8765>):

<p align="center"><img src="screenshots/settings-editor.png" width="900" alt="The layout editor"></p>

- **Pages**: add pages from templates, reorder, show/hide, export and import them as files, choose how long each page
  may stay before the panel goes home; arrange widgets on a phone-sized canvas (drag to move, drag
  the corner to resize) and set their options. The widget palette is grouped by category and searchable.
  **Landscape** and **Upright** switch the canvas between the two ways the phone can be held. Upright, a page is
  packed automatically from its landscape layout, as the panel does it; move or resize a widget there and the page
  gets an upright layout of its own (6 × 12), which a phone or tablet held upright uses as it is. Widgets left off
  it are listed under the canvas, and **Back to automatic** drops it.
  **Live preview** swaps the canvas for the real panel with your unsaved changes and live data.
- **Rules**: automations, see [Rules](#rules).
- **Shortcuts**: add, edit, reorder and test keys (label, emoji, colour). A key runs a program, presses a key
  combination (`ctrl+shift+m`), types text or runs a macro (keys, text, waits, other shortcuts); on / off keys show
  their state on the phone (mute does by itself, others from a pushed value). For a pushed value, `layout` is on
  unless it is empty, `0`, `false`, `off` or `no`; `layout=RU` is on while it is `RU`, so one value pushed to
  `/api/push/layout` can light one of several keys (one per keyboard layout, say).
  A key that runs a program or opens an address can also **take a number** (a seek position, a volume, a page): tick
  *Takes a number from a Custom HTML page or a script* and give the lowest and highest number and the step. See
  [Numbers for a shortcut](#numbers-for-a-shortcut).
- **Webhooks**: the webhook key, copy-paste examples, test buttons and the values pushed so far.
- **General**: weather location search, the temperature unit (°C or °F) and the forecast model; calendars (ICS links from Google, Outlook or iCloud), Home Assistant, OBS and [MQTT](MQTT.md)
  connections; night mode; theme and accent colour, orientation (landscape, portrait or follow the phone; how a page looks upright is set in **Pages**), clock seconds,
  vibration, the message when a session finishes, volume-key paging and the phone's sensors (brightness that follows the room, the night screen when the
  room goes dark, wave to wake it); [OLED burn-in protection](#appearance); [page switching](#rules) (home page, countdown, back home after);
  kiosk options (screen pinning, the panel as the phone's home screen so it starts after a reboot, keep the phone
  awake on the charger (or let it sleep when the computer is gone: [power standby](#power-standby)), open the
  panel when the phone is plugged in, which you can turn off for a phone you also
  use for app testing; no root needed); on macOS, optionally follow the computer lock (sleep the phone on lock,
  wake it and open PhoneBox on unlock, with an ADB connection; phones secured by a PIN or password must be unlocked
  manually). Lock sync needs the agent to run in the macOS GUI session; [battery care](#rules);
  [AI agent permission prompts](#ai-agent-permission-prompts);
  phones (several at once, each with its own pages and home page); Wi-Fi & Tailscale; updates.
- **The panel on a monitor:** `http://127.0.0.1:8765/panel/?device=monitor` (any name of letters and digits, up to 32 of them) opens the
  panel in a browser as a device of its own. It shows up under **Phones** once it has loaded, so it can get its own
  pages and home page there, like a phone; without `?device=` the browser shows every page and follows the global
  home page.
- **Photo frame** (in General): the Photo frame widget shows a folder on this computer, or one album of an Immich
  server: enter its address and an API key with the `album.read`, `asset.read` and `asset.view` permissions, press
  **Load albums** and pick one. The Immich source has been tested with Immich 3.4.2 (and earlier with 3.4.1); other versions may work.
- **Advanced**: the raw JSON.

Settings live in `%APPDATA%\PhoneBox\config.json` (Linux: `~/.config/PhoneBox`, macOS: `~/Library/Application Support/PhoneBox`);
the phone picks up changes within a few seconds.
Use UTF-8 when editing this file externally. Saved page and shortcut icons use JSON Unicode escapes to survive
ANSI-based editors; the settings editor displays the actual symbols. On startup, known double-encoded forms of
the four affected default icons are repaired, with the original file kept in `config.json.bak`.

## Power standby

By default the phone keeps its screen on while the panel runs. **Settings › General › Display › Power standby** lets a
panel wired to one computer sleep once that computer is gone, and light up again when it comes back:

- **Off** (default): the existing display and connection settings control wake, lock-screen and charging behavior.
- **Sleep when USB charge is cut**: the phone sleeps when the computer stops charging it.
- **Sleep when the agent stops answering**: the phone sleeps when the agent stops answering. This also covers a
  charge-capped port, or a board that keeps 5 V alive after shutdown.

**Standby grace (s)** (3 to 600, default 30) is how long PhoneBox waits first, so a reboot or a network hiccup does
not put the phone to sleep. Android's own screen-off timeout does the sleeping; PhoneBox only stops holding the
screen.

When the computer is back, the agent wakes the phone and opens the panel. A swipe-only lock screen is dismissed, but
only after a wake the agent started. If the phone is protected by a PIN, password or pattern, you still unlock it
yourself.

Standby only acts on the USB phone you chose under **Phone connection**. While it is on,
PhoneBox turns off Android's *stay awake while charging* setting (`stay_on_while_plugged_in`), because that system
setting would keep the screen on anyway. It saves the phone's original value and puts it back when you turn standby
off or choose another phone. If **Keep awake** is enabled, PhoneBox then applies its normal charging setting to
the selected phone.

The phone app holds no `WAKE_LOCK` permission, so the phone relights through the agent's wake key or when USB power
returns. A phone paired over Wi-Fi only (no USB) can stay dark until you touch it.

## Wake-on-LAN

A **Wake a computer** shortcut (Settings › Shortcuts › preset *Built-in: wake a computer*) turns on another machine on
your network, for example a NAS or a second PC. Put its MAC address in the arguments (`AA:BB:CC:DD:EE:FF`; dashes work
too). A second value is an optional broadcast address (`AA:BB:CC:DD:EE:FF 192.168.1.255`) for networks where the
default does not reach the machine; it has to be the broadcast address of one of this computer's own networks (or
`255.255.255.255`), because the packet never leaves the local network. The agent sends a standard "magic packet" (UDP, port 9) from every private LAN
adapter of the computer it runs on, so the key also works when the phone is away, for example over Tailscale. It
reports "packets sent", not "the machine is up": the machine has to be set up to listen.

**Waking the computer the phone is paired with.** While that computer is asleep or off, its agent is not running, so
the phone cannot ask it for anything. The Android app therefore keeps the computer's MAC addresses and shows a **Wake
the PC** button under the "PC not connected" badge. Tap it and the phone sends the magic packet itself, from its Wi-Fi,
to the computer's wired adapter; the button then reads *Waking…* until the agent answers (up to 3 minutes), and says it
did not wake after that.

The button appears only when the phone knows the computer's MAC addresses **and** is on one of the computer's own
networks: the phone's network has to hold one of the addresses the computer was paired with, so a packet is never sent
on a café's or a hotel's Wi-Fi. The computer gives its MAC addresses to the phone when Wi-Fi pairing is turned on
(Settings › General › Wi-Fi & Tailscale), over the USB cable as well as when you pair over Wi-Fi; with Wi-Fi pairing off
there are no MAC addresses and no button. If the computer later reports none (for example while its network adapter
is down), the phone keeps the ones it has. On Android 5.0 the button is not offered, because a packet cannot be
tied to the Wi-Fi network there.

What it does not do: it cannot reach across the Internet, Tailscale or mobile data (a magic packet is a broadcast on
the local network), it cannot wake a machine over Wi-Fi in most cases (use a cable), and, like every Wake-on-LAN
packet, it carries no password, so anyone on the same network who knows a MAC address can wake that machine.

**Setting up the machine to be woken (Windows):**
1. In the BIOS / UEFI, turn on *Wake on LAN* (sometimes *Power on by PCI-E* or *Resume by LAN*).
2. Device Manager › Network adapters › your Ethernet adapter › Properties › Advanced: *Wake on Magic Packet* on.
3. Same dialog › Power Management: *Allow this device to wake the computer* on, and *Only allow a magic packet to wake
   the computer* on.
4. Waking from sleep or hibernate works most reliably. Waking from a full shutdown depends on the board, and
   Windows *Fast startup* can interfere: turn it off if a shut-down machine does not wake.

## Phone bridge (macOS)

Share what you copy between the Mac and the phone. It starts with the clipboard.

- Turn on **Settings › Phone bridge › Share the clipboard with the phone**. It is off by default, and while it is off
  the agent refuses everything the phone sends for it.
- **Mac to phone:** copy plain text on the Mac and the paired phone's clipboard has it within about a second. Text over
  64 KiB is not sent, and neither is anything a password manager marked as not to be shared (concealed, transient or
  auto-generated, the markers 1Password, Bitwarden, KeePassXC and Strongbox use). A copy is read only once the clipboard
  has stayed the same for a moment. A password manager can omit a marker or add it after the bridge has read the copy.
  Turn off sharing before copying sensitive text.
  On Android 9 and older, every app on the phone can read the clipboard at any time. From Android 10, the foreground
  app and the default keyboard can read it.
- **Phone to Mac:** Android does not let an app read the clipboard in the background, so sending from the phone is
  something you do, in any of four places. **Send to Mac** in the text-selection menu (select text, it sits next to
  Copy, and the text is sent without copying first) and **Share › Send to Mac** ask first, because any app can hand text
  to them: a dialog shows the start of the text, says how much more there is that it does not show, and sends it when
  you tap **Send to Mac** there (the button works after half a second). A **Send to Mac** tile (add it under Quick
  Settings › Edit) and the **Send to Mac** button on the app's "sharing the clipboard" notification are one tap and send
  what is on the clipboard straight away. A short message says whether it arrived. These entries appear in your apps
  only while the phone's switch below is on.
- **On the phone:** open the page overview (long-press the page dots) and tap **Phone bridge**, then turn on **Share
  the clipboard with the Mac**. Both switches, this one and the Mac's, must be on; the screen says whether it is
  connected, the Mac's switch is off, the computer has no bridge, the phone must be paired again (the Mac was paired
  again or got a new key), or Android stopped the service (opening the screen starts it again). Android 13 and later asks to show notifications. On Samsung phones set PhoneBox's battery use to
  **Unrestricted** (Settings › Apps › PhoneBox › Battery), or the system may stop the service.
- **Notifications on the Mac:** turn on **Settings › Phone bridge › Show phone notifications on this Mac**. Calls (who is
  calling), messages and the apps you allow on the phone then appear as macOS notifications, with the text of the
  message. They carry the PhoneBox icon, with the phone app's own icon (Facebook, Messages) as the picture. The first time, the agent builds
  a small program for that on your Mac (about 20 seconds, needs Xcode or the command line tools, nothing is downloaded) and
  macOS asks whether PhoneBox may show notifications: allow it. Until then, or if it cannot be built, the notifications carry
  Script Editor's icon, which is how macOS labels a notification from a program that has no app of its own. A banner stays for about five seconds and then waits in **Notification Center** (click the clock). **Send a
  test notification** shows one right away; save the settings first.
- **Notifications from the phone:** on the Phone bridge screen turn on **Show notifications on the Mac**, then tap **Allow
  notification access** and switch PhoneBox on in the list (on Android 13 and later the switch is greyed out until you
  open **App info**, tap ⋮ and choose **Allow restricted settings**; the screen says so). The **Apps shown on the Mac** card below lists
  your apps with a switch each and a search box: only the ones you turn on are sent. At first only the phone's own dialer and messaging app are on.
  **All apps** sends every app instead (banking and other private notifications too), so it is off until you turn it
  on; the list is hidden while it is on. The Mac shows each app's icon as the notification's picture.
  The Mac's switch (Settings › Phone bridge › Show phone notifications) must be on too; the screen shows whether the
  last notification reached the Mac.
- **Login codes:** with **Copy one-time codes from messages** on as well, a code in a message ("Your verification code is
  482913", also in Greek) is put on the Mac's clipboard when the notification arrives, and the notification says "Code
  copied", so you can paste it into the site at once. It copies only when it finds exactly one code next to a word like
  code, OTP, PIN or κωδικός; with two numbers, or none, it copies nothing. It knows English and Greek words only, so a
  code in a message in another language is shown but not copied. It does not send the code on to your phones.
- **Calls and delivery:** a call is sent once, when it starts ringing, and is not repeated when you answer or hang up; a
  missed call is sent as "Missed call". Calls show who is calling; the voice stays on the phone. Answer there. If the Mac is asleep or away, notifications are kept for two minutes and sent when it
  is back, then dropped (a login code older than that is no use). On a Samsung phone a ringing call is posted by the in-call
  app rather than the dialer, so it is sent whenever the default dialer is on your app list (other apps' calls, such as
  WhatsApp, follow your list like any notification); an outgoing call looks the same there and is sent too. Opening the
  Phone bridge screen while notifications are enabled can prompt once for **Contacts**. If Android shows only the
  number and you allow access, the caller's name is shown on the Mac instead of the
  number (one lookup of that number, nothing is read in bulk or kept), and a name that arrives a moment later replaces
  the first notification.
- The text goes only to your own paired phones over the PhoneBox link (USB or paired Wi-Fi / Tailscale), with the
  phone's key. Enable the bridge only on a computer where users and software with access to the local agent API are
  trusted. The text is kept in memory and never stored or logged by PhoneBox (macOS itself keeps a notification in
  Notification Center until you clear it). A phone whose connection drops for a moment is sent what it missed when it
  reconnects (at most the last 20 copies, and only those from the last two minutes). See [PRIVACY.md](../PRIVACY.md).
- **macOS asking before an app reads the clipboard:** newer macOS versions can ask whether an app may paste from other
  apps (System Settings › Privacy & Security). If macOS asks about PhoneBox, choose Allow there, or it asks again at each
  copy. If PhoneBox is denied, macOS gives it no text, nothing you copy reaches the phone, and the Phone bridge card says
  so (the log gets one line too, without the text).
- Needs macOS: on Windows and Linux the card is hidden and the agent answers the phone with 501.
- **The phone's music in Now playing (Android):** on the Phone bridge screen turn on **Show the phone's music in Now
  playing**. It is off by default and needs the app's **Notification access** (it reads the phone's media sessions, not
  your notifications). The widget then shows what the phone plays while the computer plays nothing, and its buttons
  (previous, play/pause, next, seek, volume) control that app on the phone. When the phone has a song a small
  computer | phone switch sits top right of the widget to pick which side to follow. The song stays on the phone: it is
  not sent to the computer and Custom HTML widgets never see it. It also works while the computer is off.

## AI agent permission prompts

Turn on **Sound when a permission prompt appears** in **Settings › General › Display** to hear two short
beeps at the configured sound volume. The phone stays quiet on the night screen. A desktop browser may
require a tap or click on the panel after opening it to allow audio.

When Codex asks for permission (run a command, edit a file, use an MCP tool…), the phone shows the project, tool and
operation with **Allow** and **Deny**. Turn it on in **Settings › General › AI coding agent permission prompts** and
install the Codex hook. PhoneBox adds a `PermissionRequest` command hook to `~/.codex/hooks.json`, preserving other
hooks and keeping a backup next to the file. Codex requires new or changed hooks to be reviewed once: start a new
Codex session and trust the PhoneBox hook from `/hooks` (CLI) or **Settings › Hooks** (desktop app). Requires a Codex
release with `PermissionRequest` command hooks (tested with Codex 0.170.0; on older builds that gate hooks behind a
feature flag, enable `codex_hooks = true` under `[features]` in `~/.codex/config.toml`).

When Claude Code asks for permission (run a command, edit a file, use an MCP tool…), the phone shows the request —
project, tool and the command or file — with **Allow** and **Deny**. Turn it on in **Settings › General › Claude Code
permission prompts** and press *Install hook*: PhoneBox adds a `PermissionRequest` hook to `~/.claude/settings.json`
(and keeps a backup next to it as `settings.json.phonebox.bak`). If the phone is not connected or nobody answers in
time, the hook returns no decision and Claude Code asks in the terminal as usual. Answering in Claude Code's own
window removes the card from the phone too: *Install hook* also adds short `PostToolUse`, `Stop` and
`UserPromptSubmit` hooks that tell PhoneBox Claude Code went on, and *Remove hook* takes them out again. Optionally only when you have been
away from the keyboard for a minute. Each hook (Claude Code, Codex and Cursor) keeps the seconds to answer it was
installed with. A shorter time applies to the phone card at once; for a longer one, remove the hook and install it
again, or the hook gives up before the phone does. To have a reinstalled Cursor hook cover WSL as well, turn on **Also
watch Cursor in WSL** and start the distro first: with the option off, *Install hook* writes only the Windows hook.

With other Claude Code accounts set up (the folders under **Other Claude Code accounts**), *Install hook* and *Remove
hook* also write the `settings.json` of each of those folders that exists, with the same backup. The settings list
those files under the button before you press it, and a folder that doesn't exist is skipped, never created. Remove
the hook before you delete an account: *Remove hook* only reaches the folders still in the list.

WorkBuddy has its own controls under **Settings › General › AI coding agent permission prompts**.
Turn on **Ask on the phone** in the WorkBuddy section. Enable **Allow WorkBuddy approvals on the phone**
in an AI sessions widget on an enabled page. Save the settings, then click **Install hook** in the WorkBuddy
section. The installer uses the saved timeout and writes a `PermissionRequest` hook to `~/.workbuddy/settings.json`
and keeps the existing settings. Restart WorkBuddy after installation and use its default permission mode to see prompts.
To avoid duplicate prompts, remove any old PhoneBox entry you copied manually before installing this one.
The event provides the session ID, working directory, tool name and tool input. The phone returns Allow or Deny.
If the phone does not answer, WorkBuddy keeps its own approval flow.
[Tencent hook reference](https://cloud.tencent.com/document/product/1831/137030#permissionrequest).

**Show WorkBuddy sessions** is a separate switch. It reads the live process status files under `~/.workbuddy/sessions`
and displays the workspace name and activity. No chat titles or conversation text are read from those files.

Cursor has no permission-prompt hook; its `beforeShellExecution` and `beforeMCPExecution` hooks run before **every**
shell command and MCP tool call of the agent, also the ones Cursor would run by itself. So Cursor prompts are off until
you turn on **Ask on the phone for Cursor** and press *Install hook* next to Cursor in **Settings › General › AI coding
agent permission prompts**: PhoneBox adds both hooks to `~/.cursor/hooks.json` (keeping your own hooks and a backup as
`hooks.json.phonebox.bak`). This switch is Cursor's own: Claude Code's *Ask on the phone* and *Only when I am away*
do not apply to it, while the seconds to answer are shared. While a phone is connected, each command then waits for
**Allow** or **Deny** on it; Allow
runs it without Cursor's own prompt, Deny blocks it and tells the agent so. Without a phone, without an answer within
the timeout, or with the agent not running, the hook prints nothing and Cursor goes on as it would without it.
Projects opened over Remote - WSL run their hooks inside the distro, from its own `~/.cursor/hooks.json`: with **Also
watch Cursor in WSL** on, *Install hook* writes there too (in each running distro where Cursor's server has been used);
PhoneBox remembers the distros it installed to, so *Remove hook* still takes it off there after WSL watching is
turned off (once the distro runs: a stopped distro is never started for it). With WSL watching off and no hook
installed in a distro, the settings page does not look into WSL at all. That hook reaches the agent on `127.0.0.1`,
which works when WSL shares the loopback with Windows
(`networkingMode=mirrored` in `.wslconfig`); otherwise it fails and Cursor goes on as usual.

OpenCode needs no hook: prompts are off until you turn them on — press the **Ask on the phone** switch under
**Settings › General › AI coding agent permission prompts** (or set `"phoneApprovals": true` in the `opencode` section
of `config.json`). While it is on, when OpenCode's background service (`opencode serve --service`, which the desktop
app and the CLI keep running) asks for permission — run a command, edit a file, fetch a page — PhoneBox reads it from
`~/.local/state/opencode/service.json`'s local API and shows the same card, with **Allow**, **Always allow** and
**Deny**. Allow answers once, Always allow also saves OpenCode's proposed pattern for the project (its own "saved
approval") — the card shows which pattern that would be before the countdown ends — and Deny rejects. If nobody answers
within 90 seconds (`"timeoutSec"` in the `opencode` section of `config.json`), or the prompt is answered in OpenCode
first, the card simply goes away and OpenCode's window keeps the question. Turning the switch off again leaves every
prompt to OpenCode.

OpenCode inside WSL works the same way, but only for distros that are already running, and only with **Also watch
OpenCode in WSL** on (off by default). Running distros are found with `wsl.exe --list --running`, re-checked right
before the distro's files are read, and a stopped distro is never started for a check. Its prompts, questions and
sessions reach the phone like the native ones, and each answer goes back to the service that asked.

OpenCode sessions (opt-in: **Show OpenCode sessions**, off by default) show OpenCode's own session title with the
project folder underneath once **Show OpenCode session titles** is also on (off by default: OpenCode draws the titles
from the conversation, so they stay off the phone until you share them, like Claude Code's).

Antigravity sessions (opt-in: **Settings › General › Antigravity sessions**, off by default) show recent local
Antigravity IDE and CLI chats with their model, tool and token metadata (prompts, replies and tool arguments are never
retained or sent). Chat titles reach the phone only when **Show Antigravity chat titles on the phone** is also on.

Pi sessions (opt-in: **Settings › General › Pi sessions**, off by default) show recent local [Pi](https://github.com/earendil-works/pi)
agent sessions from `~/.pi/agent/sessions` (or `$PI_CODING_AGENT_DIR/sessions`; a leading `~` there is expanded the
way Pi expands it), with the model, thinking level, token counters, the current context size and the last tool
(prompts, replies and tool arguments are never retained or sent). A session reads as working while its last record is a
prompt, a tool result, or an assistant reply that carries a tool call, and as finished when the turn ended with a stop,
an abort, or an error — the usual buzz and "session finished" toast apply. A failure is reported at once; when Pi
retries it, the recovery it writes to the same file puts the session back to working, and the retried reply ends it. A
turn that writes nothing for half an hour settles as idle with no outcome, so a session Pi was killed in neither reads
"working" for hours nor claims a finish. The context window comes from Pi's cached model store, the file next to the
sessions folder, so a built-in model that Pi has not cached shows the context size alone. Session titles reach the
phone only when **Show Pi session titles on the phone** is also on; with it off no title is read at all.

OpenCode's agent questions work the same way: when the agent asks "route A, B or C, or type your own", the phone
shows the choices as buttons (and a text field when a custom answer is allowed) and sends the pick back. Questions
without tappable answers, and Claude Code's own questions, are shown for information only — they are answered at
the PC.

## Webhooks

The agent accepts data as well as serving it, so anything that can make an HTTP request can put things on the panel:

```sh
curl -X POST http://127.0.0.1:8765/api/push/temperature -d 21.5                     # shown by a "Pushed value" widget with key "temperature"
curl -X POST http://127.0.0.1:8765/api/push/build -H "Content-Type: application/json" \
     -d '{"value":"passed","sub":"main · 3m 12s"}'
curl -X POST "http://127.0.0.1:8765/api/push/door?ttl=600" -d open                    # expires after 10 minutes
curl -X POST http://127.0.0.1:8765/api/notify -H "Content-Type: application/json" \
     -d '{"title":"Deploy","text":"Production is live","level":"ok","page":"feeds"}' # toast + vibration (+ switch page)
curl -X POST http://127.0.0.1:8765/api/goto/music                                     # switch the phone to a page (id or number)
curl -X POST "http://127.0.0.1:8765/api/goto/music?countdown=0"                        # …without the on-screen countdown
curl -X POST http://127.0.0.1:8765/api/goto/next                                       # one page forward (or /api/goto/prev back)
curl -X POST http://127.0.0.1:8765/api/refresh                                        # refresh quotas/GitHub and the panel now (?reload=1 reloads it)
```

| Payload field | Meaning |
|---|---|
| `value`, `title`, `sub`, `unit`, `icon`, `color` | What the widget shows. Plain text or a number works too. |
| `level` | `ok` / `warn` / `error` colours it; values like `passed`, `failed`, `running` are coloured automatically. |
| `progress` | 0–1 or 0–100: a bar under the value, or the ring in gauge style. |
| `items` | A list (strings or `{label, value, level}`) for the list style. |
| `ttl` | Seconds until the value disappears (also `?ttl=`). |

Notifications take `title`, `text`, `level`, `vibrate`, `seconds`, `wake` (light up the night screen) and `page`.
`GET /api/push` lists the values and `DELETE /api/push/<key>` removes one; values survive restarts.

From this PC no key is needed. With Wi-Fi on, other machines on the network or Tailscale can use the same endpoints
on `https://<pc>:8766` with the **webhook key** from the settings (`Authorization: Bearer <key>`, or `?token=<key>`
for senders that cannot set headers). That key opens only the webhook endpoints, never the phone API.

To show whether a GitHub Copilot CLI session is working or waiting for you (also from WSL2), see the
[Copilot CLI guide](COPILOT-CLI.md).

## Rules

**Settings › Rules** lets the panel react to what happens on the computer. Each rule is *when → then*:

| When | Then |
|---|---|
| an AI session needs you / is working (optionally for some projects) | show a page |
| music or video plays (optionally in some apps: `Spotify\|Music`) | show a notification (`{detail}` = what happened) |
| an app is running (`zoom\|Teams\|obs64`) | push a value (e.g. `onair`, shown by a Pushed value widget) |
| an app is in front (`Code\|obs64\|Safari` — needs Accessibility on macOS, `xdotool` on Linux; not on Wayland) | |
| the microphone / the camera is in use (Windows, Linux, macOS. On macOS 14.2 and later the microphone is named by its app; before that a rule sees only "microphone", for an input device with no output side (a USB or built-in microphone), so an app filter cannot match. The camera is named by the camera itself, as macOS does not say which app runs it, and **the camera has not been tried on real hardware yet**: macOS may report it as running only while the agent has a run loop, so on a Mac with a built-in camera check once, with Photo Booth or FaceTime on and then off, and tell us if it stays "in use". On a browser call the name is the process macOS lists, so a filter has to match what the list shows: `Google Chrome Helper`, `com.apple.WebKit.GPU` and the like, not just the app's name) | |
| a calendar event is on (optionally by title) | |
| OBS is recording or streaming | |
| the CPU stays above a percentage for 15 s | |
| the CPU temperature stays at or above a limit (85 °C by default) for 15 s, read from the hardware sensors; no reading, no trigger | |
| the phone's battery temperature is at or above a limit (40 °C by default), from the battery report the Android app sends every 30 s; a report older than 3 minutes no longer counts. The iOS app sends no battery report, so this never fires for an iOS panel | |
| you have been away from the keyboard for N minutes | |
| it is between two times (`22:00-07:00`) | |
| a pushed value is on, or above / at least / below / at most / equal to a value (a webhook from Home Assistant, a doorbell…) | run a shortcut |
| a pushed value has had no update for N seconds (a sensor or a bridge stopped posting). A missing value (never pushed, deleted, or expired by its `ttl`) counts at once, however recent its last push, but only after the agent has run for N seconds | |
| an AI quota is above a percentage | |
| a GitHub review is requested · CI fails on my pull request | |
| a device battery is low · the network is slow or unreachable | |
| always (with the days and hours below, for schedules) | |

Every rule can also be limited to **some days** and **some hours**, act only when its condition **lasts N seconds**,
**rest** for a while before acting again, and has a **priority** and its own **countdown** for page switches.

**Page switches are calm.** The phone counts down before a rule changes the page (*"Claude needs me — switching to
AI in 3…"*, tap **Stay** to keep the page or **Go now**), waits while you are using the panel, keeps a minimum time
between two automatic switches and lets the higher-priority rule go first; rules that end before their turn are
dropped, so a burst of rules never bounces the screen back and forth. Settings › General › Page switching sets the
countdown, how long to wait after a touch, the minimum time between switches, the home page and how long a rule may
keep its page (30 minutes by default, so a rule that never ends can't hold the panel for good; 0 keeps it for as long
as the rule lasts). The phone's overview (and the Rules widget) pauses all rules for an hour.

With **only while it lasts**, the effect ends with the condition: the phone goes back to the page it was on (unless
you moved on yourself) and stays on the rule's page instead of returning home when idle; a pushed value is removed
again. The editor starts from examples (Claude needs me → AI page, music → Music page, on air during calls, CPU busy,
away → Ambient, evenings → Calm, doorbell, quota warning, review requested, CI failed, workday mornings, low battery,
too warm). The Test button runs one-shot actions. Use **Show the night screen** to dim the phone while a
rule is active, such as after five minutes away from the PC. A tap temporarily wakes the screen. The
normal night schedule applies independently.

**Battery care** (Settings › General) keeps the phone's battery between two levels, 50–80 % by default, so a panel
that is always plugged in lasts longer. Phones cannot stop charging without root, so PhoneBox switches the power
where it comes from: a smart plug in Home Assistant, or two shortcuts (for example `uhubctl` for a USB hub with
switchable ports). The panel keeps running over Wi-Fi while the USB power is off.

## Security & privacy

- The agent listens on `127.0.0.1` only. Requests with a foreign `Host` header (DNS rebinding) or a cross-site
  `Origin` (CSRF) are rejected; only the settings page itself is accepted from browsers.
- The optional Wi-Fi port (8766) only serves the panel API, only over TLS pinned to the paired certificate, and only
  with the paired key. Settings, shortcut tests and pairing stay local. **New key** in the settings unpairs all phones.
- The phone only ever sends a shortcut **id** (and, for a key that takes one, a number the agent checks and writes
  out itself); commands are defined on the PC. Command widgets work the same way:
  the phone asks for a widget's output and never sees the command.
- `config.json` (with the Wi-Fi, webhook and Home Assistant keys), its backup and the pushed data are readable only by
  your user on macOS and Linux (0600); on Windows they live in your profile folder.
- Webhooks from other machines need the separate webhook key, which cannot open the phone API and can be replaced
  on its own.
- Ending processes is limited to your own session, never touches a list of Windows-critical processes, and can be
  switched off in the processes widget's options.
- **AI agent permission prompts** are opt-in. Codex and Claude Code hooks only post the request to the agent on `127.0.0.1`; the
  phone can only answer allow or deny for that one request.
- **OpenCode permission prompts** (opt-in, off by default) are read from OpenCode's background service on `127.0.0.1`,
  with the password OpenCode itself wrote to `~/.local/state/opencode/service.json`; that password never leaves this
  computer and the service address is accepted only on this machine's loopback interface. The phone answers one
  prompt at a time (allow, always allow or deny) and sees the command, any message and the pattern an always allow
  would save; nothing else about the session is sent.
- **Claude quota:** read with the OAuth token Claude Code stores in `~/.claude/.credentials.json`, sent only to
  `api.anthropic.com/api/oauth/usage` (the endpoint behind Claude Code's `/usage`). The token is read-only: PhoneBox
  never refreshes it, so it can't interfere with Claude Code. This endpoint is undocumented and may change.
  It is rate limited per account and shared with Claude Code, so PhoneBox asks at most every 3 minutes, and waits
  longer after errors and as long as a `429` asks for, across restarts too. An expired or refused token is not sent:
  PhoneBox waits until Claude Code renews it.
- **Codex quota and sessions** are read from local files in `~/.codex/sessions`. With the opt-in
  **Read live Codex quotas with the Codex CLI** setting (**Settings › General › AI quotas & CodexBar**, off by
  default), PhoneBox also queries `codex app-server` about every 3 minutes (backing off on failures) while Codex is
  selected in the provider list and fresh CodexBar data is unavailable. No coding session or inference prompt is
  required, and local session quota stays as fallback. Claude Code sessions come from
  `~/.claude/sessions`. Grok sessions come from local files under `~/.grok/sessions` (or `GROK_HOME`): the project
  folder, status, model, reasoning effort and token counters. The chat, its title and the cost stay on this computer.
  OpenCode sessions (opt-in, off by default) come from its background service's local HTTP API —
  the native one and each running WSL distro's — with the password OpenCode wrote to `~/.local/state/opencode/service.json`;
  only the session's folder, title, model, token counters, times and outcome are read, never prompts, replies or tool
  arguments. Cursor sessions (opt-in in **Settings › General › Cursor sessions**) come from recent agent transcripts
  under `~/.cursor/projects/*/agent-transcripts`. Only the last message's role is kept; chat text is never shown,
  stored or sent. Project names use the folder basename from Cursor's `workspaceStorage`. Pi sessions (opt-in in
  **Settings › General › Pi sessions**) come from recent `*.jsonl` files under `~/.pi/agent/sessions` (or
  `$PI_CODING_AGENT_DIR/sessions`); only the
  working directory, session id, model, thinking level, token counters, stop reasons, tool names and times are kept,
  never prompts, replies or tool arguments.
- **Claude Code and Cursor inside WSL** (Windows, opt-in): **Also watch Claude Code in WSL** (Settings › General, next to the Claude Code options)
  and **Also watch Cursor in WSL** (Settings › General › Cursor sessions) read the same files in the running distros'
  default users' homes, through `\\wsl.localhost\<distro>`, like OpenCode in WSL: only distros that are already
  running (`wsl.exe --list --running` right before each read; a stopped one is never started) and every 5 seconds. A
  Claude Code session counts as running while its process is alive in the distro's `/proc` with the start time Claude
  Code wrote. *Show the conversation* reads a WSL session's chat file from the distro too. Without a Claude Code
  sign-in on Windows (no file, or only Claude Desktop's plan), the Claude quota uses the one in WSL
  (`~/.claude/.credentials.json`).
  Cursor chats of projects opened over Remote - WSL are named after their folder.
- **Native Antigravity quota:** the agent reads the running Antigravity server's port and CSRF token from its
  process arguments and queries its loopback quota/status API, or invokes the signed-in CLI's structured usage
  command. Only measured quota fields reach the panel; CLI conversation identifiers and token counters are omitted.
  PhoneBox does not manage Google OAuth credentials; the CLI uses its own authentication. CLI auto-updates are
  disabled for these probes.
- **No telemetry**: no accounts, no analytics, no crash reports. The [privacy policy](../PRIVACY.md) lists every
  connection the agent and the app make (update check, weather, the widgets that use public APIs) and how to turn
  each one off.
