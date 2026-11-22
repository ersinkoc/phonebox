# User guide

[Back to PhoneBox](../README.md) · [Installation and connections](SETUP.md) · [Development](DEVELOPMENT.md)

[Appearance](#appearance) · [Control Deck](#control-deck) · [Pages & templates](#pages--templates) · [Widgets](#widgets) · [Layout editor & settings](#layout-editor--settings) · [AI agent permission prompts](#ai-agent-permission-prompts) · [Webhooks](#webhooks) · [Rules](#rules) · [Security & privacy](#security--privacy)

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
| **AI quotas** — Claude and Codex limits, reset times, "at this pace: full in 1 d 5 h" | **Sessions** — Claude Code / Codex / Cursor / OpenCode / Antigravity sessions and GitHub |
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
first (*"switching to AI in 3…"*, with Stay and Go now) and never pull a page away while you are using the panel. The
panel returns to the home page when idle (each page can stay longer, or for good), can dim to a clock at night and
keeps the screen on.

<p align="center"><img src="screenshots/overview.png" width="600" alt="Page overview"></p>

## Widgets

| Category | Widgets |
|---|---|
| **Time** | Clock (with status line and weather) · Analog clock (any time zone) · **Word clock** · World clocks · Calendar · **Next up** · Time progress (day / week / month / year) · Countdown · Pomodoro · Quick timer · Stopwatch |
| **Weather & sky** | Weather · Forecast (hourly / 7 days) · Sun & moon (daylight left, UV, moon phase) · **Moon** · Air quality (European AQI, PM2.5, PM10, O₃, NO₂) |
| **Computer** | **Rules** (which hold, pause) · **Battery care** · On air (microphone / camera / OBS) · OBS Studio (live, recording, scenes) · **Dictation** (speak on the phone, typed on the PC; the app asks for the microphone on first use) · **App console** (Windows: keys and a dial for the app in front, set per app in Settings › General; use the focused app's process name from Task Manager's Details tab; packaged Store apps can all show up as `ApplicationFrameHost` and terminal tabs as `WindowsTerminal`, so a profile for those names covers all of them) · **Steam library** (installed games with covers, most recently played first; starting a game only when the widget allows it) · Device batteries (headphones, mice, keyboards) · Docker / Podman (containers, start / stop) · Metric tile · Gauge · Chart · GPU details · Memory & disks · Top processes · Processes · Now playing · Radio · Volume · **Spectrum bars** (Windows: a live spectrum of what the PC is playing, like the classic LED bar visualizers) · Shortcut grid · Big button (optionally filled with the shortcut's uploaded picture) · **Command** (PowerShell / bash / zsh output, refreshed every N seconds) · **Trackpad** (control PC cursor, tap to click, hold to drag, scroll) |
| **AI & code** | AI quotas · Quota ring · AI sessions (Claude Code, Codex, Cursor, OpenCode, Antigravity) · GitHub |
| **Personal** | **Agenda** (Google / Outlook / iCloud calendars) · Note (edit it from the phone) · Checklist (resets daily) · Counter · Breathe · Quote · Title · Phone battery · **Computers** (switch between paired computers) · **Photo frame** (a slideshow from a photo folder on the PC or an Immich album) |
| **Web & data** | **Custom HTML** (your own widget, sandboxed) · **Home Assistant** (entities with their own name and icon, tap to switch) · **Pushed value** · **Notifications** (both fed by [webhooks](#webhooks)) · **News feeds** (RSS and Atom feeds with categories, OPML/bulk import, and category filtering; with its "Open articles on the PC" option, a tap opens the article on the PC) · Hacker News · Exchange rates (ECB) · Crypto prices · Image / webcam · **Camera** (a live picture from an IP camera over RTSP, pulled by ffmpeg on the PC) |

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
Local Claude, Codex and Antigravity sources keep refreshing when CodexBar is absent, fails, or reports stale data. General settings
provide defaults; a widget's own options override them, including an empty provider selection.
Install a compatible CLI from [CodexBar](https://github.com/steipete/CodexBar) if available for your system.
PhoneBox uses native local sources when no compatible CLI is installed.

**Claude quotas** need the sign-in of the Claude Code command-line tool (run `claude` and sign in). The Claude Desktop
app keeps its own sign-in to itself, so with only the desktop app the widget shows the plan and says so; its sessions
still show up in the Sessions widget.

**Codex live quotas** are off by default (normally PhoneBox reads Codex's local session logs without running `codex`).
Turn on **Read live Codex quotas with the Codex CLI** in **Settings › General › AI quotas & CodexBar** to query
account-wide limits through `codex app-server` even when no Codex session is active. It runs every few minutes only
while an enabled page shows Codex quotas and fresh CodexBar data is not already winning, and backs off on failures.
Because it starts the Codex CLI, Codex may refresh its own sign-in in `~/.codex/auth.json`.

**Native Antigravity quotas** use a running app/hub's advertised HTTP port and CSRF token, the HTTPS port of newer
IDE servers (2.32 picks it at start-up, so the agent tries the loopback ports that server process listens
on and accepts its self-signed certificate only for loopback addresses), or a signed-in
`agy` CLI (1.1.11 or later). The CLI fallback runs `agy -p /usage --output-format json`, without a model prompt,
and also finds the standard Windows installation in `%LOCALAPPDATA%\agy\bin`. Run `agy` in your terminal to sign
in if needed. The agent reads measured session and weekly buckets, plus the plan when the local server provides it;
CLI reports without plan metadata keep the neutral Antigravity title. Older IDE servers can supply model quotas
through `GetUserStatus`; unavailable measurements are skipped, and weekly limits are never invented. Refresh
failures retain the last measurements with an error.

Tap an AI session to see its model, timing and token details. Token counters describe the local session rather than
the account allowance. Prompt/response text and tool arguments are not included in this panel data.

## Layout editor & settings

Double-click the tray icon on Windows (or open <http://127.0.0.1:8765>):

<p align="center"><img src="screenshots/settings-editor.png" width="900" alt="The layout editor"></p>

- **Pages**: add pages from templates, reorder, show/hide, export and import them as files, choose how long each page
  may stay before the panel goes home; arrange widgets on a phone-sized canvas (drag to move, drag
  the corner to resize) and set their options. The widget palette is grouped by category and searchable.
  **Live preview** swaps the canvas for the real panel with your unsaved changes and live data.
- **Rules**: automations, see [Rules](#rules).
- **Shortcuts**: add, edit, reorder and test keys (label, emoji, colour). A key runs a program, presses a key
  combination (`ctrl+shift+m`), types text or runs a macro (keys, text, waits, other shortcuts); on / off keys show
  their state on the phone (mute does by itself, others from a pushed value).
- **Webhooks**: the webhook key, copy-paste examples, test buttons and the values pushed so far.
- **General**: weather location search, the temperature unit (°C or °F) and the forecast model; calendars (ICS links from Google, Outlook or iCloud), Home Assistant and OBS
  connections; night mode; theme and accent colour, orientation (landscape, portrait or follow the phone), clock seconds,
  vibration, the message when a session finishes, volume-key paging and the phone's sensors (brightness that follows the room, the night screen when the
  room goes dark, wave to wake it); [OLED burn-in protection](#appearance); [page switching](#rules) (home page, countdown, back home after);
  kiosk options (screen pinning, the panel as the phone's home screen so it starts after a reboot, keep the phone
  awake on the charger, open the panel when the phone is plugged in, which you can turn off for a phone you also
  use for app testing; no root needed); on macOS, optionally follow the computer lock (sleep the phone on lock,
  wake it and open PhoneBox on unlock, with an ADB connection; phones secured by a PIN or password must be unlocked
  manually). Lock sync needs the agent to run in the macOS GUI session; [battery care](#rules);
  [AI agent permission prompts](#ai-agent-permission-prompts);
  phones (several at once, each with its own pages); Wi-Fi & Tailscale; updates.
- **Photo frame** (in General): the Photo frame widget shows a folder on this computer, or one album of an Immich
  server: enter its address and an API key with the `album.read`, `asset.read` and `asset.view` permissions, press
  **Load albums** and pick one. The Immich source has been tested with Immich 3.4.2 (and earlier with 3.4.1); other versions may work.
- **Advanced**: the raw JSON.

Settings live in `%APPDATA%\PhoneBox\config.json` (Linux: `~/.config/PhoneBox`, macOS: `~/Library/Application Support/PhoneBox`);
the phone picks up changes within a few seconds.
Use UTF-8 when editing this file externally. Saved page and shortcut icons use JSON Unicode escapes to survive
ANSI-based editors; the settings editor displays the actual symbols. On startup, known double-encoded forms of
the four affected default icons are repaired, with the original file kept in `config.json.bak`.

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

## AI agent permission prompts

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
time, the hook returns no decision and Claude Code asks in the terminal as usual. Optionally only when you have been
away from the keyboard for a minute.

WorkBuddy speaks the same hook protocol. Add the same `PermissionRequest` entry to `~/.workbuddy/settings.json` by
hand: *Install hook* writes to `~/.claude/settings.json` only. WorkBuddy keeps its sessions in `~/.workbuddy`, so the
card takes the project name from the workspace name WorkBuddy stores
(`~/.workbuddy/workspace-display-names.json`) instead of the folder name.

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
too warm) and has a Test button for each rule.

**Battery care** (Settings › General) keeps the phone's battery between two levels, 50–80 % by default, so a panel
that is always plugged in lasts longer. Phones cannot stop charging without root, so PhoneBox switches the power
where it comes from: a smart plug in Home Assistant, or two shortcuts (for example `uhubctl` for a USB hub with
switchable ports). The panel keeps running over Wi-Fi while the USB power is off.

## Security & privacy

- The agent listens on `127.0.0.1` only. Requests with a foreign `Host` header (DNS rebinding) or a cross-site
  `Origin` (CSRF) are rejected; only the settings page itself is accepted from browsers.
- The optional Wi-Fi port (8766) only serves the panel API, only over TLS pinned to the paired certificate, and only
  with the paired key. Settings, shortcut tests and pairing stay local. **New key** in the settings unpairs all phones.
- The phone only ever sends a shortcut **id**; commands are defined on the PC. Command widgets work the same way:
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
  default), PhoneBox also queries `codex app-server` about every 3 minutes (backing off on failures) while an enabled
  page shows Codex quotas and fresh CodexBar data is not already winning; no coding session or inference prompt is
  required, and local session quota stays as fallback. Claude Code sessions come from
  `~/.claude/sessions`. OpenCode sessions (opt-in, off by default) come from its background service's local HTTP API —
  the native one and each running WSL distro's — with the password OpenCode wrote to `~/.local/state/opencode/service.json`;
  only the session's folder, title, model, token counters, times and outcome are read, never prompts, replies or tool
  arguments. Cursor sessions (opt-in in **Settings › General › Cursor sessions**) come from recent agent transcripts
  under `~/.cursor/projects/*/agent-transcripts`. Only the last message's role is kept; chat text is never shown,
  stored or sent. Project names use the folder basename from Cursor's `workspaceStorage`.
- **Native Antigravity quota:** the agent reads the running Antigravity server's port and CSRF token from its
  process arguments and queries its loopback quota/status API, or invokes the signed-in CLI's structured usage
  command. Only measured quota fields reach the panel; CLI conversation identifiers and token counters are omitted.
  PhoneBox does not manage Google OAuth credentials; the CLI uses its own authentication. CLI auto-updates are
  disabled for these probes.
- **No telemetry**: no accounts, no analytics, no crash reports. The [privacy policy](../PRIVACY.md) lists every
  connection the agent and the app make (update check, weather, the widgets that use public APIs) and how to turn
  each one off.
