# Privacy policy

PhoneBox has no accounts, no servers of its own, no telemetry, no analytics and no crash reporting. The project never
receives any data from you. This page lists every place the agent (on your computer) and the Android app (on your
phone) connect to, so you can see exactly what leaves your machine and turn it off.

Last updated: 2026-11-15 (PhoneBox 0.9.6).

## What stays on your devices

- **Settings and layouts**: `config.json` in `%APPDATA%\PhoneBox` (Windows), `~/.config/PhoneBox` (Linux) or
  `~/Library/Application Support/PhoneBox` (macOS), plus `data.json` (pushed values and notifications) next to it.
  On macOS and Linux these files are readable only by your user (0600).
- **Discord login**: if you use the Discord widget, `discord-token.json` next to `config.json` holds the access and
  refresh token Discord gave PhoneBox (on macOS and Linux readable only by your user).
- **Last Claude quota reading**: `claude-quota.json` next to the log (usage percentages and reset times, no token),
  so the panel can show the last values after a restart, and `claude-quota-schedule.json` (when the next check may
  run, the last error message and, after Anthropic refused a token, a 16-character hash of that token, never the token
  itself), so a restart doesn't ask Anthropic again sooner than it should.
- **Log**: `agent.log` in `%LOCALAPPDATA%\PhoneBox` (Windows), `~/.local/share/PhoneBox` (Linux) or
  `~/Library/Application Support/PhoneBox` (macOS).
- **Phone app**: caches (weather, rates, widget state) in its own app storage. The app asks for two permissions only:
  `INTERNET` and `VIBRATE`.
- **Computer data shown on the panel** (CPU, GPU, memory, disks, processes, media, volume, AI coding sessions and
  quotas, Docker / Podman, OBS) is read locally and goes only to your phone: over the USB cable (`adb reverse` to
  `127.0.0.1`) or, after pairing, over Wi-Fi / Tailscale with a pinned certificate and a key.
- **Local services**: LibreHardwareMonitor's web server (`127.0.0.1:8085`), a running Antigravity language server
  (loopback), the Docker / Podman socket, OBS WebSocket and the Discord desktop app's local connection (a named pipe
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
| Weather (phone) | `api.open-meteo.com`, every 15 minutes | The coordinates of the city you chose (Tallinn by default) | From 0.9.6: asked only while a page has a widget that shows the weather (weather, forecast, sun & moon, a clock with its weather line, or a Custom HTML widget, which gets the weather too); remove those, or turn off the clock's weather line. 0.9.5 and earlier: asked while the panel runs; choose a city you are comfortable sharing in **Settings › General › Weather** |
| Air quality widget (phone) | `air-quality-api.open-meteo.com`, while the widget is on a page | The same coordinates | Remove the widget |
| City search (settings page) | `geocoding-api.open-meteo.com` | The city name you type, only when you search | Don't search |

## Connections made only when you use a feature

| Feature | Where | What is sent |
|---|---|---|
| Discord widget | `discord.com/api/oauth2/token`, once at login and when the token needs renewing; `cdn.discordapp.com`, once per server icon and avatar shown (the agent fetches them, the phone never contacts Discord; the rest goes through the Discord app on this PC, locally). Turn off in **Settings › General › Discord** | The application's client ID and secret, and the one-time login code or the saved refresh token; for images only the image's id and hash, which Discord already knows |
| Claude quota | `api.anthropic.com/api/oauth/usage` | The OAuth token Claude Code stores in `~/.claude/.credentials.json`, or on macOS, when that file doesn't exist, the "Claude Code-credentials" item from the default keychain search list (normally the login Keychain), read with `security find-generic-password`; read-only, PhoneBox never refreshes it |
| GitHub widget | `api.github.com`, through your own GitHub CLI (`gh`) | Whatever `gh` sends with its own sign-in |
| Hacker News widget (phone) | `hacker-news.firebaseio.com` | Nothing but the request |
| Exchange rates widget (phone) | `api.frankfurter.dev` | The currencies you chose |
| Crypto widget (phone) | `api.coingecko.com` | The coins and currency you chose |
| Internet radio | `*.api.radio-browser.info`, `opml.radiotime.com`, the station's own stream server and logo host | Station searches, the station you play and its logo request |
| Spotify control | `accounts.spotify.com`, `api.spotify.com` | Your own Spotify app's client ID and the sign-in you approve; the token is saved in `config.json.spotify.json` next to your settings |
| Spotify queue covers (phone) | `i.scdn.co`, while a Spotify queue widget shows covers. Turn off with the widget's **Show covers** option | The request for each cover image |
| Pairing over Wi-Fi without USB | `ersinkoc.github.io` (the page the QR code opens), then your computer | GitHub Pages serves the page and sees your IP address; the pairing details are after `#` in the link, which browsers never send, and the page removes them from the history. The phone then sends the one-time code only to your computer, over TLS pinned to its certificate |
| Calendars | The iCal addresses you add | The request to that address |
| App console widget | Nothing. On Windows the agent reads the process name of the focused window to pick the matching app profile; that name stays on the computer, and the phone only gets the profile's name and key labels (a key without a label shows its keys, such as ctrl+z). With the widget's **Send keys to the computer** option on (off by default), a tap sends the profile's id and the key's slot, and the agent presses the keys only if the app in front still matches that profile | Nothing |
| News feeds widget | The RSS and Atom addresses you add in **Settings › General**, fetched by the agent at most every 15 minutes and only while a page shows the widget; the phone never contacts the feed sites. With the widget's **Open articles on the PC** option on (off by default), tapping a headline opens its article in the computer's browser. Turn the requests off by removing the feeds in **Settings › General › News feeds**, or the widget from your pages | The request to that address, with the feed's last `ETag` / `Last-Modified` so an unchanged feed is not sent again |
| Home Assistant | Your Home Assistant server | The access token you enter |
| Image widget | The image address you enter | The request to that address |
| Photo frame widget | Nothing outside your network: the photos in the folder you choose in **Settings › General** go from the agent to the phone over USB or the paired Wi-Fi link (on Windows and macOS large JPEG and PNG photos are scaled down first), one at a time and only while an enabled page shows the widget. Clearing the folder setting or removing the widget stops it, and clearing the folder also deletes the scaled copies. Photos that are only in the cloud (OneDrive Files On-Demand and the like) are skipped, so nothing is downloaded for the slideshow | Nothing |
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
