# Roadmap

What is planned or waiting, in one place. Issues are closed once they have an answer, so this file is where the
open work lives; each item links the issue or pull request it came from. Closed issues stay readable, and the
people who proposed or prototyped something are credited in the [changelog](../CHANGELOG.md) when it ships.

## Being built in-house

- **Kiosk page for older devices:** a panel that runs in an older browser or tablet, with a paired key that can only
  do what the panel needs ([#41](https://github.com/ersinkoc/phonebox/issues/41); reference
  [#43](https://github.com/ersinkoc/phonebox/pull/43); old iPads in [#222](https://github.com/ersinkoc/phonebox/issues/222)).
- **Plugin system:** community widgets ([#40](https://github.com/ersinkoc/phonebox/issues/40); reference
  [#46](https://github.com/ersinkoc/phonebox/pull/46)).

## Contributions in progress

- **Several Claude Code accounts** ([#538](https://github.com/ersinkoc/phonebox/issues/538)): an opt-in list of extra
  Claude Code folders (`claude.extraCredentialDirs`, `{ label, dir }`), each with its own quota card as
  `claude@<label>`, read-only tokens and no fallback to another account. In three pull requests: the setting and the
  quota cards, then sessions from those folders, then the permission-prompt hook for them.
- **Discord login through the desktop app** ([#338](https://github.com/ersinkoc/phonebox/issues/338)): Discord refuses
  `AUTHORIZE` over IPC both without `redirect_uri` (0.9.24) and with it (0.9.25); the working combination is being
  tested against a real Discord client.
- **Feeding PhoneBox from MQTT**: a bridge script and a guide example for pushed values and notifications
  ([#241](https://github.com/ersinkoc/phonebox/issues/241)).
- **Rules for pushed values**: a "not updated for N seconds" condition, `>=` / `<=`, and a separate release threshold
  ([#242](https://github.com/ersinkoc/phonebox/issues/242), pull request
  [#245](https://github.com/ersinkoc/phonebox/pull/245), in 0.9.21, did the first two).
- **Voice commands**: say what you want and a language model of your choice (local through Ollama, or a hosted one)
  picks from the shortcuts you allowed; opt-in, off by default ([#257](https://github.com/ersinkoc/phonebox/issues/257)).

- **Steam library widget** ([#344](https://github.com/ersinkoc/phonebox/issues/344)): installed games, most recently
  played first, covers and update progress, read from Steam's own files with no API key or sign-in. Starting a game
  from the phone is a separate per-widget option, off by default and asking first, and the agent starts only an
  installed game's app id through Steam's URL handler.

- **One "AI providers I use" setting** ([#351](https://github.com/ersinkoc/phonebox/issues/351)): a list in Settings ›
  General, all on by default, that the Clock's status line, Quotas and the Quota ring's picker follow unless a widget
  sets its own providers.

- **Phone flashlight from the night screen** ([#369](https://github.com/ersinkoc/phonebox/issues/369)): a torch icon,
  off by default, that turns on the phone's flash through Android's `CameraManager.setTorchMode` (no camera
  permission) or fills the screen with white at full brightness; it turns off with a tap, after a set time, or when
  the app leaves the screen or the screen goes off.
- **Named quota accounts through ai-usagebar** ([#371](https://github.com/ersinkoc/phonebox/issues/371)), after #351:
  an opt-in quota source, off by default, that runs `ai-usagebar usage --json` as a fixed command without a shell,
  with a timeout and an output cap, and shows accounts such as `claude@personal` and `claude@work` as separate cards.
  When it fails, the card says so; it never falls back to the built-in readers.
- **Plex playlists** ([#379](https://github.com/ersinkoc/phonebox/issues/379)), the second of the two pull requests:
  a playlist action, a playlist picker and a Plex playlists widget, on top of the Plex queue widget (#409). The token
  goes only to the Plex server, and the phone sends ids only.
- **A white theme and an e-ink option** ([#383](https://github.com/ersinkoc/phonebox/issues/383)), in two pull
  requests: a pure white theme, then an "E-ink screen" option, off by default, with no pulsing or animations and a
  slower refresh for fast widgets.
- **LCD image retention protection** ([#642](https://github.com/ersinkoc/phonebox/issues/642)): an option next to OLED
  burn-in protection, off by default, that mirrors the grid every 10 minutes (by the widgets' grid positions, so the
  inside of each widget is untouched) and shifts the page up to ±4 px every 2 minutes. No per-card flip.
- **The whole title on the media widget** ([#643](https://github.com/ersinkoc/phonebox/issues/643)): the title's font
  shrinks to fit (never below 11 px, then an ellipsis) instead of cutting at two lines, also on the Control Deck
  media remote. The default, with no option.
- **Three small additions** ([#644](https://github.com/ersinkoc/phonebox/issues/644)), each its own pull request: the
  phone's battery time left or time to full; an estimated GPU power (marked "≈", on the tile only, never seen by
  rules or history as a reading) when the card reports none; and an Automatic / Dark / Light switch on the desktop
  settings page.
- **The phone as a computer camera** ([#661](https://github.com/ersinkoc/phonebox/issues/661)), Windows, off by
  default: the phone's camera over USB through the scrcpy-server the phone microphone already uses (#472), decoded
  with Windows' own H.264 decoder and delivered as an OBS source or a Windows 11 virtual camera (not by writing into
  OBS's virtual camera); no new downloads, and start, stop and state visible on the phone. A small prototype and a
  design note first.
- **The playing app's volume in Now playing** ([#673](https://github.com/ersinkoc/phonebox/issues/673)), Windows, the
  first of two pull requests: a widget option that makes the slider control the Core Audio session of the app behind
  the media session (a browser's own session; a WebView2 app through its parent process), with the master volume as a
  second slider below it. The source icon may show a known site taken from the browser's window title; the title
  itself stays on the computer. No mixer column inside Now playing: the Application mixer widget already does that.
- **A preview of the computer's own cameras** ([#673](https://github.com/ersinkoc/phonebox/issues/673)), Windows, the
  second pull request: webcams through `MediaCapture` in shared read-only mode (never taking the camera from another
  app), and DirectShow-only virtual cameras through ffmpeg's `dshow` input, as about 5 JPEG frames a second at 640 px
  in the Camera widget. Off by default behind a switch in Settings on the computer (the phone cannot turn it on),
  only while the widget is on screen, and PhoneBox lets go of the camera a few seconds after the last request (its
  light stays on only if another app is using it). Frames are not stored or logged.

## Open for a contributor

Agreed in principle, not being built in-house. Say so on the issue before starting.

- **Typing from the phone's keyboard**: a text field and a row of keys (Enter, Backspace, Esc, Tab, arrows, optionally
  Ctrl / Alt / Shift) on the Dictation widget, behind its **Type the text on the computer** switch and through
  `/api/dictate`; nothing logged or stored ([#320](https://github.com/ersinkoc/phonebox/issues/320)).
- **Android screen saver (DreamService)** ([#287](https://github.com/ersinkoc/phonebox/issues/287)): show the panel as
  the phone's screen saver while it charges or docks, with the lock screen left in place. With Keep awake on, the
  agent turns the phone's screen saver off over adb, so that has to follow the new setting.
- **Headless Linux server and container image** ([#281](https://github.com/ersinkoc/phonebox/issues/281)): a documented
  way to run the Linux agent without a desktop session, an address to advertise for Wi-Fi pairing from behind
  container networking (also for a LAN outside the private address ranges, which the agent does not offer by itself:
  [#614](https://github.com/ersinkoc/phonebox/issues/614)), and persistent data. Remote access to the settings page needs its own sign-in first; until
  then an SSH tunnel to the loopback port is the safe way.
- **Pushed values without refetching the whole list** ([#329](https://github.com/ersinkoc/phonebox/issues/329)): the
  change event carries the key and its new value, and the phone fetches all of `/api/push` again only when it missed a
  version. A value pushed every second, such as a game's FPS, then costs one small event instead of the full list.

- **Synced lyrics** ([#339](https://github.com/ersinkoc/phonebox/issues/339)): a widget next to Now playing that shows
  the timed lyric of the current track and follows the playback position. The agent looks the track up on LRCLIB
  (title, artist, album, duration) only when the user turns the lookup on and only while such a widget is on an enabled
  page; results stay in memory, and the phone never contacts LRCLIB.
- **Several Codex profiles** ([#633](https://github.com/ersinkoc/phonebox/issues/633)): like the Claude Code
  accounts in #538, an opt-in list of extra Codex homes (`{ label, dir }`, each a `CODEX_HOME`), each with its own
  quota card as `codex@<label>`, sessions marked with their profile, and the live quota lookup run with that profile's
  `CODEX_HOME`. Paths and tokens stay on the computer, with no fallback to another profile.
- **Audio devices and app volume on Linux** ([#613](https://github.com/ersinkoc/phonebox/issues/613)): the device
  list, the default microphone, switching the default device and per-app volume through `pactl` (sinks, sources and
  sink inputs; PipeWire answers it too), with a forced C locale for parsing. Today these are Windows-only and the
  Linux agent controls only the system output.
- **Synced lyrics from files on the computer** ([#669](https://github.com/ersinkoc/phonebox/issues/669)): `.lrc`
  files from a folder chosen in Settings on the computer, matched by artist and title, as a source before or instead
  of LRCLIB in the Synced lyrics widget. Off until a folder is set; the agent reads only `.lrc` files inside that
  folder (no path patterns from the phone), and the folder's path never goes to the phone.
- **A larger device switcher** ([#340](https://github.com/ersinkoc/phonebox/issues/340)): bigger touch targets for the
  computers in the all-pages overview, each with an icon (emoji or uploaded picture) and a colour set per computer.

## Waiting on someone else

- **Signed Windows agent** ([#56](https://github.com/ersinkoc/phonebox/issues/56),
  [#69](https://github.com/ersinkoc/phonebox/issues/69)): SignPath Foundation approved the project and the release
  pipeline already signs `PhoneBoxAgent.exe`. The release certificate, which SignPath Foundation issues in its own
  name, is still pending on their side. The first release after it is issued ships a signed agent.
- **Approving Antigravity tool permissions from the phone**
  ([#215](https://github.com/ersinkoc/phonebox/issues/215)): needs a documented Antigravity hook that waits for an
  answer, like the one Claude Code has. A permission gate built on undocumented internals could approve the wrong
  thing after an Antigravity update.
- **Power standby on Android 8+** ([#30](https://github.com/ersinkoc/phonebox/issues/30)): continued from
  [#32](https://github.com/ersinkoc/phonebox/pull/32) in the draft [#430](https://github.com/ersinkoc/phonebox/pull/430).

## Waiting on a report

- **Razer BlackShark V2 Pro missing from Device batteries after 0.9.24**
  ([#326](https://github.com/ersinkoc/phonebox/issues/326)): dongle or Bluetooth, Synapse, and what Windows reports are
  needed to tell whether the new filter for disconnected Bluetooth devices hides it.
- **Intel GPU load on Linux** ([#224](https://github.com/ersinkoc/phonebox/issues/224)): in 0.9.20, to be confirmed
  on real hardware.
- **Network widget several times too high** ([#169](https://github.com/ersinkoc/phonebox/issues/169): about 4x on a
  Realtek USB 2.5GbE adapter; [#636](https://github.com/ersinkoc/phonebox/issues/636): about 6x on Windows Ethernet):
  the rate is bytes per second and shown as MB/s correctly, so the likely cause is the same traffic counted on more
  than one adapter. Which adapters Settings › General › Ping counts is needed to confirm it.
- **Claude Code prompts not reaching the phone** ([#497](https://github.com/ersinkoc/phonebox/issues/497)): the hook
  gets an empty answer in a few milliseconds while the panel looks connected. Since 0.9.30 `agent.log` says why a
  prompt was left to the computer; that line, the `live stream opened (…)` lines, and whether two `PhoneBoxAgent.exe`
  run (a sandboxed copy has its own instance lock) are needed.
- **Home Assistant tiles not vibrating on one phone** ([#464](https://github.com/ersinkoc/phonebox/issues/464)): the
  panel calls the same 14 ms haptic for every widget, and `dumpsys vibrator` showed no vibration from the app at all,
  so whether the phone drops short vibrations needs `adb shell dumpsys vibrator` right after a shortcut tap and an HA
  tap, and MIUI's touch-vibration setting.
- **Spotify queue: a tapped track still replaces what is playing in 0.9.31**
  ([#668](https://github.com/ersinkoc/phonebox/issues/668)): since 0.9.30 a tapped track starts inside the playing
  playlist or album, but the Web API takes a starting track only there, so Liked Songs, an artist, a radio or mix and
  hand-queued tracks still play it on its own. Where the music was playing from, and `agent.log` right after a tap,
  are needed.

## How to help

Comment on the linked issue (a closed issue can be reopened) or open a draft pull request. For a larger feature,
please say so before building it, so two people don't build the same thing. See [CONTRIBUTING.md](../CONTRIBUTING.md).
