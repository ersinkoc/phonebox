# Privacy policy

PhoneBox has no accounts, no servers of its own, no telemetry, no analytics and no crash reporting. The project never
receives any data from you. This page lists every place the agent (on your computer) and the Android app (on your
phone) connect to, so you can see exactly what leaves your machine and turn it off.

Last updated: 2026-11-26 (PhoneBox 0.9.30).

## What stays on your devices

- **Settings and layouts**: `config.json` in `%APPDATA%\PhoneBox` (Windows), `~/.config/PhoneBox` (Linux) or
  `~/Library/Application Support/PhoneBox` (macOS), plus `data.json` (pushed values and notifications) next to it.
  On macOS and Linux these files are readable only by your user (0600).
- **Discord login**: if you use the Discord widget, `discord-token.json` next to `config.json` holds the access and
  refresh token Discord gave PhoneBox (on macOS and Linux readable only by your user).
- **MQTT broker login**: the address, user name and password you enter in **Settings › General › MQTT** are stored in
  `config.json` on this computer. The password is write-only: the settings page never gets it back, and it is never sent
  to the phone, written to the log or shown in the connection's status.
- **Steam key**: the SteamID64 and Web API key are stored in `config.json` on this computer. A custom profile name is
  sent to Steam once when you save it, then only the SteamID64 is stored. The key is never sent to the phone or logged.
  The activity widget derives its short history from successive polls in memory; it is not written to disk or logged and
  starts empty after an agent restart.
- **Steam library**: the Steam library widget reads Steam's own files on this computer (the installed games' manifests,
  `localconfig.vdf` for when each was last played, covers from Steam's library cache, and the game running now) and sends
  the list and the covers only to your phone. It makes no network connection; nothing is sent to Steam or anyone else.
- **Last Claude quota reading**: `claude-quota.json` next to the log (usage percentages and reset times, no token),
  so the panel can show the last values after a restart, and `claude-quota-schedule.json` (when the next check may
  run, the last error message and, after Anthropic refused a token, a 16-character hash of that token, never the token
  itself), so a restart doesn't ask Anthropic again sooner than it should.
- **Log**: `agent.log` in `%LOCALAPPDATA%\PhoneBox` (Windows), `~/.local/share/PhoneBox` (Linux) or
  `~/Library/Application Support/PhoneBox` (macOS).
- **Phone app**: caches (weather, rates, widget state) in its own app storage. The app asks for `INTERNET`,
  `VIBRATE` and `ACCESS_NETWORK_STATE` (only to tell which network is Wi-Fi when the Wake button sends its packet; Android
  grants it without a prompt), and for the microphone (`RECORD_AUDIO`) only when a Dictation widget's button is tapped for
  the first time. For the phone bridge's clipboard sharing (off until you turn it on in the app's Phone bridge screen)
  it also holds `FOREGROUND_SERVICE` and `FOREGROUND_SERVICE_SPECIAL_USE` (a service with a visible notification keeps
  the connection to your computer), `POST_NOTIFICATIONS` (so that notification can show, asked when you turn the
  switch on) and `RECEIVE_BOOT_COMPLETED` (to start that service again after a restart; the receiver is switched off,
  and the app does not start at boot, while the bridge is off). Notification forwarding uses Android's **Notification
  access**, which you grant in Settings. Android may also ask for **Contacts** so PhoneBox can resolve a caller's
  number as described below. PhoneBox requests no SMS, call-log or phone-state permission for forwarding and lists
  launchable apps so you can choose which ones to send.
- **Computer data shown on the panel** (CPU, GPU, memory, disks, processes, media, volume, AI coding sessions and
  quotas, Docker / Podman, OBS) is read locally and goes only to your phone: over the USB cable (`adb reverse` to
  `127.0.0.1`, with a key only the PhoneBox app has) or, after pairing, over Wi-Fi / Tailscale with a pinned certificate and a key. On Linux, an Intel GPU's
  load comes from the GPU usage counters in other processes' `/proc/<pid>/fdinfo` (the counters only, nothing those
  programs draw or compute); processes of other users are read only when Linux allows the agent to read them (normally
  as root or with `CAP_SYS_PTRACE`).
- **Claude Code conversation** (off by default): with Settings › Claude Code › "Show the conversation on the phone" on,
  tapping a Claude Code session in the sessions widget asks the agent for its last 30 messages (your prompts and
  Claude's replies, each cut at 4000 characters) and its last 12 steps. The agent reads them from that chat's file in
  `~/.claude/projects` and sends them only to your phone, only for a session that is running, and only while the
  details are open. A step is the tool name plus a file name, the program a command starts with (never the command
  line) or a web host; tool output, thinking and file contents are never sent. Nothing is stored on the phone or the
  computer. With the switch off the agent refuses the request and no prompt or reply text is sent.
- **Grok sessions** (on, the same as Codex): the agent reads recent metadata under `~/.grok/sessions`
  (or `GROK_HOME`) and `active_sessions.json`. The phone gets the project folder, status, model, reasoning
  effort, token counters and times. Prompts, replies, generated titles, turn summaries and costs are not sent.
  A session shows as working while its turn is still open and a metadata file was written in the last three
  minutes, and as waiting while that turn is sitting on a permission prompt and the Grok process is still
  running. Sessions with nothing written in the last three hours are skipped. This stays on this computer
  except for the same local USB / paired Wi-Fi link as the rest of the panel.
- **Cursor sessions** (off by default; **Settings › General › Cursor sessions**): while enabled, the agent scans recent
  `*.jsonl` files under `~/.cursor/projects/*/agent-transcripts` (about every 30 seconds for discovery) and reads a
  bounded tail of each file to parse the last record's `role` in memory. Only that role and the file times are kept for
  the panel; chat text is never shown, stored or sent. Project labels use the folder basename from Cursor's
  `workspaceStorage/*/workspace.json` `folder` URI (cached about a minute), sent to the phone as the session's project
  name over the same local USB / paired Wi-Fi link as other panel data. Turning the setting off stops the scan.
- **Antigravity sessions** (off by default; **Settings › General › Antigravity sessions**): while enabled, the agent
  scans recent `transcript.jsonl` files under `~/.gemini/antigravity/brain` and `~/.gemini/antigravity-cli/brain`
  (about every 30 seconds for discovery) together with local `annotations/*.pbtxt` metadata and the Antigravity CLI's
  `history.jsonl` workspace basename. Only step types, statuses, timestamps, model names, tool names and token counters
  are kept; prompts, replies, thinking and tool arguments are never shown, stored or sent. Titles reach the phone only
  while **Show Antigravity chat titles on the phone** is also on; otherwise the widget shows the project name or
  `Antigravity`. Turning the setting off stops the scan.
- **Pi sessions** (off by default; **Settings › General › Pi sessions**): while enabled, the agent scans recent
  `*.jsonl` files under `~/.pi/agent/sessions` (or `$PI_CODING_AGENT_DIR/sessions`; a leading `~` there is expanded as
  Pi expands it; about every 30 seconds for discovery) and follows appended records.
  Only the working directory, session id, model and provider names, thinking level, token counters, stop reasons,
  tool names and timestamps are kept for the panel; prompts, replies, thinking and tool arguments are never shown,
  stored or sent. The model context window, when Pi has cached that model in its own model store (the
  `models-store.json` next to the sessions folder; built-in models may not be there), is read from that file. Titles
  reach the phone only while **Show Pi session titles on the phone** is
  also on, and with it off no title is read at all. Turning the setting off stops the scan and forgets what was read.
- **OpenCode sessions (off by default):** with Settings › General › "Show OpenCode sessions" on, the agent asks
  OpenCode's own background service — the native one, and the one inside each running WSL distro — for its recent
  sessions: the same local HTTP API the desktop app uses, authenticated with the password OpenCode wrote to
  `~/.local/state/opencode/service.json`; that password never leaves the computer. Only session metadata is requested
  (the folder, title, model, token counters, start and update times and the outcome); prompts, replies and tool
  arguments are never fetched, and sessions older than three hours are ignored. A stopped WSL distro is never started
  for this. Titles reach the phone only while Settings › General › "Show OpenCode session titles" is also on;
  otherwise the widget shows the project folder. The data goes only to your phone over the same local USB / paired
  Wi-Fi link as other panel data.
- **OpenCode permission prompts and questions (off by default):** when OpenCode's background service asks for
  permission, the agent reads the pending request from its local API (`127.0.0.1`, with the password OpenCode wrote
  to `~/.local/state/opencode/service.json` in a Basic header, over loopback only — it is never sent anywhere else).
  What goes to the phone is the card's content only: the project, the action (run a command, edit a file…), the
  command or paths it names, any message OpenCode attached, and the pattern an Allow always would save for the
  project. The phone's answer (allow once, allow always, deny) is posted back to the same local API. The same card
  carries OpenCode's agent questions: the question, its choices and whether a typed answer is allowed go to the
  phone, and the pick or typed answer comes back — nothing else about the conversation is read. Nothing is offered or
  sent until Settings › General is turned on (or `"phoneApprovals": true` in the `opencode` section of
  `config.json`); turning it off again stops this entirely.
- **Claude Code chat titles** (off by default): with Settings › Claude Code › "Show Claude Code chat titles on the
  phone" on, the sessions widget shows each chat's title, the name you gave it or the one Claude Code wrote, which it
  often draws from your first prompt. The agent reads only the title records at the end of the chat's file in
  `~/.claude/projects`, never the prompts, replies or tool arguments, and sends the title only to your phone, like the
  rest of this list. With the switch off the file is not opened and no title is sent.
- **Clipboard sharing, the phone bridge** (macOS only, off by default; **Settings › Phone bridge**): while it is on,
  the agent looks at the clipboard's change counter about every 400 ms and, when you copy plain text (up to 64 KiB),
  sends it over the same USB / paired Wi-Fi link as other panel data. This assumes that users and software with access
  to the local agent API are trusted. Text a phone sends is put on this computer's clipboard. The agent skips copies
  when it detects concealed, transient or auto-generated markers. Password managers can omit or add markers after the
  clipboard has been read. Turn off sharing before copying sensitive text. The text is held in memory
  only: the last 20 copies are kept so a phone that reconnects within two minutes gets what it missed, and they are
  forgotten when the setting is turned off. It is never written to disk, `data.json` or the log (the log records no
  clipboard text), and it goes nowhere except your own phones. Turning the setting off stops both directions at once.
  If macOS denies PhoneBox the clipboard (its paste permission in Privacy & Security), nothing is read or sent, and the
  settings card says so.
  On the phone the app puts the text you copied on the computer onto the phone's clipboard, and
  reads the phone's clipboard only when you ask it to (Send to Mac in the text-selection menu, the Share sheet, a
  Quick Settings tile or a button on the service's notification); it never sends a clip Android marks as sensitive.
  Text that another app hands to Send to Mac (the selection menu, the Share sheet) is shown first and sent only when
  you tap **Send to Mac**. To pick up again after a dropped connection, the phone keeps the number of the last
  clipboard event in memory (not the text) and sends it when it reconnects. **On Android 9 and older, every app on the
  phone can read the clipboard at any time**, not only the app in front: there, text you copy on the Mac is readable
  by all apps installed on the phone. Android 10 and later let only the app in front (and the keyboard) read it.
- **The phone's own music in Now playing** (Android, off by default; **Phone bridge › Show the phone's music in Now
  playing**): while it is on, the app reads the title, artist, album, position and cover picture of the song another app
  is playing on the phone (Android's media sessions, which needs Notification access to PhoneBox) and shows it in the
  panel's Now playing widget when the computer plays nothing; its buttons then control that app on the phone. The song
  stays on the phone: it is not sent to the computer, stored, or logged, and nothing is read from notifications for it.
- **Phone notifications on the computer, the phone bridge** (macOS only, off by default; **Settings › Phone bridge ›
  Show phone notifications on this Mac**): while it is on, the agent shows the call, message and app notifications your
  phone sends it, as ordinary macOS notifications: the app's name, the title (who is calling or who wrote) and the text. Only the apps you allow on the phone are sent, and only to this computer over the same USB / paired Wi-Fi
  link as other panel data. macOS shows them with the PhoneBox icon and the phone app's icon as the picture
  when the agent can build its small notifier program (PhoneBox.app, in the agent's folder: built on this Mac from source
  inside the agent with Apple's Swift compiler when Xcode or the command line tools are installed, nothing is downloaded,
  and macOS asks once whether PhoneBox may show notifications). The agent keeps the app icons in memory only and forgets
  them within half a minute of the setting being turned off; to attach one, the notifier writes it to a temporary file, which macOS moves into
  its own notification store and keeps with the notification (a file macOS does not take is deleted). Otherwise they appear
  under Script Editor's name, which is how a program without its own app shows a notification. macOS keeps them in
  Notification Center like any other. The agent does not store them or
  write their text to its log (the log never holds their words). With **Copy one-time codes from
  messages** also on, a login code the agent finds in a message is put on this computer's clipboard, and is not sent on
  to your phones. Turning the setting off stops all of it at once, also
  for notifications that were waiting to be shown.
- **Phone notifications, on the phone side of the phone bridge** (off by default; the app's **Phone bridge** screen › Show
  notifications on the Mac, plus Android's Notification access): the app reads the notifications of the apps you switched
  on (their name, title, text and kind; for a call, who is calling) and sends them only to your paired computer over the
  same USB / paired Wi-Fi link, together with the posting app's icon (a small PNG, so the computer can show it as the
  notification's picture). The **All apps** switch on the Phone bridge screen (off until you turn it on) sends every app's
  notifications instead of the list, which includes banking, security and other private ones. Opening the Phone bridge
  screen while forwarding is on can prompt once for **Contacts**. With permission, PhoneBox can look up the number shown
  in a Samsung in-call UI notification and use a matching contact name as its title. Selecting the default dialer also
  allows Samsung's in-call UI call notifications because that app posts calls for the dialer. Its incoming and outgoing
  calls use the same format, so either can be forwarded. If PhoneBox learns the name later for a Samsung call, it sends
  an update with the title, text, app metadata and icon when available. PhoneBox always excludes its own notifications
  and ongoing media. The app holds up to 20 unsent notifications in memory. At the next delivery attempt, it discards
  entries at least two minutes old. Notification contents stay out of the phone's log and storage. If you enable
  `PhoneBoxBridge` debug logging with adb, the phone can log app package names and call flags, including for skipped calls.
  Until you turn the switch on, the app does not even appear in Android's Notification access list.
- **Sound for the Spectrum bars widget** (Windows): while an enabled page with the widget is on the phone's screen,
  the agent captures what the default output device plays, analyzes it in memory and sends only 48 loudness levels
  per frame to the phone. Audio is never stored or sent; capture stops a few seconds after the widget leaves the
  screen, and removing the widget or turning its page off turns it off.
- **Mouse control for the Trackpad widget**: touches and scroll gestures are sent directly from the phone to the agent
  on your computer (over USB or pinned-TLS Wi-Fi) to control the cursor and scroll. No mouse or gesture data is ever
  recorded, stored or sent outside your local connection. It is active only while an enabled page contains the Trackpad
  widget with **Allow controlling the mouse from the phone** enabled (off by default).
  On a Mac, a three-, four- or five-finger gesture from the phone opens Mission Control or Apps, signals the Dock, or
  presses a key (moving between desktops or pages). While you gesture, the agent reads the Mac's Trackpad, `com.apple.dock`
  and `com.apple.symbolichotkeys` preferences (at most once every 2 seconds) to do what your own trackpad would do. Only
  the gesture settings are kept, in memory, and nothing is stored or sent.
- **Local services**: LibreHardwareMonitor's web server (`127.0.0.1`, port 8085 by default), a running Antigravity language server
  (loopback, over HTTP or HTTPS), the Docker / Podman socket, OBS WebSocket and the Discord desktop app's local connection (a named pipe
  or socket on this computer) are local connections.

[`scripts/uninstall.ps1 -Purge`](scripts/uninstall.ps1) and [`scripts/uninstall.sh --purge`](scripts/uninstall.sh)
remove the agent together with its settings, pushed data and log (without the switch, settings are kept). Uninstalling
the Android app removes everything on the phone.

The Water intake widget stores the current local day's total and the next reminder in panel storage on that device.
It sends no intake data to the desktop agent or an external service.

WorkBuddy sessions and phone approvals are off by default. When enabled, WorkBuddy session cards use local status files and
workspace names. When an enabled AI sessions widget allows WorkBuddy phone approvals, the hook sends pending
prompt details (session ID, workspace, working directory, tool, action details and any description) to the local
agent. The paired phone receives the prompt and replies over USB or pinned TLS over Wi-Fi. Turn off **Ask on
the phone** in WorkBuddy's settings section, disable the widget's approval option or remove the hook to stop it.

## Connections made by default

Each of these servers sees your IP address, like any web request. PhoneBox sends none of them an identifier for you
or your devices, with these exceptions: the update check can retry with your GitHub CLI token (see below), which
identifies your GitHub account to GitHub, and the Claude quota check sends the Claude Code sign-in token, which
identifies your account, to Anthropic only. The Grok quota check (below) does the same for Grok, but only while a page
shows it.

| What | Where | What is sent | How to turn it off |
|---|---|---|---|
| Update check | `api.github.com` (release info), `github.com` (downloads) | The repository name; with `gh` signed in and only when GitHub turns away an anonymous request, your GitHub CLI token, to `api.github.com` only | **Settings › General › Updates › Install new releases automatically** turns off installing. The check every 6 hours stays; it sends nothing about you |
| Phone mic setup (Windows) | `github.com` (the scrcpy 5.0.1 release, `Genymobile/scrcpy`), which redirects the download to `release-assets.githubusercontent.com` | The request itself; nothing about you. Only when you press **Settings › General › Phone mic › Set up** and no official scrcpy-server 5.0.1 is found on this computer; the file is kept only if its SHA-256 matches the official release. The phone's audio itself never leaves this computer and its phone: it goes over the phone's adb connection to the agent (USB; over your own network only if you connected adb over Wi-Fi yourself) and into a playback device. Muting only silences it on the computer: while the phone mic is on, the phone keeps recording; turn the mic off to stop that | Don't press Set up, or put the file next to the settings file yourself |
| Network latency (ping metric, "ping high" rule) | `1.1.1.1` (Cloudflare DNS) by default, an ICMP echo every 5 seconds | An empty ping packet | From 0.9.6: empty **Settings › General › Ping**, or enter another host. 0.9.5 and earlier: not switchable; block ICMP to `1.1.1.1` in your firewall |
| Weather (phone) | `api.open-meteo.com`, every 15 minutes | The coordinates of the city you chose (Tallinn by default), and the forecast model when one is picked in **Settings › General › Weather** | From 0.9.6: asked only while a page has a widget that shows the weather (weather, forecast, sun & moon, a clock with its weather line, or a Custom HTML widget, which gets the weather too); remove those, or turn off the clock's weather line. 0.9.5 and earlier: asked while the panel runs; choose a city you are comfortable sharing in **Settings › General › Weather** |
| Air quality widget (phone) | `air-quality-api.open-meteo.com`, while the widget is on a page | The same coordinates | Remove the widget |
| City search (settings page) | `geocoding-api.open-meteo.com` | The city name you type, only when you search | Don't search |
| Claude quota | `api.anthropic.com/api/oauth/usage`, every 3 minutes after a successful check (sooner after a failed one, as often as every 30 seconds while the network is down; less often when Anthropic asks it to wait; agents running on this computer share one schedule) while Claude Code is signed in on this computer, whether or not a page shows a quota widget | The OAuth token Claude Code stores in `~/.claude/.credentials.json`, or on macOS, when that file doesn't exist, the "Claude Code-credentials" item from the default keychain search list (normally the login Keychain), read with `security find-generic-password`; read-only, PhoneBox never refreshes it. Only the Claude Code command-line sign-in is used; the Claude Desktop app's own sign-in is never read. Each folder listed under Settings › General › Claude Code › other accounts (`claude.extraCredentialDirs`) is a token source too: the token in its `.credentials.json` is sent in a request of its own, on a schedule of its own (only that file is read, never the Keychain); the phone sees only the label you gave it | No switch yet for the default sign-in: it stops when Claude Code is signed out on this computer. An extra account keeps being checked whether or not the default one is signed in; removing its row in Settings stops the checks for that folder |

## Connections made only when you use a feature

| Feature | Where | What is sent |
|---|---|---|
| iPhone USB connection | The local usbmuxd service (`127.0.0.1:27015` on Windows, `/var/run/usbmuxd` or `/run/usbmuxd` on macOS and Linux), while the agent’s Wi-Fi & Tailscale listener is enabled. Turn it off in the agent settings and restart the agent to stop connections | Device discovery and a connection request for a USB device’s local relay. The response to the iPhone’s fresh challenge uses the existing pairing key. The service relays encrypted HTTPS between the agent and the paired iPhone |
| Grok quota | Only while an enabled page shows Grok quotas (a Quotas or CodexBar widget, or a Clock with its status line, whose providers include Grok, or a Quota ring set to Grok) and the Grok CLI is signed in on this computer. `cli-chat-proxy.grok.com/v1/billing?format=credits` every 3 minutes after a successful check (sooner after a failed one, as often as every 30 seconds while the network is down; later when Grok asks it to wait), and `cli-chat-proxy.grok.com/v1/settings` for the plan name at most every 6 hours (that request gives up after 2 seconds). Remove those widgets, or take Grok out of their providers, to stop the requests | The bearer token the Grok CLI stores in `~/.grok/auth.json` (`GROK_HOME` overrides that folder); read-only, PhoneBox never refreshes it, never logs it and never sends it to the phone |
| Codex live quota (opt-in) | Off by default until turned on in **Settings › General › AI quotas & CodexBar › Read live Codex quotas with the Codex CLI**, and only while Codex is selected in the provider list and fresh CodexBar data is unavailable. This also serves custom widgets that read the quota API. Runs the local `codex` CLI (`codex app-server`), which contacts its configured OpenAI/ChatGPT endpoints (by default `chatgpt.com` / `api.openai.com`) about every 3 minutes, backing off up to 30 minutes on failure. Turn off by unchecking **Read live Codex quotas with the Codex CLI** | PhoneBox sends only `initialize` metadata and `account/rateLimits/read` over local stdio (no prompt or thread) and never reads or logs credentials; the Codex CLI uses its own sign-in and may refresh and rewrite `~/.codex/auth.json` |
| Discord widget | `discord.com/api/oauth2/token`, once at login and when the token needs renewing; `cdn.discordapp.com`, once per server icon and avatar shown (the agent fetches them, the phone never contacts Discord; the rest goes through the Discord app on this PC, locally). Turn off in **Settings › General › Discord** | The application's client ID and secret, and the one-time login code or the saved refresh token; for images only the image's id and hash, which Discord already knows. For favourite servers and channels the names and avatars of the people in voice channels you are not in reach the phone too (read from the Discord app on this PC, kept in memory only). With screen share on (off by default) and a Discord widget's **Control** on, the names of the apps with a window on this PC (Windows only) with their process IDs, and the name of what you share, go to the phone while its share view is open, and the phone sends back the process ID of the app you pick (the name of what you share goes with that view only). The current-channel bar's own **Control** gets only whether you share, for its Live badge and Stop button, never the app list or the name; no game list is fetched. With the read-only chat on (off by default), these reach the phone, read from the Discord app on this PC: the text of the messages in the channel shown and the names and avatars of their authors, the names of your servers and their text channels, and, in a direct or group call, the messages of that conversation. All of it is kept in memory only, never in a file or the log |
| Steam friends and activity | `api.steampowered.com`, only while Steam is enabled and an enabled page contains either widget. Clear the API key, turn off Steam, or remove both widgets to stop requests | The API key in the request header; your SteamID64 for the friend-list request; your custom profile name as `vanityurl` only when you save it; and friend SteamIDs in player-summary batches. Steam returns names, avatars, presence and public game details. The activity list is built from successive polls and stays in memory only |
| Steam avatars | `*.steamstatic.com` and `steamcdn-a.akamaihd.net`, only when the agent fetches an avatar shown by either widget. Clear the API key, turn off Steam, or remove both widgets to stop requests | A request to the avatar URL Steam returned (including its image hash); the Web API key and SteamID64 are not sent to these image hosts |
| Synced lyrics widget | `lrclib.net` (LRCLIB, free and open, no account), off until you turn on a Synced lyrics widget's **Look up lyrics on LRCLIB** (per widget). Then asked by the agent when a song starts, only for a widget on an enabled page with the option on; the phone never contacts it. The exact lookup sends the title, artist, album and length; when it finds nothing (or the song is longer than an hour), a search sends the title and artist only. A song is asked again only once its answer has expired or was dropped from the 100. Answers are kept in memory only, never on disk: an answer is reused for 6 hours for a song that was found and 30 minutes for one that was not, at most 100 songs; an expired answer is dropped the next time any widget asks for lyrics, and all of them when the agent stops; after a failure nothing is asked for a while (1 minute, doubling up to 30, or as long as LRCLIB's Retry-After asks). Turn the option off or remove the widget to stop the requests | The title, artist, album and length of the track playing on this computer, and a `PhoneBox/<version> (+https://github.com/ersinkoc/phonebox)` user agent; nothing about you |
| Stocks widget (phone) | `qt.gtimg.cn` (Tencent), and `proxy.finance.qq.com` for intraday charts. The agent requests data over HTTPS for the saved watchlist when a Stocks widget on an enabled page asks for it. Remove the widget to stop the requests | The watchlist codes you chose (e.g. `sh000001`); nothing about you |
| Stock forum widget (phone) | `guba.eastmoney.com` and `gbapi.eastmoney.com` (East Money), only while an enabled page shows a Stock forum widget; the agent requests the configured code over HTTPS, caching successful answers for ten minutes and failures for five minutes. Remove the widget to stop the requests | The stock code of the forum (a 6-digit code you chose); nothing about you |
| GitHub widget | `api.github.com`, through your own GitHub CLI (`gh`) | Whatever `gh` sends with its own sign-in |
| Hacker News widget (phone) | `hacker-news.firebaseio.com` | Nothing but the request |
| Exchange rates widget (phone) | `api.frankfurter.dev` | The currencies you chose |
| Crypto widget (phone) | `api.coingecko.com` | The coins and currency you chose |
| Internet radio | `*.api.radio-browser.info`, `opml.radiotime.com`, the station's own stream server and logo host | Station searches, the station you play and its logo request |
| Spotify control | `accounts.spotify.com`, `api.spotify.com` | Your own Spotify app's client ID and the sign-in you approve; the token is saved in `config.json.spotify.json` next to your settings |
| Spotify queue covers (phone) | `i.scdn.co`, while a Spotify queue widget shows covers. Turn off with the widget's **Show covers** option | The request for each cover image |
| Plex control (Caldera Amp) | The Plex server address you enter in Settings (which may be on the internet) and the player address (this computer, `127.0.0.1:32500`, by default). The widget's requests are made only while an enabled page shows a Plex queue or Plex playlists widget; remove the widget or disable its page to stop them. **Check Plex connection** and **Load Plex playlists** in Settings send one request each time you press them, with or without a widget. Opening a Plex playlists widget's options in the layout editor loads the playlist list from the server (with the token) once, when no list is loaded yet for the saved server address, token and player address; its **Refresh** button loads it again. Clearing the server address or the token stops both | To the server: the token you enter, in an `X-Plex-Token` header, and the requests for its identity, the current play queue, the list of audio playlists, and a new play queue when you start a playlist. To the player: timeline polls (what it is playing), the skip command when you tap a queued track, and the play command with the server's address and the new queue when you start a playlist, without the token. The addresses and token stay in `config.json`. |
| Pairing over Wi-Fi without USB | `ersinkoc.github.io` (the page the QR code opens), then your computer | GitHub Pages serves the page and sees your IP address; the pairing details are after `#` in the link, which browsers never send, and the page removes them from the history. The phone then sends the one-time code only to your computer, over TLS pinned to its certificate |
| Calendars | The iCal addresses you add | The request to that address |
| Dictation widget | Only while you hold a dictation open (the microphone button is red, at most a minute): the phone's own speech recognizer hears you. On most phones that is Google's, which may send the audio to Google. With the widget's **Recognize on the phone only** option the app uses Android's on-device recognizer, which keeps the audio on the phone; that needs Android 12 or later and the offline language, and on older Android the option refuses to listen rather than fall back. The recognized text goes from the phone to the agent and is typed into the app in front if the widget's **Type the text on the computer** option is on (off by default). Neither the audio nor the text is stored or logged by PhoneBox. Android asks for the microphone the first time the button is tapped; you can take it back in Android's app settings | Google (or your phone's recognizer), only while dictating; nothing with on-device recognition |
| App console widget | Nothing. On Windows the agent reads the process name of the focused window to pick the matching app profile; that name stays on the computer, and the phone only gets the profile's name and key labels (a key without a label shows its keys, such as ctrl+z). With the widget's **Send keys to the computer** option on (off by default), a tap sends the profile's id and the key's slot, and the agent presses the keys only if the app in front still matches that profile | Nothing |
| News feeds widget | The RSS and Atom addresses you add in **Settings › General**, fetched by the agent at most every 15 minutes and only while a page shows the widget; the phone never contacts the feed sites. With the widget's **Open articles on the PC** option on (off by default), tapping a headline opens its article in the computer's browser. Turn the requests off by removing the feeds in **Settings › General › News feeds**, or the widget from your pages | The request to that address, with the feed's last `ETag` / `Last-Modified` so an unchanged feed is not sent again |
| Home Assistant | Your Home Assistant server | The access token you enter |
| MQTT broker (off by default) | The broker you enter in **Settings › General › MQTT**, which may be on this network or on the internet (a public broker sees this computer's address, the client id and everything the agent sends it). Once **Connect to an MQTT broker** is on, the agent connects and stays connected, trying again after a failure with a wait that grows to a minute, until you turn it off or clear the address; the agent subscribes to nothing and publishes nothing itself yet. **Test** connects once and disconnects. The phone never contacts the broker and never gets its address, user name, password or topics. The certificate is always checked: a broker with its own certificate is trusted only through the CA file or the fingerprint you enter, and there is no switch to skip the check. A last will is sent only if you enter a topic (retained only if you tick it) | The login you enter (user name and password, in MQTT's CONNECT packet; in the clear unless you tick **Use TLS**), the keep-alive pings, the client id (yours, or `phonebox-` and eight characters derived from this computer's name) and, if you set one, the last will's topic and message. The address, user name and password stay in `config.json` |
| Home Assistant dashboard widget | Your Home Assistant server (the address in Settings › General, or the widget's own): while the widget is on screen, the phone checks every minute (every 15 seconds while it does not answer) that the server answers, with a request for `/manifest.json`, and opens the dashboard, which then stays open on its page; remove the widget or turn its page off to stop both | The dashboard's own requests, as Home Assistant's pages make them. The phone signs in to Home Assistant itself, as a browser would, and keeps that session in the dashboard's own storage; the token from Settings › General never goes to the phone |
| Web page in a Custom HTML widget | The address you enter in the widget: the phone loads it, like a browser, while a page shows the widget, and only once an address is entered. Clear the address or remove the widget to stop it | Whatever that page asks for, as a browser would, over plain http if the address is http; PhoneBox adds nothing. The page cannot keep third-party cookies (a sign-in kept in the page's own storage, as Home Assistant does, works) |
| Wake-on-LAN | Your own network only: a UDP broadcast (port 9) to the machine's MAC address, sent only when you press a *wake a computer* key, or the phone's **Wake the PC** button while the paired computer is unreachable | The magic packet, which holds the MAC address you entered (or, for the button, the paired computer's own MAC addresses); nothing leaves your local network (a broadcast address you add must be one of this computer's own networks' or `255.255.255.255`). The paired computer's MAC addresses are kept on the phone next to its pairing details, and the button sends only on a network the computer is on (the phone's network must hold one of the computer's addresses) |
| Image widget | The image address you enter | The request to that address |
| Camera widget | The RTSP cameras you add in **Settings › General › Cameras**, pulled by ffmpeg on this computer only while an enabled page shows a Camera widget for them, and stopped a few seconds after it leaves the screen. The phone gets JPEG pictures (5 a second by default, 1 to 10 per camera) over USB or the paired Wi-Fi link and never contacts the camera. The addresses, with the user names and passwords in them, stay in `config.json` and never go to the phone, but while a camera runs its address is visible in this computer's process list (ffmpeg's command line). The pictures are not stored, and the log names the camera, never its address | The camera's own RTSP login and stream requests |
| Photo frame widget | Nothing outside your network: the photos in the folder you choose in **Settings › General** go from the agent to the phone over USB or the paired Wi-Fi link (on Windows and macOS large JPEG and PNG photos are scaled down first), one at a time and only while an enabled page shows the widget. Clearing the folder setting or removing the widget stops it, and clearing the folder also deletes the scaled copies. Photos that are only in the cloud (OneDrive Files On-Demand and the like) are skipped, so nothing is downloaded for the slideshow | Nothing |
| Photo frame widget, Immich source | The Immich server address you enter in **Settings › General › Photo frame** (off by default: the source is a folder until you choose **An Immich album**). The agent fetches the album's list at most every five minutes and each photo's preview-size picture as the slideshow shows it, only while an enabled page shows the widget; the phone never contacts the server and never sees its address, the key or Immich's asset ids. Redirects are not followed. The albums list in settings is requested only when you press **Load albums**. Turn it off by switching **Photos from** back to **A folder on this computer** or by removing the widget | The API key you enter (in an `x-api-key` header) and the requests for the album and its pictures. The address and key stay in `config.json` on this computer |
| Album art on Linux | The artwork address your music player reports | The request to that address |
| OSC controls (Control Deck) | The OSC host and port you configure, over UDP | The control value |
| Antigravity quota through its CLI | Whatever the Antigravity CLI contacts with its own sign-in (PhoneBox only starts it) | Decided by the CLI |
| Webhooks | Inbound only: scripts and services you set up post to the agent | Nothing is sent out |
| Setup scripts (`setup.ps1`, `setup.sh`), when you run them | `raw.githubusercontent.com` (the script), `api.github.com` and `github.com` (release info and downloads); only for what is missing and after asking: winget, Homebrew or your distribution's package servers, or `dl.google.com` (adb) | The requests themselves; nothing about you or the computer. With `gh` 2.93.0 or later signed in, the GitHub CLI checks the build provenance with its own sign-in, which also contacts `tuf-repo.github.com` and Sigstore's servers (`tuf-repo-cdn.sigstore.dev`) |

When the phone has no internet connection of its own, the agent can fetch the public widget feeds above (Open-Meteo,
Hacker News, Frankfurter, CoinGecko) on its behalf. Only those hosts are allowed, and the same data is sent.

Third-party services have their own privacy policies; PhoneBox does not control what they log.

## Contact

Questions or concerns: open an issue at <https://github.com/ersinkoc/phonebox/issues>, or for anything sensitive see
[SECURITY.md](SECURITY.md).
