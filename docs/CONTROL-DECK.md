# Control Deck implementation and verification

The requested scope includes every item in the proposed control catalog. Existing widgets remain available.
This document tracks implementation and evidence; a catalog entry or demo is not proof of a native integration.

## Requirements

| Group | Required capabilities | Implementation / evidence |
|---|---|---|
| Audio mixer | Per-application mixer, pinned application fader, device volume, stereo balance, meters, emergency mute | Native Windows implementation; isolated silent-session write/readback passed without changing other sessions or device levels |
| Microphone / devices | Microphone volume/mute, input/output selection, push-to-talk, audio scenes, ducking | Implemented; lease expiry/overlap/restoration tests pass; physical microphone/default-device mutations deliberately not exercised during active work |
| Music | Player selection, transport, seek, shuffle/repeat, playlists, queue/favorites, soundboard, sleep timer | Implemented; Spotify PKCE, disconnect, rate-limit and URI tests pass; actual Spotify account connection remains user setup |
| Application controls | Running application dock, keys, macros, window layouts, desktops, projects/folders, snippets, presentations | Implemented with computer-configured bindings; foreground/window/presentation actions need the corresponding running application |
| Display / system | Brightness, display profiles, power plans, work/focus profiles, lock/display off, sleep/restart | Implemented; destructive power actions require opt-in and a single-use hold nonce; power and display mutations not executed during active work |
| OBS | Input mixer, scenes, source visibility, recording/streaming, replay buffer, transitions, virtual camera | Implemented; WebSocket fixture verifies native protocol requests and resource validation; browser mute interaction passes |
| Meetings | Meeting shortcuts, Discord shortcuts, microphone/camera indicators, meeting profiles/timers | Implemented; timer persistence browser test passes; app shortcuts require user-specific bindings |
| Development / automation | Build/test tasks, dev servers, services, Docker, GitHub workflows, scripts, Home Assistant scenes | Implemented, reusing existing Docker/HA widgets; owned-process stop test preserves unrelated processes; workflow dispatch and service mutations require configured targets |
| Advanced audio / protocols | Application routing, external mixer, MIDI, OSC, PC-audio listening on the phone | Implemented with explicit dependencies/opt-ins; MIDI/OSC encoding tested; external hardware, Voicemeeter and listening playback require separate live validation |
| Product integration | Widget catalog/options, setup editor, templates, phone/LAN proxy, demo, documentation | 113 widgets and 30 templates; private settings/token routes excluded from LAN; 40 browser tests pass, including settings persistence and portrait/landscape layout |
| Validation | Unit/API tests, browser interactions, native Windows checks, Android lint/build, release/installed proof | 245 .NET tests and 40 browser tests at 0.8.0, Android lintDebug/assembleDebug, both .NET target builds and installer regression checks pass; release/installation require the published-artifact checks below |

## Local verification (2026-11-10)

- Both .NET targets compile with zero warnings and zero errors; the full .NET suite passed 245 tests at 0.8.0. An earlier
  concurrent build run hit the existing PowerShell child-startup timeout test; its isolated rerun and the final full
  suite passed. The test was not skipped or weakened. OBS tests also cover the explicit loading response during slow discovery and wait for the measured controls before asserting their contents.
- HTTP fixtures serialize port allocation/start and use distinct ports; a sixteen-listener contention test covers parallel startup. Fixtures close their listener once: calling Stop followed by Close can rebind an already removed endpoint in the Unix HttpListener implementation. Owned-process fixture cleanup waits for bounded Windows descendant handle teardown.
- All 40 Playwright tests pass, including application fader isolation, output selection, PTT release, disabled
  unsupported controls, OBS mute, Spotify queue, persistent meeting timers, private settings/public-config separation,
  catalog construction and 780px/360px layouts. Screenshots were inspected for both orientations.
- Android `lintDebug assembleDebug` succeeds. The installer regression verifies signature/error failures preserve
  the installed application and successful upgrades work.
- A separate Windows agent using an isolated config discovered nine devices and twenty-four sessions. A silent WAV
  generated one owned test session; setting it to 35%, reading back and restoring succeeded. All device levels and
  unrelated application session levels remained unchanged. Its loopback endpoint supplied 48 kHz stereo 32-bit float
  audio; no captured audio was saved. First state response was approximately 1.1 seconds during hardware discovery.
- Spotify authentication/account playback, external MIDI hardware/OSC receiver, Voicemeeter, routing migration and
  physical-phone audio playback are not established by these local checks. Power, service, foreground-window and
  display mutations were not run against the user's active work.

For publication, follow [RELEASING.md](RELEASING.md): exact-commit CI, native builds/smokes, signed APK, release tag,
checksums/provenance, downloaded Windows-binary smoke and physical-device version/preservation checks. Unit tests,
preview data and catalog entries do not replace those checks.

## Setup

1. Open desktop settings and create any executable, folder, text or macro shortcuts in **Deck**.
2. Open **General → Control Deck**, add bindings, choose a kind and enter the target described by the inline hint.
   A binding ID identifies the control; executable paths, arguments and credentials are not sent to the phone.
3. Use **List device, monitor and MIDI IDs** to identify actual hardware. Discovery of some DDC/CI displays can take
   several seconds; refresh the list after discovery finishes. Unsupported monitor brightness displays as unavailable.
4. Add a template or individual widgets under **Pages**, enable needed control permissions, then Save. Template
   creation does not install external applications or create executable bindings automatically.

Audio profiles capture endpoint volumes/mutes, per-application volumes/mutes and all Windows default-device roles.
Application levels match active application names rather than temporary session IDs; closed applications are skipped.
Applications with several sessions receive the captured application level. Explicit input/output overrides apply after
captured roles. Captured levels and ordered action IDs can be edited in settings. A routine-only profile can contain
just action IDs. Missing explicit devices are rejected before audio levels are changed; subsequent native failures can
still leave a partially applied profile and are reported as failures.

Push-to-talk temporarily unmutes the selected microphone. Release, page hiding, window blur, lost capture or lease
expiry restores its original mute state. Renewals expire after three seconds without contact, with a maximum continuous
hold of sixty seconds. Overlapping holds share the original state. Ducking lowers other audio sessions while an unmuted
microphone has a signal, and preserves manual volume changes when restoring. It is not a noise gate or speech recognizer.

The music sleep timer fades matching application sessions over its final thirty seconds, pauses the selected player,
then restores volume. A manual volume adjustment takes ownership away from the fade. Players without a matching
application audio session can still be paused; their volume cannot be faded. Soundboard files and radio streams play on
Windows; radio targets accept direct HTTP(S) audio streams and Windows-only HLS (`.m3u8`) manifests, not playlist webpages.

## Optional integrations

| Integration | Computer setup | Feedback and limits |
|---|---|---|
| Spotify | Create an app in the Spotify Developer dashboard, save Client ID, click Connect, register the exact loopback redirect address, then follow the sign-in link | PKCE; credentials stored next to config in a private token file. No automatic browser login during polling. Playback depends on Spotify account/API permissions and an active player. No account has been connected during development. |
| OBS | Enable obs-websocket, configure its URL/password in existing OBS settings and enable control on an OBS widget | Inputs, sources and transitions are discovered; unsupported features remain disabled. OBS control permission applies across OBS widgets. |
| Application routing | Install [SoundVolumeView](https://www.nirsoft.net/utils/sound_volume_view.html), set its executable path and configure process/output ID bindings | Successful helper exit is shown as **Routing requested**; it is not independent proof that a running application migrated its stream. The helper is not bundled. |
| Virtual mixer | Install/start Voicemeeter and set the path to its matching 64-bit Remote DLL | Measured Strip/Bus gain and toggle values; missing engine/DLL displays unavailable. Third-party binaries are not bundled. |
| MIDI | Choose an actual output port ID | Wire-order channel message bytes, e.g. `B0 07 {value}`. Values 0–127. Note pads send note-off after 120 ms. SysEx is excluded. |
| OSC | Configure host:port and address such as `/volume` | Sends the binding's whole-number value as one float argument using network byte order. UDP delivery is not an acknowledgment from the receiving device. |
| GitHub workflows | Install/sign in to `gh`; configure owner/repo, workflow filename and optional ref | Dispatch success means accepted by GitHub CLI, not a completed workflow. |
| Services | Configure the actual Windows service name and start/stop/restart | Reads real service state; restart waits for stop/start state transitions. Permission failures are reported. |
| Docker / Home Assistant | Configure their existing integrations and widget control permissions | Existing container and entity/scene controls remain available; no duplicate daemon or account is introduced. |

Tasks and development servers use executable shortcut IDs. Only processes launched by that binding are stopped;
output is bounded to the latest 64 KiB and readable only from desktop settings. Task timeout is thirty minutes;
development-server timeout is twenty-four hours. Foreground shortcuts type/send keys to the current application;
configure application/global shortcuts appropriately. Presentation bindings target PowerPoint or LibreOffice.

## Platforms and connection behavior

The full native audio/session/device, DDC/CI, Windows service/window/display/power, soundboard, MIDI and loopback
implementation targets Windows. Linux/macOS preserve their existing master volume/media features and report new
native-only operations as unavailable. Configured executable tasks, OSC and account integrations are portable when
their corresponding programs and APIs are available. Do not interpret cross-platform compilation as native feature parity.

Listening is an explicit desktop opt-in and a user-started phone action. It streams default-output loopback PCM to the
phone, stops when its widget leaves view, and allows at most two listeners. Enabling it does not reroute other apps.
Listening and all control requests retain existing USB/LAN authentication boundaries. Spotify sign-in and private task
output are not exposed over LAN. Long control POSTs have a thirty-second phone timeout; after a submitted command
loses its response, the app reports an unknown result rather than retrying a possibly non-idempotent action over Wi-Fi.

## Boundaries

- Phone requests identify configured actions and resources. Executable commands, local file paths and credentials
  stay on the computer.
- Optional integrations expose their setup and capability state. Missing devices/accounts are not represented by
  fabricated values or successful no-op actions.
- Native Windows functionality and Linux/macOS functionality are reported separately.
- Destructive power actions require an explicit hold/confirmation interaction. Device controls use the actual
  selected device identity. Push-to-talk must restore the previous microphone state after release, disconnect or timeout.
- Live validation must preserve the user's active work and restore temporary audio changes.
