#!/usr/bin/env bash
# Removes the PhoneBox agent on Linux / macOS.
#   ./scripts/uninstall.sh            # agent + autostart entry (settings are kept)
#   ./scripts/uninstall.sh --purge    # ... and the settings, logs and pushed data
#   ./scripts/uninstall.sh --apk      # ... and the panel app on the connected phone
set -euo pipefail
purge=0; apk=0
for a in "$@"; do
  case "$a" in
    --purge) purge=1 ;;
    --apk) apk=1 ;;
    -h|--help)
      printf '%s\n' 'Usage: uninstall.sh [--purge] [--apk]' \
        'Remove the agent and its autostart entry. Settings are kept unless --purge is set.' \
        'Use --apk to also remove the app from the connected phone.'
      exit 0 ;;
    *) echo "unknown option: $a (see --help)" >&2; exit 2 ;;
  esac
done

if [ "$(uname -s)" = Darwin ]; then
  plist="$HOME/Library/LaunchAgents/net.oxog.phonebox.agent.plist"
  launchctl unload "$plist" 2>/dev/null || true
  rm -f "$plist"
  base="$HOME/Library/Application Support/PhoneBox"
  conf="$base"
else
  if command -v systemctl >/dev/null; then
    systemctl --user disable --now phonebox.service 2>/dev/null || true
    rm -f "$HOME/.config/systemd/user/phonebox.service"
    systemctl --user daemon-reload 2>/dev/null || true
  fi
  base="${XDG_DATA_HOME:-$HOME/.local/share}/PhoneBox"
  conf="${XDG_CONFIG_HOME:-$HOME/.config}/PhoneBox"
fi
pkill -x PhoneBoxAgent 2>/dev/null || true
rm -rf "$base/app" "$base/updates"
if [ "$purge" = 1 ]; then rm -rf "$base" "$conf"; fi
if [ "$apk" = 1 ] && command -v adb >/dev/null; then adb uninstall net.zephracloud.phonebox.deskpanel || true; fi
echo "PhoneBox removed."
