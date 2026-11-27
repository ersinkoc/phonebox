# PhoneBox for iOS (experimental)

An initial iOS companion app for PhoneBox. It pairs with the same desktop agent as the Android app and shows
the same web panel, inside a SwiftUI shell and a `WKWebView`.

There are no releases for this app; it is built from source. The panel is **not** duplicated: the Xcode build
copies `android/app/src/main/assets/www` into `PhoneBox.app/www`, so the Android app remains the single source of
the panel, its widgets and its translations.

## Requirements

- macOS with Xcode 16 or newer (iOS 15+ simulator or device, so iPhones and iPads that stop at iOS/iPadOS 15 run it)
- [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`)
- The desktop agent with **Wi-Fi & Tailscale** enabled, and the phone on the same Wi-Fi or Tailscale network
  for the first pairing (USB reuses that pairing)
- For USB: the desktop agent running, with usbmuxd (Apple Mobile Device Support on Windows, libimobiledevice
  on Linux; macOS has it built in)

On Linux (including Arch) you cannot build this locally; use the GitHub Actions workflow below, or a Mac.

## Build

```sh
brew install xcodegen
cd ios/PhoneBox
xcodegen generate
open PhoneBox.xcodeproj
```

Choose the **PhoneBox** scheme and an iPhone simulator, then Run. From the command line:

```sh
cd ios/PhoneBox
xcodegen generate
xcodebuild \
  -project PhoneBox.xcodeproj \
  -scheme PhoneBox \
  -configuration Debug \
  -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath build \
  CODE_SIGNING_ALLOWED=NO \
  build
```

The app is at `build/Build/Products/Debug-iphonesimulator/PhoneBox.app`.

For a physical device, open the project in Xcode, select your team under **Signing & Capabilities**, pick the
device and Run. The generated `Info.plist` already carries the camera and local-network usage descriptions.

The Xcode project, the generated `Support/Info.plist` and the build directory are ignored by git; `project.yml`
is the checked-in definition.

### Tests without Xcode

`PhoneBoxKit` is a small Swift package with the parsing and proxy rules, so the tests need only a Swift
toolchain (the macOS workflow runs them):

```sh
swift test --package-path ios/PhoneBox/PhoneBoxKit
```

They cover both pairing-link forms (`https://ersinkoc.github.io/phonebox/pair/#…` and
`phonebox://pair?…`), the allowed private/Tailscale IPv4 ranges, the check code, the `/agent/*` proxy
allowlist (panel header, read-only images, methods and traversal attempts), and the USB handshake lines.

## GitHub Actions

[`.github/workflows/ios.yml`](../../.github/workflows/ios.yml) runs on `macos-15` for changes under `ios/`, the
Android panel assets or the workflow itself, and manually via **workflow_dispatch**:

1. `swift test --package-path ios/PhoneBox/PhoneBoxKit`
2. `xcodegen generate`
3. `xcodebuild … -destination 'generic/platform=iOS Simulator'`
4. checks that `www/index.html`, `www/main.js`, a widget module and `PanelShim.js` are in the built app
5. uploads `PhoneBox.app` as the `phonebox-ios-simulator` artifact

The simulator app is unsigned (`CODE_SIGNING_ALLOWED=NO`); it is a build check, not a distributable app.

## Pairing

1. On the computer, open PhoneBox settings (tray icon, or <http://127.0.0.1:8765>) and turn on
   **General → Wi-Fi & Tailscale → Let the phone connect without the USB cable**, then Save and restart the
   agent if asked.
2. Choose **Pair a phone over Wi-Fi**. The settings page shows a QR code, the computer's name and a short
   check code (the first 8 hex characters of the TLS certificate fingerprint, e.g. `A1B2 C3D4`).
3. In the iOS app, tap **Scan the pairing QR code** and scan it. On the Simulator (no camera) use
   **Paste a pairing link** instead.
4. Confirm the computer's name and that the app's check code matches the one on the computer, then tap
   **Pair**.

The app then verifies the computer's TLS certificate against the fingerprint from the QR code, sends the
one-time pairing code to `POST /api/pair/claim`, checks the fingerprint in the answer again, and stores the
computer's name, addresses, port, certificate fingerprint and token in the iOS Keychain. Codes work once and
expire after five minutes.

iOS asks for **Local Network** access the first time; allow it, or every request to a LAN address fails.
Tailscale addresses (100.64/10) and LAN addresses (10/8, 172.16/12, 192.168/16) are the only ones the app
accepts from a pairing link.

The panel's options button (top-right `⋯`) shows the paired computer, its fingerprint and check code, the
USB state, and offers **Forget this computer**, which deletes the Keychain item.

## USB cable

iOS has no `adb reverse`: usbmuxd only lets the computer open connections *into* the phone. The direction is
therefore inverted — the app listens on loopback and the desktop agent dials in — and the panel's HTTPS
session runs over that pipe instead of Wi-Fi:

1. Pair over Wi-Fi or Tailscale once. USB reuses the pairing's certificate and token.
2. Keep the agent running with **Wi-Fi & Tailscale** enabled (for the certificate and token; the network
   itself is not used), connect the iPhone, unlock it and tap **Trust**.
3. Open the panel. The agent finds the phone through usbmuxd on its own and keeps a pool of authenticated
   pipes; there is no helper or `iproxy` to run. The options sheet shows **USB cable: Connected**.

The app accepts a pipe on `127.0.0.1:8765` only after the agent answers a fresh nonce with
`HMAC-SHA256(token, nonce)`, so the token itself never crosses the cable and another app on the phone cannot
join the pool. Each pipe carries one TLS session end to end to the agent's Wi-Fi HTTPS port, so the same
certificate pin and Bearer token apply; the agent only moves ciphertext. `127.0.0.1:8767` (the address
`AgentClient` uses) listens only while a pipe is authenticated, so with no cable the app goes straight to
Wi-Fi.

The app must stay in the foreground. There is no USB-only pairing. The agent needs usbmuxd — Apple Mobile
Device Support on Windows (installed with iTunes or Apple Devices), libimobiledevice on Linux, or the system
daemon on macOS — and retries quietly when no iPhone is attached. The agent reserves a connection attempt
for each attached USB device when the pool has room, and favors the last working phone for spare connections.

## How the panel is served

- The panel's files are served from the app bundle under the private origin `phonebox://localhost/www/…` by a
  `WKURLSchemeHandler`. A custom scheme gives the page a normal-looking origin, so its ES modules load.
- The panel's `fetch('/agent/api/…')` calls go through a document-start shim (`PanelShim.js`) to a native
  `WKScriptMessageHandlerWithReply`. WebKit refuses `fetch()` to custom schemes, which is why the shim exists.
  Only the main frame may call the handler, and only with a per-launch bridge key, so a sandboxed Custom HTML
  widget iframe cannot use the proxy.
- Images (`/agent/api/art`, icons, covers, wallpaper) are loaded through the scheme handler; they cannot carry
  the panel header, so GET requests for the same read-only image endpoints the Android shell allows are
  accepted without it.
- `AgentClient` tries the USB relay at `127.0.0.1:8767` first, then the paired Wi-Fi/Tailscale addresses,
  and adds `Authorization: Bearer <token>`, `X-PhoneBox-App`, `X-PhoneBox-Device` and `X-PhoneBox-Model`,
  keeps query strings and methods, and returns the agent's status, content type and body unchanged. Only
  `GET` and `POST` are forwarded; redirects are treated as errors, like on Android.
- The token and certificate pin exist only in native code. The WebView never receives either.

## Security notes

- The agent's certificate is self-signed, so identity is the SHA-256 pin from the pairing link. A connection is
  accepted only when the leaf certificate matches it; otherwise the handshake is cancelled and no pairing code
  or request body is sent. There is no fallback to the system trust store.
- No App Transport Security exception is declared. TLS is never disabled globally and no broad arbitrary-load
  exception exists.
- The answer's `fingerprint` is compared with the link's before the token is stored; a mismatch discards the
  token and shows an error.
- Pairing only happens after the user confirms the computer name and check code.
- The Keychain item uses `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (not migrated by backups) and is
  the only storage for the token. Nothing is logged.
- The proxy refuses anything that is not `/agent/*` and rejects path traversal and percent-encoded dots/slashes.

## Current limitations

- **One computer per iPhone.** Choosing between several paired computers (the Android page overview) is not
  implemented. Forget the current computer and pair another.
- **USB needs a first pairing over Wi-Fi/Tailscale and the desktop agent.** There is no USB-only pairing. The
  agent must be running with its Wi-Fi listener enabled (for the certificate and token it lends to USB) and
  needs usbmuxd: Apple Mobile Device Support on Windows, libimobiledevice on Linux, built in on macOS. Without
  an iPhone it retries quietly. One iPhone at a time; the agent keeps 12 pipes.
- **The USB pool is fixed (12 pipes).** Each pipe carries one keep-alive TLS connection; a request waits up to
  3 s for a free pipe, then fails rather than being retried, so a command is never sent twice.
- **The agent lists a cable connection as Wi-Fi**, because USB reuses its Wi-Fi listener; the iOS app's own
  options sheet shows the real USB state.
- **No live event stream.** The panel falls back to polling (`/agent/api/state`), so updates are slower than
  the Android stream, and features that rely on the stream (some spectrums, push events) update less often.
- **Android-only bridge features are absent, not emulated:** light/proximity sensors, screen brightness,
  the phone's own battery, dictation, haptics, notification buzz, volume-key page flipping, screen pinning,
  home-screen role, forced orientation, Wake-on-LAN, the trackpad and switching computers. The panel loads and
  works without `window.Android`; those widgets show their unavailable state.
- **On iOS 15 widgets don't scale fully.** Safari 15 has no container queries or container units (they came in
  Safari 16), so, as on an Android WebView older than Chrome 105, small widgets keep details they would hide and
  some sizes don't follow the widget. The panel itself runs: keep its code free of syntax Safari 15 rejects, such
  as regex lookbehind (`(?<=`, `(?<!`, Safari 16.4), which stops `main.js` from loading at all.
- The panel does not force landscape on iPhone; every orientation is allowed and the panel lays itself out.
- The app is a foreground panel. iOS suspends it in the background; polling resumes when it is frontmost.
- PhoneBox does not register the `phonebox://` URL scheme on iOS, so a pairing link opened from another app does
  not launch it. Pair by scanning the QR code or pasting the link inside the app.
- No app icon asset catalog is included yet (the Simulator build uses the default icon).
- There is no automatic update mechanism, App Store release or TestFlight build.
- CI builds the Simulator app. A [manual test on an iPhone 16e](https://github.com/ersinkoc/phonebox/issues/604)
  confirmed pairing, panel rendering, and USB-to-Wi-Fi failover with a macOS agent.

## Layout

```
ios/PhoneBox/
├── project.yml                 XcodeGen definition
├── PhoneBoxKit/                pairing/proxy rules + tests (Swift package)
└── PhoneBox/
    ├── PhoneBoxApp.swift       app entry point
    ├── RootView.swift          panel shell and computer options
    ├── UnpairedView.swift      unpaired screen, QR and paste flows
    ├── PairConfirmationView.swift
    ├── Pairing/                claim flow, Keychain-backed store, QR scanner
    ├── Networking/             pinned URLSession, agent client, device headers
    ├── USB/                    loopback USB relay (nonce-authenticated pipes)
    ├── Panel/                  scheme handler, fetch bridge, WKWebView host
    └── Resources/PanelShim.js  routes /agent fetches to native code
```

The computer side lives in the agent: `agent/PhoneBox.Agent/IosUsbBridge.cs` speaks usbmuxd directly and
starts with the agent.
