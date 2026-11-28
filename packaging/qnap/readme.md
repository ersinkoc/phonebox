# Running PhoneBox on QNAP Container Station

Run the [PhoneBox](https://github.com/ersinkoc/phonebox) Linux agent 24/7 in QNAP Container Station so an Android phone or tablet can stay connected over Wi-Fi without leaving a desktop computer turned on.

## What works on a NAS (and what doesn't)

| Works 24/7 on QNAP | Desktop-only (won't reflect your PC) |
| :--- | :--- |
| Clocks, weather, calendars, timers, checklists, notes | Desktop PC CPU, GPU, and hardware sensors |
| RSS news feeds, stock watchlists, webhooks, rules | Desktop media controls and volume |
| Home Assistant, Plex, GitHub status | Desktop app shortcuts, macros, and trackpad |
| NAS CPU/RAM/disk stats and QNAP Docker containers (optional) | Local desktop AI coding sessions |

PhoneBox supports pairing one Android device with multiple hosts ([README](https://github.com/ersinkoc/phonebox/blob/main/README.md)), so you can pair your phone or tablet with both your QNAP (for an always-on desk display) and your PC (when it is powered on).

## Requirements

* A QNAP NAS running **Container Station** on an **x64** or **ARM64** CPU (the official Linux releases require glibc; Alpine/musl is not supported per [docs/SETUP.md](https://github.com/ersinkoc/phonebox/blob/main/docs/SETUP.md)).
* An Android 7.0+ phone or tablet with the PhoneBox APK installed from [GitHub Releases](https://github.com/ersinkoc/phonebox/releases).
* Both the QNAP NAS and Android device on the same local network (or connected via Tailscale).

## 1. Create the application in Container Station

1. Open **Container Station** in QTS / QuTS hero and go to **Applications › Create**.
2. Name the application `phonebox`.
3. Paste the contents of `docker-compose.yml` into the YAML editor and click **Create**.
4. Wait ~30–60 seconds on first startup while the container installs `curl`, `ca-certificates`, `unzip`, and `libicu72`, then downloads and SHA-256-verifies the latest Linux release via `setup.sh` ([docs/SETUP.md](https://github.com/ersinkoc/phonebox/blob/main/docs/SETUP.md)).

### Why the Compose file is structured this way

* **`debian:bookworm-slim` base image:** PhoneBox's self-contained Linux builds target glibc rather than musl/Alpine.
* **Non-root `phonebox` user:** `setup.sh` refuses to run as `root`, so the container creates a dedicated `phonebox` user and runs both setup and the agent under that account.
* **Parent volume mount (`/home/phonebox/.local/share/PhoneBox`):** Mounting the parent directory instead of `.../app` allows `setup.sh` and PhoneBox's self-updater to rename `app` to `app.old` without hitting `Device or resource busy`.
* **`network_mode: host`:** Ensures Wi-Fi pairing advertises the NAS's real LAN IP on port `8766` rather than an internal Docker bridge IP.
* **Optional `phonebox-proxy` sidecar:** PhoneBox binds its settings UI strictly to `127.0.0.1:8765` ([README](https://github.com/ersinkoc/phonebox/blob/main/README.md)). The lightweight `nginx:alpine` sidecar forwards LAN port `18765` to `127.0.0.1:8765` so you can configure PhoneBox from a browser on another computer without SSH.

## 2. Open PhoneBox settings

Choose one of two ways to reach the settings page on your QNAP:

* **Using the included `phonebox-proxy` container (easiest):** Open `http://<qnap-ip>:18765` in a browser on your computer.
* **Using an SSH tunnel (strictest security — no proxy container needed):** Remove the `phonebox-proxy` service from `docker-compose.yml`, enable SSH on your QNAP (**Control Panel › Network & File Services › Telnet / SSH**), run the command below from your computer, and open `http://127.0.0.1:18765`:
  ```bash
  ssh -L 18765:127.0.0.1:8765 <qnap-user>@<qnap-ip>
  ```

## 3. Enable Wi-Fi and pair your Android device

1. In the PhoneBox settings page, open **General** and scroll to **Wi-Fi & Tailscale** ([docs/SETUP.md](https://github.com/ersinkoc/phonebox/blob/main/docs/SETUP.md)).
2. Turn on **Let the phone connect without the USB cable**.
3. Click **Save**, then click **Restart to apply** at the top of the page. Wait a few seconds for the container to restart and refresh the page.
4. Confirm the status line next to **Pair a phone over Wi-Fi** reads `Listening on port 8766`.
5. Click **Pair a phone over Wi-Fi**, scan the **pairing QR code** (the second QR code) with your Android device's camera, open the link in PhoneBox, and confirm the matching check code shown in your browser ([docs/SETUP.md](https://github.com/ersinkoc/phonebox/blob/main/docs/SETUP.md)).

## 4. Configure QNAP QuFirewall (if enabled)

If **QuFirewall** is active on your QNAP, allow TCP port `8766` from your local subnet ([QNAP QuFirewall Guide](https://www.qnap.com/en/how-to/tutorial/article/how-to-use-qufirewall)):

1. Open **QuFirewall › Firewall Profiles** and click **Edit** on your active profile.
2. Click **Add Rule** with:
   * **Permission:** `Allow`
   * **Interface:** `All` (or your active LAN adapter)
   * **Source:** `IP` → **IP subnet** → your home LAN subnet (for example, `192.168.1.0` / `255.255.255.0`)
   * **Protocol:** `TCP`
   * **Service Port:** `Custom` → `8766` (add `18765` as `8766,18765` if your profile blocks LAN access to the settings proxy)
3. Click **Apply**, make sure the rule's **ON** checkbox is enabled, and drag it **above** any `Deny` rule at the bottom of the list.
4. Click **Apply** on the profile to save.

## Security and privacy notes

* **Stop `phonebox-proxy` when you are done:** PhoneBox's settings page has no login screen because it is designed to stay local ([README](https://github.com/ersinkoc/phonebox/blob/main/README.md)). Only `phonebox` needs to run 24/7. Once your tablet is paired and your pages are configured, stop `phonebox-proxy` in Container Station and start it only when you want to edit layouts.
* **Keep port `18765` strictly on your private LAN:** Never port-forward `18765` on your router to the internet.
* **Docker socket is opt-in:** `/var/run/docker.sock` is commented out by default. Only uncomment it if you want PhoneBox's Docker widget to monitor your QNAP containers.
* **Outbound connections:** On startup, the container contacts `deb.debian.org` to install `curl`, `ca-certificates`, `unzip`, and `libicu72`, and on first install contacts `raw.githubusercontent.com` and `github.com` via `setup.sh` to download and checksum-verify the release. All agent outbound connections are documented in [PRIVACY.md](https://github.com/ersinkoc/phonebox/blob/main/PRIVACY.md).

## Troubleshooting

* **`502 Bad Gateway` on `http://<qnap-ip>:18765`:** `phonebox-proxy` is reachable, but `phonebox` is not listening on `127.0.0.1:8765` yet. Wait 30–60 seconds on first boot for package installation and download to finish, then check the individual `phonebox` container logs in Container Station.
* **Tablet cannot reach port `8766`:** Verify that `General › Wi-Fi & Tailscale` shows `Listening on port 8766`, that both devices are on the same subnet, and that your QuFirewall `Allow` rule is enabled and placed above any `Deny` rule.
* **Updating the PhoneBox agent:** Your settings and pairings persist in `/share/Container/phonebox/config`, and the installed agent persists in `/share/Container/phonebox/share/app`. PhoneBox's built-in updater can update the agent in place, or you can stop the container, clear `/share/Container/phonebox/share/app`, and start it again to pull the latest release via `setup.sh`.
