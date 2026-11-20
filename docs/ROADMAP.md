# Roadmap

What is planned or waiting, in one place. Issues are closed once they have an answer, so this file is where the
open work lives; each item links the issue or pull request it came from. Closed issues stay readable, and the
people who proposed or prototyped something are credited in the [changelog](../CHANGELOG.md) when it ships.

## Being built in-house

- **Antigravity sessions on the panel:** detect running Antigravity sessions, the way Claude Code and Codex
  sessions already show up (reference [#36](https://github.com/ersinkoc/phonebox/pull/36)).
- **Kiosk page for older devices:** a panel that runs in an older browser or tablet, with a paired key that can only
  do what the panel needs ([#41](https://github.com/ersinkoc/phonebox/issues/41); reference
  [#43](https://github.com/ersinkoc/phonebox/pull/43); old iPads in [#222](https://github.com/ersinkoc/phonebox/issues/222)).
- **Plugin system:** community widgets ([#40](https://github.com/ersinkoc/phonebox/issues/40); reference
  [#46](https://github.com/ersinkoc/phonebox/pull/46)).

## Contributions in progress

- **Discord widget: show who is speaking** ([#226](https://github.com/ersinkoc/phonebox/issues/226)).
- **Feeding PhoneBox from MQTT**: a bridge script and a guide example for pushed values and notifications
  ([#241](https://github.com/ersinkoc/phonebox/issues/241)).
- **Rules for pushed values**: a "not updated for N seconds" condition, `>=` / `<=`, and a separate release threshold
  ([#242](https://github.com/ersinkoc/phonebox/issues/242), pull request
  [#245](https://github.com/ersinkoc/phonebox/pull/245), in 0.9.21, did the first two).
- **Codex permission prompts on the phone**: answer Codex's own approval requests with Allow / Deny, like Claude Code's
  ([#253](https://github.com/ersinkoc/phonebox/issues/253), pull request
  [#254](https://github.com/ersinkoc/phonebox/pull/254)).
- **Live Codex quota without a running session**: read the quota through Codex's own app-server, so the numbers
  update while Codex isn't running on this computer ([#298](https://github.com/ersinkoc/phonebox/issues/298), pull
  request [#290](https://github.com/ersinkoc/phonebox/pull/290)).
- **Wake the PC from the panel**: a Wake button while the paired computer is unreachable; the phone sends the
  Wake-on-LAN packet itself ([#304](https://github.com/ersinkoc/phonebox/issues/304), pull request
  [#303](https://github.com/ersinkoc/phonebox/pull/303)). The Wake a computer shortcut is #302.
- **Voice commands**: say what you want and a language model of your choice (local through Ollama, or a hosted one)
  picks from the shortcuts you allowed; opt-in, off by default ([#257](https://github.com/ersinkoc/phonebox/issues/257)).
- **Immich album as a photo frame source** ([#278](https://github.com/ersinkoc/phonebox/issues/278), pull requests
  [#279](https://github.com/ersinkoc/phonebox/pull/279) and [#280](https://github.com/ersinkoc/phonebox/pull/280)).

## Open for a contributor

Agreed in principle, not being built in-house. Say so on the issue before starting.

- **Photo frame pictures as the page background** ([#288](https://github.com/ersinkoc/phonebox/issues/288)): a
  per-page background from the photo frame's folder, dimmed under the widgets, using the same `/api/photos` feed.
- **Android screen saver (DreamService)** ([#287](https://github.com/ersinkoc/phonebox/issues/287)): show the panel as
  the phone's screen saver while it charges or docks, with the lock screen left in place. With Keep awake on, the
  agent turns the phone's screen saver off over adb, so that has to follow the new setting.
- **Headless Linux server and container image** ([#281](https://github.com/ersinkoc/phonebox/issues/281)): a documented
  way to run the Linux agent without a desktop session, an address to advertise for Wi-Fi pairing from behind
  container networking, and persistent data. Remote access to the settings page needs its own sign-in first; until
  then an SSH tunnel to the loopback port is the safe way.

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

- **Intel GPU load on Linux** ([#224](https://github.com/ersinkoc/phonebox/issues/224)): in 0.9.20, to be confirmed
  on real hardware.
- **Network widget about 4x too high on a Realtek USB 2.5GbE adapter**
  ([#169](https://github.com/ersinkoc/phonebox/issues/169)): the adapter's counters are right; the agent's own numbers
  are needed to find where the factor comes from.

## How to help

Comment on the linked issue (a closed issue can be reopened) or open a draft pull request. For a larger feature,
please say so before building it, so two people don't build the same thing. See [CONTRIBUTING.md](../CONTRIBUTING.md).
