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

- **Pomodoro focus history** ([#350](https://github.com/ersinkoc/phonebox/issues/350)): an option on the Pomodoro
  widget, off by default, with a small line chart of finished focus minutes per day for the last seven days, kept on
  the phone with the widget's own state.
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
- **Mics off, camera off** ([#387](https://github.com/ersinkoc/phonebox/issues/387)): an action next to panic that
  mutes only the microphones and stops the OBS virtual camera. It doesn't touch the physical webcam and says so
  instead of reporting success.

## Open for a contributor

Agreed in principle, not being built in-house. Say so on the issue before starting.

- **Read-only Discord chat on the phone** ([#356](https://github.com/ersinkoc/phonebox/issues/356)): the text chat of
  the voice channel you're in or a picked channel, with new-message dots, behind its own switch (off by default,
  `messages.read`, message text in memory only). The code is written and reviewed in
  [#368](https://github.com/ersinkoc/phonebox/pull/368) (by AlberBM), but was never tried against the real Discord
  app: it needs someone who can test it there, then a rebase onto the screen-share scope code from #416.
- **Typing from the phone's keyboard**: a text field and a row of keys (Enter, Backspace, Esc, Tab, arrows, optionally
  Ctrl / Alt / Shift) on the Dictation widget, behind its **Type the text on the computer** switch and through
  `/api/dictate`; nothing logged or stored ([#320](https://github.com/ersinkoc/phonebox/issues/320)).
- **Photo frame pictures as the page background** ([#288](https://github.com/ersinkoc/phonebox/issues/288)): a
  per-page background from the photo frame's folder, dimmed under the widgets, using the same `/api/photos` feed.
- **Android screen saver (DreamService)** ([#287](https://github.com/ersinkoc/phonebox/issues/287)): show the panel as
  the phone's screen saver while it charges or docks, with the lock screen left in place. With Keep awake on, the
  agent turns the phone's screen saver off over adb, so that has to follow the new setting.
- **Headless Linux server and container image** ([#281](https://github.com/ersinkoc/phonebox/issues/281)): a documented
  way to run the Linux agent without a desktop session, an address to advertise for Wi-Fi pairing from behind
  container networking, and persistent data. Remote access to the settings page needs its own sign-in first; until
  then an SSH tunnel to the loopback port is the safe way.
- **Night screen as a rule action** ([#330](https://github.com/ersinkoc/phonebox/issues/330)): while a rule holds
  (for example *PC away for 5 minutes*), the phone shows the night screen at its own brightness, and goes back when the
  rule ends. The schedule stays as it is, and a tap still wakes the screen for a while.
- **Pushed values without refetching the whole list** ([#329](https://github.com/ersinkoc/phonebox/issues/329)): the
  change event carries the key and its new value, and the phone fetches all of `/api/push` again only when it missed a
  version. A value pushed every second, such as a game's FPS, then costs one small event instead of the full list.

- **Synced lyrics** ([#339](https://github.com/ersinkoc/phonebox/issues/339)): a widget next to Now playing that shows
  the timed lyric of the current track and follows the playback position. The agent looks the track up on LRCLIB
  (title, artist, album, duration) only when the user turns the lookup on and only while such a widget is on an enabled
  page; results stay in memory, and the phone never contacts LRCLIB.
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
- **Power standby on Android 8+** ([#30](https://github.com/ersinkoc/phonebox/issues/30)): pull request
  [#32](https://github.com/ersinkoc/phonebox/pull/32) was closed; a new pull request built on its review is welcome.

## Waiting on a report

- **Razer BlackShark V2 Pro missing from Device batteries after 0.9.24**
  ([#326](https://github.com/ersinkoc/phonebox/issues/326)): dongle or Bluetooth, Synapse, and what Windows reports are
  needed to tell whether the new filter for disconnected Bluetooth devices hides it.
- **Intel GPU load on Linux** ([#224](https://github.com/ersinkoc/phonebox/issues/224)): in 0.9.20, to be confirmed
  on real hardware.
- **Network widget about 4x too high on a Realtek USB 2.5GbE adapter**
  ([#169](https://github.com/ersinkoc/phonebox/issues/169)): the adapter's counters are right; the agent's own numbers
  are needed to find where the factor comes from.

## How to help

Comment on the linked issue (a closed issue can be reopened) or open a draft pull request. For a larger feature,
please say so before building it, so two people don't build the same thing. See [CONTRIBUTING.md](../CONTRIBUTING.md).
