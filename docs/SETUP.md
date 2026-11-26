# Installation and connections

[Back to PhoneBox](../README.md) · [User guide](GUIDE.md) · [Development](DEVELOPMENT.md)

[Quick start](#quick-start) · [Wi-Fi & Tailscale](#wi-fi--tailscale) · [Updates](#updates) · [FAQ & troubleshooting](#faq--troubleshooting)

## Quick start

### 1. Install PhoneBox on the computer

**Windows 10/11 x64 or Windows 11 ARM64**: open PowerShell (a normal window, not *Run as administrator*) and paste:

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux and macOS**: open a terminal and paste (run it as yourself, not with `sudo`):

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Setup installs the [latest release](https://github.com/ersinkoc/phonebox/releases/latest) and anything missing:

1. **Checks the computer**: Windows x64 / ARM64; Linux x64 / ARM64 with glibc; macOS on Apple silicon or Intel.
2. **Downloads and checks the agent**: the download must match the release's SHA-256 checksums. With the
   [GitHub CLI](https://cli.github.com) 2.93.0 or later signed in, it also verifies the build provenance (`gh attestation verify`).
   No .NET to install: the agent is one self-contained program.
3. **Installs adb if it is missing**, after asking. adb is the USB link to the phone. Setup gets it from winget, Homebrew or
   your distribution (apt, dnf, pacman, zypper), or from Google's download as a fallback.
4. **Offers optional helpers** for more widgets. On Linux: `playerctl` for Now Playing, `pactl` for volume, and `xprintidle`.
   On macOS: `nowplaying-cli`.
5. **Installs the agent for your user only**:

   | | Program | Settings (kept on reinstall) |
   |---|---|---|
   | Windows | `%LOCALAPPDATA%\PhoneBox\app` | `%APPDATA%\PhoneBox` |
   | Linux | `~/.local/share/PhoneBox/app` | `~/.config/PhoneBox` |
   | macOS | `~/Library/Application Support/PhoneBox/app` | `~/Library/Application Support/PhoneBox` |

   It adds a Start Menu shortcut on Windows, and asks whether to start PhoneBox at login: a Startup-folder shortcut
   on Windows, a systemd user service on Linux, a launchd agent on macOS.
6. **Puts the app on the phone over USB**, if you want. It shows how to turn on USB debugging and waits for the phone.
7. **Starts the agent** and opens its settings at <http://127.0.0.1:8765>.

The agent installs for your user. System package managers may request administrator rights for missing dependencies;
on Linux, setup asks before using `sudo`. Run it again
any time to update or repair PhoneBox; settings and pairings are kept. Both scripts skip devices listed under
**Settings › General › Phone connection › Ignored USB devices**, even when passed an explicit serial. If the
installer cannot verify the USB policy, it installs the desktop agent and skips the phone app.
This also applies when an older agent cannot read the policy from an existing configuration. Read [`setup.ps1`](../setup.ps1) or
[`setup.sh`](../setup.sh) before you run it if you like. With `-DryRun` / `--dry-run` it downloads and checks everything
and changes nothing.

<details><summary><b>Setup options</b></summary>

| Windows (`setup.ps1`) | Linux / macOS (`setup.sh`) | |
|---|---|---|
| `-Yes` | `--yes` | Take the default answer to every question (unattended) |
| `-NoPhone` | `--no-phone` | Do not install the app over USB (for a Wi-Fi-only phone) |
| `-Serial <id>` | `--serial <id>` | The phone to use when several are connected (`adb devices`) |
| `-NoAutostart` | `--no-autostart` | Do not start PhoneBox at login |
| `-NoStart` | `--no-start` | Install only |
| — | `--no-extras` | Do not offer the optional helpers |
| `-Version 0.9.8` | `--version 0.9.8` | Install that release instead of the latest |
| `-DryRun` | `--dry-run` | Download and check, change nothing |

To pass options:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1))) -NoPhone
```

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash -s -- --no-phone
```

</details>

<details><summary><b>Before you run it: warnings</b></summary>

- **The Windows agent is not code-signed yet.** [SignPath Foundation](../README.md#code-signing-policy) accepted the project; releases are signed once its release certificate is issued.
  Windows SmartScreen or an antivirus may warn about a new, unsigned program, and some antivirus engines have flagged
  it ([#69](https://github.com/ersinkoc/phonebox/issues/69)). Setup only installs a file that matches the release
  checksum. If your antivirus removes `PhoneBoxAgent.exe`, setup says so; restore it from quarantine or report it.
- **Smart App Control** (Windows 11) blocks programs that are neither signed nor known to Microsoft. The agent is
  not code-signed yet (signing through SignPath Foundation waits for its release certificate, [#56](https://github.com/ersinkoc/phonebox/issues/56)),
  so with Smart App Control on, Windows refuses to start it: "An Application Control policy has blocked this file"
  ([#69](https://github.com/ersinkoc/phonebox/issues/69)). Setup warns you first, still installs it, and explains
  if Windows blocks it. Please keep Smart App Control on: on older Windows builds it cannot be turned back on without
  resetting Windows. In evaluation mode it blocks nothing yet. On a work or school computer, an AppLocker or group
  policy can block the agent too; its administrator can allow it.
- **USB debugging gives a computer you allow control over the phone.** Allow only your own computers. You can
  withdraw them on the phone under *Developer options › Revoke USB debugging authorisations*.
- **Wi-Fi is off until you turn it on.** Until then the agent listens only on this computer (`127.0.0.1:8765`).
  When you turn it on, Windows asks to let PhoneBox through the firewall: allow *Private* networks only. On Linux
  with ufw or firewalld, open TCP port 8766 for your local network. Every connection PhoneBox makes is listed in
  the [privacy policy](../PRIVACY.md).
- **Linux USB permissions**: if `adb devices` shows `no permissions`, adb also needs udev rules. Install
  `android-sdk-platform-tools-common` on Debian/Ubuntu or `android-udev` on Arch. Some systems also need the
  `plugdev` group (`sudo usermod -aG plugdev $USER`, then log out and in). Setup points this out when it happens.
- **Linux shortcuts that type or press keys** use `xdotool` on X11. On Wayland they use `wtype` (wlroots, KDE) or
  `ydotool` (GNOME, which takes neither of the others). PhoneBox uses ydotool 0.1.x (Ubuntu 24.04's `ydotool`
  package, `sudo apt install ydotool`; 1.x isn't supported yet), which needs no `ydotoold` but needs write access to
  `/dev/uinput`. Give that access through a group of its own, not `input`: the `input` group can usually also read
  the real keyboards (`/dev/input/event*`), and with them every password typed. For example:
  `sudo groupadd --system uinput`, the udev rule `KERNEL=="uinput", GROUP="uinput", MODE="0660",
  OPTIONS+="static_node=uinput"` in `/etc/udev/rules.d/60-uinput.rules`, `sudo usermod -aG uinput $USER`, then
  restart. Any program you run can then send key presses and mouse input, as PhoneBox does. ydotool presses the keys
  of a US layout, so on other layouts some characters come out different, and it types only ASCII.
- **On macOS**, the agent is signed with PhoneBox's own certificate, not an Apple Developer ID. The first time,
  macOS may ask whether it may control Music or Spotify (for Now Playing), or accept incoming connections (with Wi-Fi
  on). Shortcuts that press keys, and the Trackpad widget (its mouse events and its gestures), need *System Settings ›
  Privacy & Security › Accessibility* for PhoneBoxAgent; it
  carries over to later updates. Before 0.9.22 the agent was signed ad hoc, and macOS dropped the permission with every
  update, so after updating from an older version allow it once more: remove PhoneBoxAgent there with *−* and add it
  again, even if it is listed and switched on ([#269](https://github.com/ersinkoc/phonebox/issues/269)).
- **The macOS app lives in the menu bar.** Running as `PhoneBox.app` it has no Dock icon or window; a phone icon in the
  menu bar shows whether the phone is connected and offers Open Settings, Check for Updates (and Install update when
  there is one), Restart Agent, Show Log, Open Config Folder, Start at Login and Quit. Start at Login adds a launchd
  agent (`net.oxog.phonebox.app`) that opens the app, found by its bundle identifier so moving the app does not break it,
  at your next login; it is greyed out while `setup.sh`'s own login agent exists (and `setup.sh` removes this one, so the
  two never both start). Quitting from the system (logout, `osascript`) stops the agent the same way the menu's Quit does. The agent started by `setup.sh` or from a terminal has no menu bar icon; `--menu-bar` adds one,
  `--no-menu-bar` (or `PHONEBOX_NO_MENU_BAR=1`) removes it from the app.
- **Windows USB drivers**: if the phone never appears, Windows may need its USB driver. Google phones use the
  Google USB Driver; other brands have one on their support site.
- **Scoop users**: setup stops if PhoneBox is installed with Scoop, so two copies don't fight over the same port.
  Keep using `scoop update phonebox`, or `scoop uninstall phonebox` first and then run setup.
- **musl Linux** (Alpine) has no release build yet.

</details>

#### Windows on ARM

The first release with a native ARM64 package includes `PhoneBoxAgent-<version>-win-arm64.zip`.
Run the Windows setup command above to install it, or extract the ZIP and run `PhoneBoxAgent.exe`.
The setup script selects ARM64 even when PowerShell itself is running under emulation. Windows on ARM requires Windows 11.

For older releases, try the portable `win-x64` ZIP using [Windows 11 x64 emulation](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation).
Update a portable copy manually. Wi-Fi pairing below works without Android USB drivers.

### 2. Connect the phone

- **Over USB** (recommended): it wakes the phone on plug-in, updates the app and falls back to USB when Wi-Fi drops.
  The phone needs Android 5.0 or later (Android 6 or later recommended) with USB debugging turned on:
  1. *Settings › About phone*: tap *Build number* seven times.
  2. *Developer options*: turn on *USB debugging*. On Xiaomi, Redmi and POCO, also turn on *Install via USB*.
  3. Plug the phone in with a data cable and answer *Allow USB debugging?* with *Allow*.

  Setup installs the app, or use *Settings › General › Install the phone app* later. Then the phone wakes up and
  opens the panel whenever it is plugged in.
- **Over Wi-Fi only**, with no developer options or adb: install the APK from the
  [release page](https://github.com/ersinkoc/phonebox/releases/latest) on the phone. Then on the computer turn on
  *Settings › General › Wi-Fi & Tailscale*, click **Pair a phone over Wi-Fi** and scan the QR code (see
  [Wi-Fi & Tailscale](#wi-fi--tailscale)).

The settings are at <http://127.0.0.1:8765>, on Windows also from the tray icon.

### Other ways to install

- **Manual download**: the [release](https://github.com/ersinkoc/phonebox/releases/latest) has the signed Android
  APK and agents for Windows x64/ARM64, Linux x64/ARM64 and macOS Apple silicon/Intel, with SHA-256 checksums and build
  provenance. Unzip the agent and run it. It can update itself only from the install folder in the table above. On a Mac,
  the `.dmg` holds `PhoneBox.app`: drag it to Applications (or `~/Applications`) and open it. It has no window; the settings
  are at http://127.0.0.1:8765. If you installed with `setup.sh`, that agent is already running and holds the port, so first quit it
  and run `launchctl unload ~/Library/LaunchAgents/net.oxog.phonebox.agent.plist` (otherwise opening the app only reopens its settings,
  and the login job starts the old agent again). It is not notarized, so macOS blocks it the first time: open it once, then allow it in *System Settings › Privacy &
  Security › Open Anyway* (right-click › Open works only up to macOS 14). The Accessibility permission is recorded per
  app, so allow PhoneBox there once more when you move between this app and an agent started from the zip or `setup.sh`.
- **Scoop** (Windows), which can then put the app on the phone from *Settings › General › Install the phone app*:

  ```powershell
  scoop bucket add phonebox https://github.com/ersinkoc/phonebox
  scoop install main/adb phonebox/phonebox
  ```

- **winget**: a package (`ersinkoc.PhoneBox`) is prepared in [`packaging/winget`](../packaging/winget) for
  winget-pkgs. Once it is accepted, `winget install ersinkoc.PhoneBox` will install it.
- **Windows x64 installer**: `PhoneBox-<version>-win-x64-setup.exe` on the release installs for the current user (no
  administrator), into the folder the agent updates itself in, with a Start Menu shortcut and an optional *Start PhoneBox
  with Windows*. Uninstall it from *Settings › Apps*; your settings are kept. The agent updates itself from the release,
  so *Apps & features* keeps showing the version the installer wrote (the one first installed); *Settings › General ›
  Updates* shows the running one. Run again with nothing ticked to turn *Start with Windows* off (a silent run with no
  `/TASKS=` or `/MERGETASKS=` leaves it as it is). The installer is not code-signed, and neither is the agent yet (see
  above), so SmartScreen may ask once: *More info › Run anyway*.
- **From source** (for development): needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and `adb`. To build
  the phone app it also needs **JDK 17** and **Android SDK Platform 35** (for example through Android Studio), with
  `JAVA_HOME` set and `ANDROID_HOME` or `android/local.properties` (`sdk.dir`) pointing at the SDK.

  ```powershell
  git clone https://github.com/ersinkoc/phonebox; cd phonebox
  .\scripts\install.ps1 -Apk -Autostart   # add -Serial <id> with several devices; -BuildApk always rebuilds the app
  ```

  ```sh
  git clone https://github.com/ersinkoc/phonebox && cd phonebox
  ./scripts/install.sh --apk --autostart  # systemd user service (Linux) or launchd agent (macOS)
  ```

### Stop and start the agent

On Windows, use **Quit** in the tray menu. Start it again from the Start Menu (search for **PhoneBox**); with
**Start with Windows** on, it also starts at the next sign-in.
On Linux, setup installs a systemd user service named `phonebox` (no sudo needed):

```sh
systemctl --user stop phonebox            # off until the next login; start / restart / status work the same way
systemctl --user disable --now phonebox   # off, and no longer started at login (enable --now turns both back on)
```

On macOS, `launchctl unload ~/Library/LaunchAgents/net.oxog.phonebox.agent.plist` stops it, and `launchctl load`
with the same path starts it again. While the agent is off, the phone shows that it is offline and reconnects by
itself once the agent runs again.

### Uninstall

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/scripts/uninstall.ps1))) # add -Purge -Apk
```

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/scripts/uninstall.sh | bash -s --   # add --purge --apk
```

Without options it removes the program and the start-at-login entry and keeps the settings. `-Purge` / `--purge` also
deletes the settings, and `-Apk` / `--apk` removes the app from the connected phone.

If you uninstall PhoneBox while **power standby** is on, the phone may keep *stay awake while charging* switched off.
Turn standby off first (**Settings › General › Display › Power standby: Off**), which puts the setting back. If it is
already off, turn *Stay awake* back on in the phone's Developer options, or run
`adb shell settings put global stay_on_while_plugged_in 7` (use your own value if it was different).

### What works where

<details><summary><b>Full platform compatibility</b></summary>

| | Windows | Linux | macOS |
|---|---|---|---|
| CPU, RAM, network, disks, ping | ✓ | ✓ | ✓ |
| CPU temperature | LibreHardwareMonitor | ✓ (hwmon) | — |
| CPU power | LibreHardwareMonitor | RAPL, where the agent may read it (most distributions allow only root) | — |
| GPU load, VRAM, temperature | Any vendor (Windows GPU counters); NVIDIA adds power, fan and clocks (`nvidia-smi`), AMD / Intel add them with LibreHardwareMonitor | NVIDIA (`nvidia-smi`), AMD (`amdgpu` sysfs), Intel (`i915` / `xe`: load and clock) | Apple silicon (`ioreg`) |
| Now playing + controls | ✓ (apps in Windows' media controls) | ✓ (MPRIS via `playerctl`) | System media, including browser video, with `nowplaying-cli`; Spotify and Music fallback |
| Volume | ✓ | ✓ (`wpctl` / `pactl`) | ✓ |
| Processes, end one / all | ✓ | ✓ (your own) | ✓ (your own) |
| App icons | ✓ | ✓ (icon theme) | ✓ (app bundles) |
| Shortcuts, lock, screen off | ✓ | ✓ (`loginctl`, `xset`) | ✓ (`open`, `pmset`) |
| Command widgets | PowerShell | bash | zsh |
| Claude / Codex quotas & sessions, GitHub, webhooks | ✓ | ✓ | ✓ (Claude token from the Keychain) |
| Tray icon | ✓ | — (settings in the browser) | — |
| Start at login | Startup folder | systemd user service | launchd agent |

On Windows, Now playing shows the apps that appear in Windows' own media controls (the panel above the volume in Quick Settings): Spotify, browsers, Media Player and most streaming apps. A player that does not report itself there, such as VLC 3, stays blank on the phone. While no other app is in those controls, play / pause sends the keyboard's media key, which such a player may act on (VLC does when its media keys are global hotkeys or it is in front), but the phone cannot tell whether it plays.

On macOS, `nowplaying-cli` supplies system media metadata and cover art. The agent labels matching Safari or Chrome tabs as YouTube or YouTube Music. The progress bar advances from the last position supplied by macOS; Safari may report an outdated starting position. Volume follows the selected system output. When macOS does not expose its volume, an optional `m1ddc` installation can read it from a compatible display; display volume is read only in PhoneBox. Unsupported outputs show `n/a`. Apple silicon GPU usage comes from IORegistry when macOS exposes it; temperature, power and VRAM figures may remain unavailable.

</details>

Optional extras that light up more widgets: the [GitHub CLI](https://cli.github.com) (`gh auth login`), Claude Code,
Codex, and [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) with its web server
running for CPU temperature and, on AMD and Intel graphics cards, the hot spot, memory temperature, board power in
watts, fan speed and clocks. Without it Windows still reports any GPU's load, VRAM, temperature and power share; NVIDIA
cards get the full set from `nvidia-smi`. A PC with two GPUs (a graphics card and the processor's integrated one) shows
both: the card is the main GPU, the GPU details widget can show either.

#### LibreHardwareMonitor on Windows

PhoneBox reads LibreHardwareMonitor's sensor list from `http://127.0.0.1:8085/data.json` by default.

1. Download the latest release from
   [LibreHardwareMonitor's releases](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases), unzip it
   to a folder of its own and start `LibreHardwareMonitor.exe` **as administrator** (CPU temperatures and several GPU
   sensors need it).
2. In its menu choose **Options › Remote Web Server › Run**. If you use a different port, set the same port in
   the **LibreHardwareMonitor** section below the preview in PhoneBox Settings › Pages, then click **Save**.
3. Check `http://127.0.0.1:<port>/data.json` in a browser on the PC. PhoneBox shows the CPU temperature, package power
   and extra GPU values as your hardware reports them. If the previous port was unavailable, the next reading can take
   up to 30 seconds.
4. To keep it running: **Options › Start Minimized**, **Minimize To Tray** and **Run On Windows Startup**.

The web server answers on every network the PC is on. If Windows Firewall asks, PhoneBox only needs it on this
computer, so you can leave the network access unchecked. Menu names are those of recent LibreHardwareMonitor versions
and may differ slightly in older ones.

## Wi-Fi & Tailscale

Wi-Fi is off by default. To turn it on:

1. Open the settings page on the PC: click the tray icon, or go to `http://127.0.0.1:8765` in a browser.
2. In **General**, scroll down to **Wi-Fi & Tailscale** and turn on **Let the phone connect without the USB cable**.
3. Click **Save**, then **Restart to apply** at the top of the page. The line next to **Pair a phone over Wi-Fi**
   says "Listening on port 8766" once it works.
4. Allow PhoneBox through the firewall when Windows asks.

Then pair the phone in one of two ways:

- **Without a cable:** click **Pair a phone over Wi-Fi**. The window shows a QR code that downloads the app and one
  that pairs. Scan the second with the phone's camera, open the link and tap **Open in PhoneBox**; the phone asks you
  to confirm a code that the window also shows. No developer options, USB debugging or adb needed. The QR code holds
  the PC's addresses, its certificate fingerprint and a one-time code (5 minutes, single use), never the key itself.
  A phone paired this way only uses Wi-Fi: plugging it into the PC does not wake it or open the panel, there is no
  USB fallback when Wi-Fi drops, and app updates are installed from the release page instead of over USB.
- **Over USB:** plug the phone in once.

Either way the phone gets the PC's addresses, a secret key and the fingerprint of the agent's self-signed TLS
certificate, and trusts only that certificate. With USB, when the cable is unplugged it switches to
`https://<pc>:8766` automatically and back to USB when it is plugged in again; the status line shows which one it
uses.

- The phone and the PC need to be on the same network. If they are not (separate Wi-Fi networks, another
  building…), install [Tailscale](https://tailscale.com) on both: the agent offers its Tailscale address too.
- Windows Firewall asks once when Wi-Fi is turned on; allow it for the network types you use.
- The screen stays on, so the phone still needs a charger — just not necessarily the PC.

With several computers (each paired once over USB), the page overview on the phone lists them and switches between
them over Wi-Fi.

## Updates

The agent checks the latest GitHub release every 6 hours (or **Settings › General › Updates › Check now**) and, with
automatic updates on, installs the new APK on the phone with `adb install -r` while it is plugged in (no prompt on the
phone; over Wi-Fi the panel shows "plug in USB to update") and replaces its own files, then restarts. Android only
accepts an update signed with the same key as the installed app; the agent never uninstalls to force one.
When GitHub turns away anonymous requests (it answers 503 / 504 now and then), the agent asks again with the
[GitHub CLI](https://cli.github.com)'s token if `gh` is signed in; the token only goes to `api.github.com`.
A Mac agent running from `PhoneBox.app` (the `.dmg` of a release) updates the same way, by replacing the whole app: it
downloads the release's `PhoneBox-<version>-<rid>.dmg`, checks it against the release checksums and the new app's signature, swaps it
in and restarts. The app must sit in a folder you can write to, such as `~/Applications`.
See the [changelog](../CHANGELOG.md).

## FAQ & troubleshooting

<details><summary><b>The panel says "PC not connected".</b></summary>

Is the agent running (tray icon)? Run `adb devices`: the phone must be listed as `device`, not `unauthorized` —
accept the USB debugging prompt on the phone. If several devices are connected, pick the phone under
**Settings › General › Phone connection**. Over Wi-Fi, check that Wi-Fi is enabled on the agent, pair with the
QR code or over USB, and allow the agent through the firewall for your private network.

If `adb devices` itself doesn't answer, the adb server is hung. The agent tries to restart a local adb server that has
been hung for a minute, which also ends other tools' adb sessions (Android Studio, scrcpy). It doesn't while the phone
app is being installed, or when `ADB_SERVER_SOCKET` or `ANDROID_ADB_SERVER_ADDRESS` points adb at a server on another
machine, and it tries at most once every 5 minutes. It ends the server's process, found with `netstat` on Windows and
`lsof` (or `ss` on Linux) elsewhere. When `agent.log` says the process couldn't be found and it is "only asking it to
stop", the agent could only send `adb kill-server`, which a hung server may ignore: the restart may not have happened.

To recover at once from a local adb server, end `adb.exe` in Task Manager (`pkill adb` on Linux and macOS):
`adb kill-server` asks the server itself, so a hung one may not answer it. With `ADB_SERVER_SOCKET` or
`ANDROID_ADB_SERVER_ADDRESS` pointing at another machine, restart the adb server on that machine instead.
</details>

<details><summary><b>The phone keeps dropping off USB.</b></summary>

The panel carries on over Wi-Fi when Wi-Fi is on, and **Settings › General › Phone connection** says what the phone
itself sees: if it is charging but has no USB data connection, the cable or the port only carries power — try another
cable, or a port straight on the computer instead of a hub or a front-panel port. Updates of the phone app need USB.

On some Samsung phones the USB data link restarts each time charging stops, starts or changes speed, and adb sees
the phone go away for a few seconds. **Protect battery** (charging stops at 85 % and starts again below it) does
this every few minutes, and fast charging can too. Turning Protect battery off made the drops rare on a Galaxy A31
(Android 11), but not gone ([#410](https://github.com/ersinkoc/phonebox/issues/410)); there, Wi-Fi with the phone on
a charger of its own works without drops.
</details>

<details><summary><b>Some widgets show "—" or "No data".</b></summary>

GPU values come from Windows itself (the temperature, as in Task Manager, needs Windows 10 2004 or later and a WDDM 2.4 driver), `nvidia-smi` for NVIDIA cards, `amdgpu` sysfs on Linux, an Intel card's DRM usage counters on Linux, or IORegistry on Apple Silicon. An Intel card's load on Linux needs a kernel whose driver exposes busy counters in fdinfo (`i915` from kernel 5.19, `xe`); a card whose programs never report them shows no load. A program that starts using the GPU is picked up within about 30 seconds and counts from the reading after that, and programs of other users are counted only when Linux lets the agent read them (normally as root or with `CAP_SYS_PTRACE`). The GPU hot spot and an AMD or Intel card's watts, fan and clocks need LibreHardwareMonitor. Windows CPU temperature needs LibreHardwareMonitor's web server,
GitHub needs `gh auth login`, quotas and sessions need Claude Code / Codex to have been used on this PC.
</details>

<details><summary><b>On Android 5 or 6, the weather says "Weather unavailable".</b></summary>

The weather and air quality come straight from Open-Meteo over HTTPS, and Android before 7.1.1 doesn't trust the
certificate authority its certificate chains to (Let's Encrypt's ISRG Root X1). Download `isrgrootx1.pem` from
[letsencrypt.org/certificates](https://letsencrypt.org/certificates/), rename it to `isrgrootx1.crt`, and install it
under **Settings › Security › Install from device storage**. The panel itself talks to the computer and is not
affected. Thanks to [phongnguyen2277](https://github.com/phongnguyen2277) for finding this (#184).
</details>

<details><summary><b>On an x86 tablet the panel asks for a newer WebView, and the Play Store has none.</b></summary>

Some older tablets have an Intel (x86) processor, such as the Galaxy Tab 3 10.1 (GT-P5200). On the one we tried, the
Play Store offered no WebView or Chrome newer than 101, below the 105 the panel needs to scale widgets. On a rooted
device running LineageOS 14.1 (Android 7), LineageOS's own x86 WebView can replace the system one:

1. Take `webview.apk` from the commit with
   [WebView 119.0.6045.134](https://github.com/LineageOS/android_external_chromium-webview_prebuilt_x86/tree/91c9fde9772f760daebb73402e5f3dce9808f427)
   (the APK is stored with Git LFS). 119 is the last Chromium for Android 7: from 120 on, WebView needs Android 8.0
   (API 26), and the newer builds on that repository's default branch will not run on Android 7.
2. Keep a copy of `/system/app/webview/webview.apk`, put the new APK in its place and restart the tablet.
   `adb install -r` cannot do this, because the new APK is signed with a different key than the system one.
3. On Android 7 to 9, Chrome can be the active WebView, and then replacing the system one changes nothing. Open
   **Developer options › WebView implementation**, pick *Android System WebView* (or disable Chrome), and check that
   the version is 105 or later under **Settings › Apps › Android System WebView**; the panel's update notice also
   goes away.

This worked on a GT-P5200 with LineageOS 14.1 (Android 7.1.2), where Chrome 101 is installed next to it. Without root
there is no newer WebView for these devices.
</details>

<details><summary><b>I want the volume keys to change the phone's volume.</b></summary>

Turn off **Settings › General › Display › The phone's volume keys switch pages**.
</details>

<details><summary><b>The update was not installed on the phone.</b></summary>

App updates need USB (Android installs them through adb without a prompt). They also need to be signed with the same
key as the installed app — if you built the app yourself, keep using your own release key.
</details>

<details><summary><b>Can I use it without root?</b></summary>

Yes. PhoneBox needs no root on the phone and no admin rights on the PC.
</details>
