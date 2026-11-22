# Privacy policy

PhoneBox has no accounts, no servers of its own, no telemetry, no analytics and no crash reporting. The project never
receives any data from you. This page lists every place the agent (on your computer) and the Android app (on your
phone) connect to, so you can see exactly what leaves your machine and turn it off.

Last updated: 2026-11-20 (PhoneBox 0.9.6).

## What stays on your devices

- **Settings and layouts**: `config.json` in `%APPDATA%\PhoneBox` (Windows), `~/.config/PhoneBox` (Linux) or
  `~/Library/Application Support/PhoneBox` (macOS), plus `data.json` (pushed values and notifications) next to it.
  On macOS and Linux these files are readable only by your user (0600).
- **Discord login**: if you use the Discord widget, `discord-token.json` next to `config.json` holds the access and
  refresh token Discord gave PhoneBox (on macOS and Linux readable only by your user).
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
  the first time.
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
- **Local services**: LibreHardwareMonitor's web server (`127.0.0.1:8085`), a running Antigravity language server
  (loopback, over HTTP or HTTPS), the Docker / Podman socket, OBS WebSocket and the Discord desktop app's local connection (a named pipe
  or socket on this computer) are local connections.

[`scripts/uninstall.ps1 -Purge`](scripts/uninstall.ps1) and [`scripts/uninstall.sh --purge`](scripts/uninstall.sh)
remove the agent together with its settings, pushed data and log (without the switch, settings are kept). Uninstalling
the Android app removes everything on the phone.

## Connections made by default

Each of these servers sees your IP address, like any web request. PhoneBox sends none of them an identifier for you
or your devices, with one exception: the update check can retry with your GitHub CLI token (see below), which
identifies your GitHub account to GitHub.

| What | Where | What is sent | How to turn it off |
|---|---|---|---|
| Update check | `api.github.com` (release info), `github.com` (downloads) | The repository name; with `gh` signed in and only when GitHub turns away an anonymous request, your GitHub CLI token, to `api.github.com` only | **Settings › General › Updates › Install new releases automatically** turns off installing. The check every 6 hours stays; it sends nothing about you |
| Network latency (ping metric, "ping high" rule) | `1.1.1.1` (Cloudflare DNS) by default, an ICMP echo every 5 seconds | An empty ping packet | From 0.9.6: empty **Settings › General › Ping**, or enter another host. 0.9.5 and earlier: not switchable; block ICMP to `1.1.1.1` in your firewall |
| Weather (phone) | `api.open-meteo.com`, every 15 minutes | The coordinates of the city you chose (Tallinn by default), and the forecast model when one is picked in **Settings › General › Weather** | From 0.9.6: asked only while a page has a widget that shows the weather (weather, forecast, sun & moon, a clock with its weather line, or a Custom HTML widget, which gets the weather too); remove those, or turn off the clock's weather line. 0.9.5 and earlier: asked while the panel runs; choose a city you are comfortable sharing in **Settings › General › Weather** |
| Air quality widget (phone) | `air-quality-api.open-meteo.com`, while the widget is on a page | The same coordinates | Remove the widget |
| City search (settings page) | `geocoding-api.open-meteo.com` | The city name you type, only when you search | Don't search |
| Claude quota | `api.anthropic.com/api/oauth/usage`, every 3 minutes after a successful check (sooner after a failed one, as often as every 30 seconds while the network is down; less often when Anthropic asks it to wait; agents running on this computer share one schedule) while Claude Code is signed in on this computer, whether or not a page shows a quota widget | The OAuth token Claude Code stores in `~/.claude/.credentials.json`, or on macOS, when that file doesn't exist, the "Claude Code-credentials" item from the default keychain search list (normally the login Keychain), read with `security find-generic-password`; read-only, PhoneBox never refreshes it. Only the Claude Code command-line sign-in is used; the Claude Desktop app's own sign-in is never read | No switch yet: it stops when Claude Code is signed out on this computer |

## Connections made only when you use a feature

| Feature | Where | What is sent |
|---|---|---|
| Codex live quota (opt-in) | Off by default until turned on in **Settings › General › AI quotas & CodexBar › Read live Codex quotas with the Codex CLI**, and only while an enabled page shows Codex quotas and fresh CodexBar data is not already active. Runs the local `codex` CLI (`codex app-server`), which contacts its configured OpenAI/ChatGPT endpoints (by default `chatgpt.com` / `api.openai.com`) about every 3 minutes, backing off up to 30 minutes on failure. Turn off by unchecking **Read live Codex quotas with the Codex CLI** | PhoneBox sends only `initialize` metadata and `account/rateLimits/read` over local stdio (no prompt or thread) and never reads or logs credentials; the Codex CLI uses its own sign-in and may refresh and rewrite `~/.codex/auth.json` |
| Discord widget | `discord.com/api/oauth2/token`, once at login and when the token needs renewing; `cdn.discordapp.com`, once per server icon and avatar shown (the agent fetches them, the phone never contacts Discord; the rest goes through the Discord app on this PC, locally). Turn off in **Settings › General › Discord** | The application's client ID and secret, and the one-time login code or the saved refresh token; for images only the image's id and hash, which Discord already knows. For favourite servers and channels the names and avatars of the people in voice channels you are not in reach the phone too (read from the Discord app on this PC, kept in memory only) |
| Steam friends and activity | `api.steampowered.com`, only while Steam is enabled and an enabled page contains either widget. Clear the API key, turn off Steam, or remove both widgets to stop requests | The API key in the request header; your SteamID64 for the friend-list request; your custom profile name as `vanityurl` only when you save it; and friend SteamIDs in player-summary batches. Steam returns names, avatars, presence and public game details. The activity list is built from successive polls and stays in memory only |
| Steam avatars | `*.steamstatic.com` and `steamcdn-a.akamaihd.net`, only when the agent fetches an avatar shown by either widget. Clear the API key, turn off Steam, or remove both widgets to stop requests | A request to the avatar URL Steam returned (including its image hash); the Web API key and SteamID64 are not sent to these image hosts |
| Synced lyrics widget | `lrclib.net` (LRCLIB, free and open, no account), off until you turn on a Synced lyrics widget's **Look up lyrics on LRCLIB** (per widget). Then asked by the agent when a song starts, only for a widget on an enabled page with the option on; the phone never contacts it. The exact lookup sends the title, artist, album and length; when it finds nothing (or the song is longer than an hour), a search sends the title and artist only. A song is asked again only once its answer has expired or was dropped from the 100. Answers are kept in memory only, never on disk: an answer is reused for 6 hours for a song that was found and 30 minutes for one that was not, at most 100 songs; an expired answer is dropped the next time any widget asks for lyrics, and all of them when the agent stops; after a failure nothing is asked for a while (1 minute, doubling up to 30, or as long as LRCLIB's Retry-After asks). Turn the option off or remove the widget to stop the requests | The title, artist, album and length of the track playing on this computer, and a `PhoneBox/<version> (+https://github.com/ersinkoc/phonebox)` user agent; nothing about you |
| GitHub widget | `api.github.com`, through your own GitHub CLI (`gh`) | Whatever `gh` sends with its own sign-in |
| Hacker News widget (phone) | `hacker-news.firebaseio.com` | Nothing but the request |
| Exchange rates widget (phone) | `api.frankfurter.dev` | The currencies you chose |
| Crypto widget (phone) | `api.coingecko.com` | The coins and currency you chose |
| Internet radio | `*.api.radio-browser.info`, `opml.radiotime.com`, the station's own stream server and logo host | Station searches, the station you play and its logo request |
| Spotify control | `accounts.spotify.com`, `api.spotify.com` | Your own Spotify app's client ID and the sign-in you approve; the token is saved in `config.json.spotify.json` next to your settings |
| Spotify queue covers (phone) | `i.scdn.co`, while a Spotify queue widget shows covers. Turn off with the widget's **Show covers** option | The request for each cover image |
| Plex control (Caldera Amp) | The Plex server address you enter in Settings (which may be on the internet) and the player address (this computer, `127.0.0.1:32500`, by default). The widget's requests are made only while an enabled page shows a Plex queue widget; remove the widget or disable its page to stop them. **Check Plex connection** in Settings sends one check each time you press it, with or without a widget. Clearing the server address or the token stops both | To the server: the token you enter, in an `X-Plex-Token` header, and the requests for its identity and the current play queue. To the player: timeline polls (what it is playing) and the skip command when you tap a queued track, without the token. The addresses and token stay in `config.json`. |
| Pairing over Wi-Fi without USB | `ersinkoc.github.io` (the page the QR code opens), then your computer | GitHub Pages serves the page and sees your IP address; the pairing details are after `#` in the link, which browsers never send, and the page removes them from the history. The phone then sends the one-time code only to your computer, over TLS pinned to its certificate |
| Calendars | The iCal addresses you add | The request to that address |
| Dictation widget | Only while you hold a dictation open (the microphone button is red, at most a minute): the phone's own speech recognizer hears you. On most phones that is Google's, which may send the audio to Google. With the widget's **Recognize on the phone only** option the app uses Android's on-device recognizer, which keeps the audio on the phone; that needs Android 12 or later and the offline language, and on older Android the option refuses to listen rather than fall back. The recognized text goes from the phone to the agent and is typed into the app in front if the widget's **Type the text on the computer** option is on (off by default). Neither the audio nor the text is stored or logged by PhoneBox. Android asks for the microphone the first time the button is tapped; you can take it back in Android's app settings | Google (or your phone's recognizer), only while dictating; nothing with on-device recognition |
| App console widget | Nothing. On Windows the agent reads the process name of the focused window to pick the matching app profile; that name stays on the computer, and the phone only gets the profile's name and key labels (a key without a label shows its keys, such as ctrl+z). With the widget's **Send keys to the computer** option on (off by default), a tap sends the profile's id and the key's slot, and the agent presses the keys only if the app in front still matches that profile | Nothing |
| News feeds widget | The RSS and Atom addresses you add in **Settings › General**, fetched by the agent at most every 15 minutes and only while a page shows the widget; the phone never contacts the feed sites. With the widget's **Open articles on the PC** option on (off by default), tapping a headline opens its article in the computer's browser. Turn the requests off by removing the feeds in **Settings › General › News feeds**, or the widget from your pages | The request to that address, with the feed's last `ETag` / `Last-Modified` so an unchanged feed is not sent again |
| Home Assistant | Your Home Assistant server | The access token you enter |
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
