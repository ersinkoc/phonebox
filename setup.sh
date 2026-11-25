#!/usr/bin/env bash
# PhoneBox setup for Linux and macOS: installs the latest release, everything it needs, and the phone app.
#
#   curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash -s -- --no-phone
#
# Run it as yourself, not with sudo: it asks for sudo only to install packages, and asks first.
# Running it again updates PhoneBox or repairs the installation; settings are kept.
#
# Options:
#   --yes               answer every question with its default (unattended)
#   --no-phone          do not install the app on a phone over USB (for a Wi-Fi-only phone)
#   --serial <id>       the phone to use when several are connected (see "adb devices")
#   --no-autostart      do not start PhoneBox at login
#   --no-start          install only; do not start the agent
#   --no-extras         do not offer the optional helpers (playerctl, xprintidle, nowplaying-cli ...)
#   --version <x.y.z>   install this release instead of the latest
#   --dry-run           download and check everything, show what would happen, change nothing
set -euo pipefail

repo="ersinkoc/phonebox"
package="net.oxog.phonebox.deskpanel"
settings_url="http://127.0.0.1:8765"

yes=0; phone=1; serial=""; autostart=1; start=1; extras=1; version=""; dry=0
tmp=""

if [ -t 1 ]; then c_step=$'\033[36m'; c_ok=$'\033[32m'; c_warn=$'\033[33m'; c_err=$'\033[31m'; c_off=$'\033[0m'
else c_step=""; c_ok=""; c_warn=""; c_err=""; c_off=""; fi
step() { printf '\n%s==> %s%s\n' "$c_step" "$1" "$c_off"; }
info() { printf '    %s\n' "$1"; }
ok() { printf '    %sOK  %s%s\n' "$c_ok" "$1" "$c_off"; }
caution() { printf '    %s!   %s%s\n' "$c_warn" "$1" "$c_off"; }
die() {
  printf '\n%sSetup stopped: %s%s\n' "$c_err" "$1" "$c_off" >&2
  printf '%sHelp: https://github.com/%s#quick-start  -  issues: https://github.com/%s/issues%s\n' "$c_err" "$repo" "$repo" "$c_off" >&2
  exit 1
}
have() { command -v "$1" >/dev/null 2>&1; }

# Questions go to the terminal even when the script itself arrives through a pipe (curl ... | bash).
tty_ok=0
if (: </dev/tty) 2>/dev/null; then tty_ok=1; fi

ask() { # ask "question" y|n -> 0 for yes
  local question="$1" default="$2" hint answer
  if [ "$default" = y ]; then hint="[Y/n]"; else hint="[y/N]"; fi
  if [ "$yes" = 1 ] || [ "$tty_ok" = 0 ]; then
    if [ "$default" = y ]; then info "$question $hint -> yes (default)"; return 0; fi
    info "$question $hint -> no (default)"; return 1
  fi
  while :; do
    printf '    %s %s ' "$question" "$hint" >/dev/tty
    IFS= read -r answer </dev/tty || answer=""
    case "$answer" in
      "") if [ "$default" = y ]; then return 0; else return 1; fi ;;
      [Yy]|[Yy][Ee][Ss]) return 0 ;;
      [Nn]|[Nn][Oo]) return 1 ;;
    esac
  done
}

read_line() { # read_line "prompt" -> the answer on stdout (empty without a terminal)
  local answer=""
  if [ "$yes" = 0 ] && [ "$tty_ok" = 1 ]; then
    printf '    %s ' "$1" >/dev/tty
    IFS= read -r answer </dev/tty || answer=""
  fi
  printf '%s' "$answer"
}

download() { # download url file
  if have curl; then curl -fsSL --retry 3 --proto '=https' --proto-redir '=https' -A phonebox-setup -o "$2" "$1"
  else wget -q --https-only -U phonebox-setup -O "$2" "$1"; fi
}

download_text() { # download_text url -> stdout
  if have curl; then curl -fsSL --retry 2 --proto '=https' -A phonebox-setup "$1"
  else wget -q --https-only -U phonebox-setup -O - "$1"; fi
}

local_up() { # is the agent answering on this computer?
  if have curl; then curl -fsS --max-time 2 "$settings_url/api/info" >/dev/null 2>&1
  else wget -q -T 2 -O /dev/null "$settings_url/api/info" 2>/dev/null; fi
}

sha256_of() {
  if have sha256sum; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

check_sum() { # check_sum file sums-file
  local name expected actual
  name="$(basename "$1")"
  expected="$(awk -v n="$name" '{ f = $2; sub(/^\*/, "", f); if (f == n) { print tolower($1); exit } }' "$2")"
  if [ -z "$expected" ]; then
    printf '%s\n' "$name is not listed in the release checksums." >&2
    return 1
  fi
  actual="$(sha256_of "$1" | tr 'A-F' 'a-f')"
  if [ "$actual" != "$expected" ]; then
    printf '%s\n' "$name does not match the release checksum (expected $expected, got $actual)." >&2
    return 1
  fi
  ok "$name matches the release checksum"
}

version_ge() { # version_ge 2.93.1 2.93.0 -> 0 when the first is at least the second
  local IFS=. i x y
  local -a a b
  read -r -a a <<<"$1"
  read -r -a b <<<"$2"
  for i in 0 1 2; do
    x="${a[i]:-0}"; y="${b[i]:-0}"
    if [ "$x" -gt "$y" ]; then return 0; fi
    if [ "$x" -lt "$y" ]; then return 1; fi
  done
  return 0
}

check_provenance() {
  local out ghv
  if ! have gh || ! gh auth status >/dev/null 2>&1; then
    info "Build provenance not checked (needs the GitHub CLI, signed in): gh attestation verify $(basename "$1") --repo $repo"
    return
  fi
  # gh 2.92.0 and earlier send the GitHub token to the TUF mirrors during verification (GHSA-8xvp-7hj6-mcj9)
  ghv="$(gh --version 2>/dev/null | sed -n 's/^gh version \([0-9]*\.[0-9]*\.[0-9]*\).*/\1/p' | head -n1)" || ghv=""
  if [ -z "$ghv" ] || ! version_ge "$ghv" 2.93.0; then
    caution "Build provenance not checked: GitHub CLI ${ghv:-(unknown version)} has a security problem in \"gh attestation\""
    caution "(it sends your GitHub token to another server). Update it to 2.93.0 or later."
    return
  fi
  if out="$(gh attestation verify "$1" --repo "$repo" 2>&1)"; then
    ok "$(basename "$1") was built by this repository's release workflow (GitHub attestation)"
  elif printf '%s' "$out" | grep -qi 'unknown command'; then
    info 'Build provenance not checked: this GitHub CLI is too old for "gh attestation".'
  else
    die "the build provenance of $(basename "$1") could not be verified; nothing was installed. $out"
  fi
}

latest_version() {
  local v=""
  v="$(download_text "https://api.github.com/repos/$repo/releases/latest" 2>/dev/null \
    | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1)" || v=""
  # the API allows 60 requests an hour per address; the release page redirects to the newest tag as well
  if [ -z "$v" ] && have curl; then
    v="$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$repo/releases/latest" 2>/dev/null | sed -n 's#.*/tag/##p')" || v=""
  fi
  printf '%s' "$v"
}

designated() { # designated binary -> its designated requirement (what macOS ties permissions to), or nothing
  codesign -d -r- "$1" 2>&1 | sed -n 's/^designated => //p'
}
unzip_to() { # unzip_to zip dir
  mkdir -p "$2"
  if have unzip; then unzip -q -o "$1" -d "$2"
  elif have bsdtar; then bsdtar -xf "$1" -C "$2"
  elif have python3; then python3 -I -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$1" "$2"
  else die "cannot unpack the download: install unzip (for example: sudo apt install unzip) and run setup again."; fi
}

# ---- packages ---------------------------------------------------------------------------------------------------
pm=""
apt_updated=0
detect_pm() {
  if [ "$os" = osx ]; then
    if have brew; then pm=brew; fi
    return
  fi
  for p in apt-get dnf pacman zypper; do
    if have "$p"; then pm="$p"; return; fi
  done
}

pkg_install() { # pkg_install package... (one at a time, so a missing one does not stop the others)
  local p failed=0
  for p in "$@"; do
    case "$pm" in
      brew) brew install "$p" >/dev/null || failed=1 ;;
      apt-get)
        if [ "$apt_updated" = 0 ]; then sudo apt-get update -qq || true; apt_updated=1; fi
        sudo apt-get install -y -qq "$p" >/dev/null || failed=1 ;;
      dnf) sudo dnf install -y -q "$p" >/dev/null || failed=1 ;;
      pacman) sudo pacman -S --needed --noconfirm "$p" >/dev/null || failed=1 ;;
      zypper) sudo zypper --non-interactive --quiet install "$p" >/dev/null || failed=1 ;;
      *) failed=1 ;;
    esac
  done
  return "$failed"
}

sudo_note() {
  if [ "$pm" != brew ] && [ "$(id -u)" != 0 ]; then
    have sudo || { caution "sudo is not available, so packages cannot be installed."; return 1; }
    info "This uses sudo ($pm); you may be asked for your password."
  fi
  return 0
}

find_adb() {
  local c
  if have adb; then command -v adb; return; fi
  for c in "$HOME/Android/Sdk/platform-tools/adb" "$HOME/Library/Android/sdk/platform-tools/adb" \
    /opt/homebrew/bin/adb /usr/local/bin/adb /usr/bin/adb /usr/lib/android-sdk/platform-tools/adb; do
    if [ -x "$c" ]; then printf '%s' "$c"; return; fi
  done
}

install_adb() {
  info 'adb (Android SDK Platform-Tools) is Google software under the Android SDK terms:'
  info 'https://developer.android.com/studio/releases/platform-tools'
  case "$pm" in
    brew) brew install --cask android-platform-tools >/dev/null || true ;;
    apt-get) if sudo_note; then pkg_install adb android-sdk-platform-tools-common || true; fi ;;
    dnf|zypper) if sudo_note; then pkg_install android-tools || true; fi ;;
    pacman) if sudo_note; then pkg_install android-tools android-udev || true; fi ;;
  esac
  if [ -n "$(find_adb)" ]; then return; fi
  # no package manager (or it failed): Google's download, in a folder PhoneBox looks in
  local url dest
  if [ "$os" = osx ]; then url="https://dl.google.com/android/repository/platform-tools-latest-darwin.zip"; dest="$HOME/Library/Android/sdk"
  elif [ "$arch" = x64 ]; then url="https://dl.google.com/android/repository/platform-tools-latest-linux.zip"; dest="$HOME/Android/Sdk"
  else caution "Google offers adb for x64 Linux only; install your distribution's adb / android-tools package."; return; fi
  info "Downloading Google's platform tools to $dest/platform-tools ..."
  if download "$url" "$tmp/platform-tools.zip"; then unzip_to "$tmp/platform-tools.zip" "$dest"; fi
  chmod +x "$dest/platform-tools/adb" 2>/dev/null || true
}

offer_extras() {
  local missing="" reasons=""
  if [ "$os" = osx ]; then
    if ! have nowplaying-cli; then missing="nowplaying-cli"; reasons="nowplaying-cli (Now Playing for any app, with cover art)"; fi
    if [ -n "$missing" ] && [ "$pm" != brew ]; then
      info "Optional: $reasons. Install Homebrew (https://brew.sh), then: brew install nowplaying-cli"
      return
    fi
  else
    if ! have playerctl; then missing="playerctl"; reasons="playerctl (Now Playing and its controls)"; fi
    if ! have wpctl && ! have pactl; then
      case "$pm" in pacman) missing="$missing libpulse" ;; *) missing="$missing pulseaudio-utils" ;; esac
      reasons="$reasons, pactl (volume)"
    fi
    if ! have xprintidle && [ -n "${DISPLAY:-}" ]; then missing="$missing xprintidle"; reasons="$reasons, xprintidle (idle time on X11)"; fi
    if [ -n "$missing" ] && [ -z "$pm" ]; then info "Optional, from your package manager: ${reasons#, }"; return; fi
  fi
  [ -n "$missing" ] || { ok "All optional helpers are installed"; return; }
  info "Optional helpers for more widgets: ${reasons#, }"
  if [ "$dry" = 1 ]; then info "Would offer to install: $missing"; return; fi
  if ask "Install them now?" y; then
    # shellcheck disable=SC2086 # one package name per word
    if sudo_note && pkg_install $missing; then ok "Installed: $missing"
    else caution "Some could not be installed (not every distribution packages all of them); PhoneBox works without them."; fi
  fi
}

# ---- agent ------------------------------------------------------------------------------------------------------
unit="$HOME/.config/systemd/user/phonebox.service"   # where scripts/uninstall.sh looks
plist="$HOME/Library/LaunchAgents/net.oxog.phonebox.agent.plist"
app_login_plist="$HOME/Library/LaunchAgents/net.oxog.phonebox.app.plist"   # written by the menu bar item in PhoneBox.app

systemd_user() { have systemctl && systemctl --user show-environment >/dev/null 2>&1; }

remove_autostart() {
  if [ "$os" = linux ] && [ -f "$unit" ]; then
    systemctl --user disable phonebox.service >/dev/null 2>&1 || true
    rm -f "$unit"
    info 'Removed the login service; PhoneBox no longer starts at login.'
  fi
  if [ "$os" = osx ] && [ -f "$plist" ]; then
    rm -f "$plist"
    info 'Removed the login item; PhoneBox no longer starts at login.'
  fi
  # PhoneBox.app's own "Start at Login" (menu bar): without autostart it must not start PhoneBox at login either
  if [ "$os" = osx ] && [ -f "$app_login_plist" ]; then
    rm -f "$app_login_plist"
    info 'Removed the app'"'"'s "Start at Login"; PhoneBox no longer starts at login.'
  fi
}

# a path or an assignment as one quoted systemd word (the install path can contain spaces through XDG_DATA_HOME);
# "$" is left alone: systemd does not expand it in the executable path, and paths with "$" are refused below
systemd_quote() {
  printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/%/%%/g')"
}

xml_escape() { printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

stop_agent() {
  if [ "$os" = linux ] && systemd_user; then systemctl --user stop phonebox.service 2>/dev/null || true; fi
  if [ "$os" = osx ] && [ -f "$plist" ]; then launchctl unload "$plist" 2>/dev/null || true; fi
  pkill -x PhoneBoxAgent 2>/dev/null || true
  local i=0
  while pgrep -x PhoneBoxAgent >/dev/null 2>&1 && [ "$i" -lt 40 ]; do sleep 0.25; i=$((i + 1)); done
}

write_autostart() {
  if [ "$os" = linux ]; then
    if ! systemd_user; then
      caution "No systemd user session here, so PhoneBox cannot start at login automatically."
      caution "Add $app/PhoneBoxAgent to your desktop's startup applications instead."
      return 1
    fi
    local unsafe=0
    case "$app$HOME${XDG_DATA_HOME:-}${XDG_CONFIG_HOME:-}" in *'$'*|*'
'*) unsafe=1 ;; esac
    # systemd also refuses an executable path with a quote, an apostrophe or a backslash ("special characters"),
    # e.g. an AD home such as /home/o'brien@corp.example: the unit would be enabled but never start
    case "$app" in *'"'*|*"'"*|*[\\]*) unsafe=1 ;; esac
    if [ "$unsafe" = 1 ]; then
      caution "The install or settings path contains \$, a quote, a backslash or a line break, which a systemd service cannot be given safely;"
      caution "add $app/PhoneBoxAgent to your desktop's startup applications instead."
      remove_autostart   # an older unit would otherwise start the previous install at login and in start_agent
      return 1
    fi
    # the service must see the same XDG folders as this shell, or the agent would use other settings and install folders
    local env_lines="" v
    for v in XDG_DATA_HOME XDG_CONFIG_HOME; do
      if [ -n "${!v:-}" ]; then env_lines="${env_lines}Environment=$(systemd_quote "$v=${!v}")
"; fi
    done
    mkdir -p "$(dirname "$unit")"
    cat > "$unit" <<EOF
[Unit]
Description=PhoneBox agent (desk panel for an Android phone)
After=graphical-session.target

[Service]
ExecStart=$(systemd_quote "$app/PhoneBoxAgent")
Restart=on-failure
RestartSec=5
${env_lines}
[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable phonebox.service >/dev/null 2>&1
    ok "systemd user service: $unit"
  else
    mkdir -p "$(dirname "$plist")"
    rm -f "$app_login_plist"   # PhoneBox.app's "Start at Login": this agent replaces it, two would start two instances
    cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>net.oxog.phonebox.agent</string>
  <key>ProgramArguments</key><array><string>$(xml_escape "$app/PhoneBoxAgent")</string></array>
  <key>EnvironmentVariables</key><dict>
    <key>PATH</key><string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
</dict></plist>
EOF
    ok "launchd agent: $plist"
  fi
}

start_agent() {
  if [ "$os" = linux ] && [ -f "$unit" ] && systemd_user; then systemctl --user restart phonebox.service && return; fi
  if [ "$os" = osx ] && [ -f "$plist" ]; then launchctl load "$plist" 2>/dev/null && return; fi
  nohup "$app/PhoneBoxAgent" >/dev/null 2>&1 &
  disown 2>/dev/null || true
}

# ---- phone ------------------------------------------------------------------------------------------------------
phone_help() {
  info 'On the phone (Android 5.0 or later):'
  info '  1. Settings > About phone: tap "Build number" seven times to turn on Developer options.'
  info '  2. Settings > System > Developer options: turn on "USB debugging".'
  info '     Xiaomi / Redmi / POCO: also turn on "Install via USB" and "USB debugging (Security settings)".'
  info '  3. Connect it with a data cable (some cables only charge) and unlock the screen.'
  info '  4. Answer "Allow USB debugging?" with Allow; tick "Always allow from this computer".'
}

linux_usb_help() {
  caution 'Linux does not let adb open the phone yet ("no permissions"). Usually one of these fixes it:'
  caution '  - the udev rules: Debian/Ubuntu android-sdk-platform-tools-common, Arch android-udev, Fedora android-tools;'
  caution "  - the plugdev group: sudo usermod -aG plugdev $USER, then log out and in again;"
  caution '  - unplug the phone and plug it in again, then: adb kill-server'
  caution '  Guide: https://developer.android.com/studio/run/device#setting-up'
}

read_usb_install_policy() {
  local config
  "$app/PhoneBoxAgent" --print-install-dir --write-ignored-usb "$1" >/dev/null 2>&1 || return 1
  [ -f "$1" ] && return 0
  # Legacy agents cannot validate an existing configuration.
  if [ "$os" = osx ]; then config="$HOME/Library/Application Support/PhoneBox/config.json"
  else config="${XDG_CONFIG_HOME:-$HOME/.config}/PhoneBox/config.json"; fi
  if [ -e "$config" ] || [ -L "$config" ]; then return 1; fi
  : > "$1"
}

select_phone() { # prints the serial of the one authorised phone, or nothing to skip
  local ignore_list="$1" wanted="$serial" devices available ready count answer
  "$adb" start-server >/dev/null 2>&1 || true
  while :; do
    if [ -n "$wanted" ] && grep -Fxq -- "$wanted" "$ignore_list"; then
      caution "$wanted is in PhoneBox's ignored USB device list. Skipping the phone app." >&2
      return 0
    fi
    devices="$("$adb" devices 2>/dev/null | awk -F'\t' 'NF >= 2 { print $1 "\t" $2 }')"
    available="$(printf '%s\n' "$devices" | awk -F'\t' 'FILENAME == ARGV[1] { ignored[$0] = 1; next }
      NF >= 2 && !($1 in ignored)' "$ignore_list" -)"
    if [ -n "$devices" ] && [ -z "$available" ]; then
      caution 'All attached devices are ignored. Skipping the phone app.' >&2
      return 0
    fi
    devices="$available"
    if [ -n "$wanted" ]; then devices="$(printf '%s\n' "$devices" | awk -F'\t' -v s="$wanted" '$1 == s')"; fi
    # only phones on a cable are picked automatically, not emulators or wireless-debugging connections
    ready="$(printf '%s\n' "$devices" | awk -F'\t' -v w="$wanted" '$2 == "device" && (w != "" || $1 !~ /^emulator-|:|\._tcp/) { print $1 }')"
    count="$(printf '%s' "$ready" | grep -c . || true)"
    if [ "$count" = 1 ]; then printf '%s' "$ready"; return; fi
    if [ "$count" -gt 1 ]; then
      caution "Several phones are connected: $(printf '%s' "$ready" | tr '\n' ' ')" >&2
      if [ "$yes" = 1 ] || [ "$tty_ok" = 0 ]; then caution 'Run setup again with --serial <id> to pick one; skipping the phone app.' >&2; return; fi
      wanted="$(read_line 'Type the serial of the phone to use (empty to skip):')"
      [ -n "$wanted" ] || return 0   # skipping is not an error: main runs under set -e
      continue
    fi
    if [ -z "$devices" ]; then
      if [ -n "$wanted" ]; then caution "No phone with the serial $wanted is connected." >&2; else caution 'No phone is connected over USB yet.' >&2; fi
      phone_help >&2
    else
      printf '%s\n' "$devices" | while IFS="$(printf '\t')" read -r s state; do
        case "$state" in
          unauthorized) caution "$s: waiting for you to allow USB debugging on the phone (unlock it and tap Allow)." >&2 ;;
          offline) caution "$s: offline. Unplug it and plug it in again." >&2 ;;
          device) caution "$s: an emulator or a wireless connection, not used unless you pass --serial $s." >&2 ;;
          no\ permissions*) linux_usb_help >&2 ;;
          *) caution "$s: $state" >&2 ;;
        esac
      done
    fi
    if [ "$yes" = 1 ] || [ "$tty_ok" = 0 ]; then caution 'Skipping the phone app (no phone ready).' >&2; return; fi
    answer="$(read_line 'Press Enter to check again, or type S to skip the phone:')"
    case "$answer" in [Ss]|[Ss][Kk][Ii][Pp]) return ;; esac
    "$adb" reconnect offline >/dev/null 2>&1 || true
  done
}

install_phone_app() { # install_phone_app apk serial -> 0 when installed
  local apk="$1" device="$2" level out
  level="$("$adb" -s "$device" shell getprop ro.build.version.sdk 2>/dev/null | tr -d '\r[:space:]')"
  case "$level" in ''|*[!0-9]*) level=0 ;; esac
  if [ "$level" -gt 0 ] && [ "$level" -lt 21 ]; then
    caution "This phone runs Android API $level; PhoneBox needs Android 5.0 (API 21) or later. The app was not installed."
    return 1
  fi
  info "Installing $(basename "$apk") on $device (the phone may ask you to confirm) ..."
  out="$("$adb" -s "$device" install -r "$apk" 2>&1)" || true
  case "$out" in
    *INSTALL_FAILED_UPDATE_INCOMPATIBLE*)
      caution 'The phone has a PhoneBox app signed with a different key (for example one you built yourself).'
      caution 'It was left as it is, with its data. To switch to the release app, uninstall PhoneBox on the phone'
      caution '(this deletes its settings) and run setup again.'
      return 1 ;;
    *INSTALL_FAILED_VERSION_DOWNGRADE*) ok 'The phone already has a newer PhoneBox app; it was kept.'; return 0 ;;
    *INSTALL_FAILED_USER_RESTRICTED*|*INSTALL_FAILED_ABORTED*)
      caution 'The phone refused the installation. Confirm it on the phone; Xiaomi / Redmi / POCO phones also need'
      caution '"Install via USB" in Developer options. Then run setup again.'
      return 1 ;;
    *Success*) ;;
    *) caution "The app could not be installed: $(printf '%s' "$out" | tr '\n' ' ')"; return 1 ;;
  esac
  ok 'PhoneBox app installed on the phone'
  "$adb" -s "$device" shell am start -n "$package/.MainActivity" >/dev/null 2>&1 || true
  return 0
}

# ---- main -------------------------------------------------------------------------------------------------------
main() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --yes|-y) yes=1 ;;
      --no-phone) phone=0 ;;
      --serial) [[ $# -ge 2 && -n $2 && $2 != -* ]] || die "--serial needs a value"; serial="$2"; shift ;;
      --no-autostart) autostart=0 ;;
      --no-start) start=0 ;;
      --no-extras) extras=0 ;;
      --version) [[ $# -ge 2 && -n $2 && $2 != -* ]] || die "--version needs a value"; version="$2"; shift ;;
      --dry-run) dry=1 ;;
      -h|--help) sed -n '2,19p' "$0" 2>/dev/null || echo "See https://github.com/$repo#quick-start"; exit 0 ;;
      *) die "unknown option: $1 (see --help)" ;;
    esac
    shift
  done

  printf '\n%sPhoneBox setup%s\n' "$c_step" "$c_off"
  if [ "$dry" = 1 ]; then printf '%sDry run: downloads and checks everything, changes nothing.%s\n' "$c_warn" "$c_off"; fi

  step 'Checking this computer'
  [ "$(id -u)" != 0 ] || die "do not run setup as root or with sudo. PhoneBox runs as you (its settings, autostart and phone pairing belong to your account); setup asks for sudo itself when it installs packages."
  case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *) die "this script is for Linux and macOS; on Windows run setup.ps1 (see https://github.com/$repo#quick-start)." ;;
  esac
  if [ "$os" = osx ]; then
    # a shell under Rosetta reports x86_64 on Apple silicon; the native build is the right one
    if [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ]; then arch=arm64; else arch=x64; fi
  else
    case "$(uname -m)" in
      x86_64|amd64) arch=x64 ;;
      aarch64|arm64) arch=arm64 ;;
      *) die "there is no PhoneBox build for this CPU ($(uname -m)); x64 and arm64 are supported." ;;
    esac
    if [ -f /etc/alpine-release ] || case "$(ldd --version 2>&1 || true)" in *musl*) true ;; *) false ;; esac; then
      die "PhoneBox's Linux builds need glibc; musl systems such as Alpine are not supported yet."
    fi
  fi
  have curl || have wget || die "curl or wget is needed to download PhoneBox."
  have sha256sum || have shasum || die "sha256sum (coreutils) or shasum is needed to check the download."
  rid="$os-$arch"
  detect_pm
  ok "$( [ "$os" = osx ] && echo "macOS $(sw_vers -productVersion 2>/dev/null)" || echo "Linux $(uname -r)"), $arch${pm:+, package manager: $pm}"

  step 'Finding the release'
  local tag ver base
  tag="${version:-$(latest_version)}"
  [ -n "$tag" ] || die "could not find the latest release on GitHub. Check the internet connection, or pass --version 0.9.8."
  ver="${tag#v}"
  printf '%s' "$ver" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || die "unexpected version '$tag'."
  base="https://github.com/$repo/releases/download/v$ver"
  ok "PhoneBox $ver ($rid)"

  tmp="$(mktemp -d "${TMPDIR:-/tmp}/phonebox-setup.XXXXXX")"
  trap 'rm -rf "$tmp"' EXIT

  step "Downloading PhoneBox $ver"
  local zip_name="PhoneBoxAgent-$ver-$rid.zip" sums
  sums="$tmp/SHA256SUMS-$ver.txt"
  download "$base/SHA256SUMS-$ver.txt" "$sums" || die "release $ver was not found ($base)."
  info "$zip_name (about 45 MB) ..."
  download "$base/$zip_name" "$tmp/$zip_name" || die "the download failed: $base/$zip_name"
  check_sum "$tmp/$zip_name" "$sums" || die "the agent download could not be verified. Nothing was installed."
  check_provenance "$tmp/$zip_name"
  unzip_to "$tmp/$zip_name" "$tmp/agent"
  chmod +x "$tmp/agent/PhoneBoxAgent"
  if [ "$os" = osx ]; then
    xattr -dr com.apple.quarantine "$tmp/agent" 2>/dev/null || true
    # release binaries are signed (with PhoneBox's own certificate since 0.9.22, ad hoc before); Apple silicon refuses
    # to run a binary without any signature
    codesign --verify "$tmp/agent/PhoneBoxAgent" >/dev/null 2>&1 || codesign --force --sign - "$tmp/agent/PhoneBoxAgent" >/dev/null 2>&1 || true
  fi
  local out
  if ! out="$("$tmp/agent/PhoneBoxAgent" --print-install-dir 2>&1)"; then
    die "this system cannot run the PhoneBox $rid build: $out"
  fi
  app="$(printf '%s\n' "$out" | tail -n1)"
  case "$app" in /*/PhoneBox/app) ;; *) die "unexpected install folder from the agent: $app" ;; esac
  local installed=""
  if [ -x "$app/PhoneBoxAgent" ]; then installed="present"; fi
  if [ -f "$app/PhoneBoxAgent.dll" ]; then
    info "Found a source install (scripts/install.sh): it is replaced by the self-contained release; settings stay."
  elif [ -n "$installed" ]; then
    info "Found an existing install in $app: it is replaced; settings stay."
  fi
  if [ "$os" = osx ]; then
    info 'The agent is signed with its own certificate, not an Apple Developer ID, so macOS may ask about it the first'
    info 'time: allow it to control Music or Spotify (Now Playing) and, once Wi-Fi is on, to accept incoming connections.'
    # macOS ties the Accessibility permission to the signature's designated requirement: the same from release to
    # release since 0.9.22, but different from an older (ad hoc) build's, which changed with every build (#269)
    if [ -n "$installed" ] && [ "$(designated "$app/PhoneBoxAgent")" != "$(designated "$tmp/agent/PhoneBoxAgent")" ]; then
      info 'If you allowed it under Accessibility (shortcuts that press keys), allow it once more: macOS does not apply'
      info 'that to this build. In System Settings › Privacy & Security › Accessibility, remove PhoneBoxAgent with −'
      info 'and add it again. Later updates keep the permission.'
    fi
  fi

  step 'Checking adb (needed for the USB connection to the phone)'
  adb="$(find_adb)"
  if [ -n "$adb" ]; then ok "$adb"
  else
    caution 'adb is not installed. Without it PhoneBox still works over Wi-Fi, but cannot reach the phone over USB'
    caution '(wake it on plug-in, install and update the phone app, fall back to USB when Wi-Fi drops).'
    if [ "$dry" = 1 ]; then info 'Would offer to install it.'
    elif ask 'Install adb now?' y; then
      install_adb
      adb="$(find_adb)"
      if [ -n "$adb" ]; then ok "adb installed: $adb"; else caution 'adb could not be installed; PhoneBox will work over Wi-Fi only until it is.'; fi
    fi
  fi

  if [ "$extras" = 1 ]; then
    step 'Optional helpers'
    offer_extras
  fi

  if [ "$dry" = 1 ]; then
    step 'Dry run finished'
    info "Would stop the running agent and install PhoneBox $ver to $app (settings stay)."
    [ "$autostart" = 0 ] || info 'Would ask to start PhoneBox at login.'
    [ "$phone" = 0 ] || info 'Would offer to install the app on a phone connected over USB.'
    info 'Nothing was changed.'
    return
  fi

  step "Installing to $app"
  stop_agent
  mkdir -p "$(dirname "$app")"
  rm -rf "$app.new" "$app.old"
  cp -R "$tmp/agent" "$app.new"
  if [ -d "$app" ]; then mv "$app" "$app.old"; fi
  if ! mv "$app.new" "$app"; then
    if [ -d "$app.old" ]; then mv "$app.old" "$app"; fi
    die "installing failed; the previous version was restored."
  fi
  rm -rf "$app.old"
  ok "PhoneBox $ver installed"

  if [ "$autostart" = 1 ]; then
    step 'Start at login'
    if ask 'Start PhoneBox when you log in?' y; then write_autostart || true
    else remove_autostart; fi
  else
    remove_autostart
  fi

  local phone_done=0 device apk
  if [ "$phone" = 1 ]; then
    step 'Phone app'
    if [ -z "$adb" ]; then caution 'Skipped: adb is not installed.'
    elif ask 'Install the PhoneBox app on a phone connected over USB now?' y; then
      info 'USB debugging lets an allowed computer control the phone. Allow only your own computers; you can'
      info 'revoke them under Developer options > "Revoke USB debugging authorisations".'
      local ignore_list="$tmp/ignored-usb-devices"
      if read_usb_install_policy "$ignore_list"; then device="$(select_phone "$ignore_list")"
      else caution 'Phone app skipped: this agent could not read the USB ignore settings.'; device=""; fi
      if [ -n "$device" ]; then
        apk="$tmp/PhoneBox-$ver.apk"
        if ! download "$base/PhoneBox-$ver.apk" "$apk"; then
          caution "Phone app skipped: the download failed ($base/PhoneBox-$ver.apk)."
        elif ! check_sum "$apk" "$sums"; then
          caution 'Phone app skipped: the APK could not be verified.'
        elif install_phone_app "$apk" "$device"; then
          phone_done=1
        fi
      fi
    fi
  fi

  if [ "$start" = 0 ]; then
    step 'Done (not started)'
    info "Start it with $app/PhoneBoxAgent"
  else
    step 'Starting PhoneBox'
    start_agent
    local i=0 up=0
    while [ "$i" -lt 30 ]; do
      sleep 0.7
      if local_up; then up=1; break; fi
      i=$((i + 1))
    done
    if [ "$up" = 1 ]; then
      ok "PhoneBox is running: settings at $settings_url"
      if [ "$os" = osx ]; then open "$settings_url" >/dev/null 2>&1 || true
      elif [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] && have xdg-open; then (xdg-open "$settings_url" >/dev/null 2>&1 &) || true; fi
    else
      caution "The agent did not answer. Its log: $(dirname "$app")/agent.log"
    fi
  fi

  step 'Good to know'
  if [ "$phone_done" = 1 ]; then info 'Keep the phone plugged in: PhoneBox wakes it and opens the panel.'
  elif [ "$phone" = 1 ]; then info 'Phone app later: plug the phone in and use Settings > General > Install the phone app, or run setup again.'; fi
  info 'Wi-Fi only: install the app from the release page on the phone, then on the computer open'
  info 'Settings > General > Wi-Fi & Tailscale > Pair a phone over Wi-Fi and scan the QR code.'
  if [ "$os" = linux ]; then info 'With a firewall (ufw, firewalld), Wi-Fi also needs TCP port 8766 open on your local network.'; fi
  info "Until Wi-Fi is on, PhoneBox only listens on this computer. Every connection it makes: https://github.com/$repo/blob/main/PRIVACY.md"
  info 'Updates: PhoneBox updates itself from Settings. Run setup again any time to repair it.'
  info "Uninstall: curl -fsSL https://raw.githubusercontent.com/$repo/main/scripts/uninstall.sh | bash -s -- [--purge] [--apk]"
}

# Everything runs from here, so a download cut off half way cannot run half a script.
main "$@"
