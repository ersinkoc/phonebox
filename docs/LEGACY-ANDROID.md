# Experimental Android 4.1 mode

Maintained by Mani Nezafat (مانی نظافت). Based on PhoneBox by Ersin KOÇ and contributors; the original MIT license and attribution apply.

This fork adds an ES5 renderer for Android 4.1 stock WebKit. It uses the running desktop PhoneBox agent's public page layout: the same enabled pages, order, widget positions and live system readings. Edit your layout in the regular desktop settings editor. The existing Android app and its API-21 minimum are unchanged.

## Run on the computer

Install/start the regular PhoneBox desktop agent, then from this repository run:

```sh
python3 tools/legacy-server.py --host 0.0.0.0
```

Open `http://YOUR_COMPUTER_LAN_IP:8789/` on the tablet. Enter the six-digit code printed by the bridge on the computer. Sessions expire after 24 hours or when the bridge restarts. Changes to the desktop layout are read every 15 seconds; system data every 5 seconds. Keep the computer awake and both devices on the same trusted network.

This is a separate, opt-in **read-only HTTP bridge** for old devices that cannot run the normal pinned-TLS app. HTTP is not encrypted: do not expose the port to the internet or use it on shared/untrusted Wi-Fi. A pairing code is required and guesses are rate-limited. It forwards only filtered public layout and state responses from the loopback agent, never the settings, arbitrary paths, commands, hooks, integration credentials, session transcripts or control APIs. No analytics or external services are added.

## Widget coverage

- Clock, stat/gauge tiles, a simple chart for the first configured metric, memory/disks, GPU details, current media title/artist, text/title, calendar date and a local 25-minute focus timer.
- Original layout and page navigation are preserved, but rendering is simplified. The calendar currently shows today's Gregorian date, not the full calendar. World/word clocks use the tablet's local time. Themes, custom fonts and advanced appearance settings are not yet implemented.
- Unsupported widgets explicitly say so. AI quotas, coding sessions, GitHub, weather, camera, Custom HTML, controls and remote actions are not forwarded in this first version.
- Focus timers are local to the visible page and reset when the page is rebuilt. No background notification or audio is promised.

## Optional API-16 launcher

The independent `android/legacy` Java app loads the bridge in an old-compatible WebView, keeps the screen awake **while the app is foregrounded**, and uses full screen. It has a separate package ID, no JavaScript-to-native bridge and no certificate bypass. Press Back to change the address. Pair the app's browser separately from the tablet browser.

```sh
cd android
./gradlew :legacy:lintDebug :legacy:assembleDebug
```

Requires JDK 17 and Android SDK 35. Install `legacy/build/outputs/apk/debug/legacy-debug.apk` on a test device. The debug APK is experimental, not signed with the upstream release key. The dedicated `Legacy Android 4.1` workflow builds this artifact on demand. Build success alone is not a physical-device compatibility test.

## Validation

```sh
python3 -m unittest discover -s tools -p 'legacy_test*.py' -v
npx --yes --package acorn@8.14.1 acorn --ecma5 android/app/src/main/assets/www/legacy/legacy.js > /dev/null
```

Android 4.1 stock browser/device testing remains required. Record the device, OS and browser version and which pages actually render before claiming full support.

## Original panel in Firefox 68

The `tools/kiosk-lite` converter comes from **mzramna/phonebox**, branch `kiosk-web-preview`, commit `9a1b8a232419afc122cf7fd029924a0d45a88b2f` (upstream PR #43), under the original MIT license. This fork extends it to lower the current shared scripts and adjusts the old browser syntax probe. Keep this attribution when reusing it.

```sh
cd tools/kiosk-lite
npm ci
npm run build
```

Then pair in the normal bridge landing page and open `/original/` in the same browser. This uses the **real original panel code**, with JavaScript and CSS adapted for Firefox 68. The bridge still serves only configuration and system state: widget controls and additional data APIs are unavailable. Firefox 68 for Android officially requires Android 4.1 or newer, but it is no longer maintained; use it only for the local dashboard. The stock Android 4.1 browser cannot render this variant and should use the ES5 fallback instead. Test on the physical tablet before claiming compatibility.
