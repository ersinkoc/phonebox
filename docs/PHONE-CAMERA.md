# Phone as webcam

PhoneBox can expose the phone as **PhoneBox Camera** on Windows 11 x64, without OBS. It uses Windows Media
Foundation's virtual camera API, not a kernel driver. Windows 10, Linux and macOS can use the optional OBS output.

1. Update both the desktop agent and Android app. Connect the phone over USB or paired Wi-Fi and keep PhoneBox open.
2. In desktop Settings → General → Phone as webcam, click **Install component**. Windows asks for administrator
   approval to copy the COM source to `%ProgramFiles%\PhoneBox\Camera` and register its class under HKLM.
   This is a one-time component installation. The agent itself continues to run as your normal user.
3. Click **Enable PhoneBox Camera**. Select the camera whose name starts with **PhoneBox Camera** in your calling app.
   Enabling the virtual device does not start the phone's camera. Until the phone agrees, applications get black video.
4. Select the connected phone and click **Request camera**.
5. On the phone choose the front or rear camera and **Allow this session**. On first use Android also asks for the
   camera permission; after that dialog, confirm the session again. Pending requests expire after 30 seconds.
6. Stop video using **Stop sharing** on the phone or **Stop** in desktop settings. **Disable virtual camera** also
   stops the phone session and removes the virtual device. Close and reopen the calling app's device picker if needed.

The virtual device exists only while enabled in the current agent process, and is visible only to that Windows user.
Enable it again after restarting the agent. To uninstall, disable the camera, close applications using it, and click
**Remove component** (administrator approval). The protected setup helper can remain on disk for future repairs;
the COM registration is removed. An in-use DLL is removed only if Windows releases it.

If Windows refuses to enable it with access denied, check Settings → Privacy & security → Camera, including access
for desktop applications. Native support requires build 22000 or later and the x64 Windows agent. It is not available
in the cross-platform `net10.0` agent package unless its native component is bundled separately.

The first version sends JPEG frames at up to 10 fps, with a preview size no larger than 1280 × 720. Actual resolution
and frame rate depend on the camera and connection. The native output is 1280 × 720 NV12 at up to 10 fps; portrait video
is letterboxed and its orientation is preserved. There is no audio.

## Optional OBS output

Expand **Optional OBS output** in settings. In OBS add a **Browser** source using the address shown there, normally
`http://127.0.0.1:8765/phone-camera`. Set it to 1280 × 720, then click **Start Virtual Camera**. Select **OBS Virtual Camera**
in the calling app. Leave OBS's “Shutdown source when not visible” off if the virtual camera needs that source.

## Consent and privacy

- Pairing and Android camera permission do not grant permission to stream. Every session needs native confirmation
  on the phone. The prompt shows the destination name and a prefix of its pinned certificate fingerprint.
- Camera capture is native, with no JavaScript method to start or approve it. Closing the app, locking the phone,
  changing destination/transport or rotating the phone ends sharing. Rotation requires a fresh request.
- The session captures one pinned HTTPS destination and credential. Frames never fall back to another PC or route.
  A fresh random session capability is needed in addition to pairing credentials. Consent is not saved.
- A missing upload expires consent and clears the agent's frame after 3 seconds (5 seconds to deliver the first frame).
  The native source outputs black video when the agent has no frame, cannot be reached, or returns an invalid frame.
  The OBS page clears its canvas when the agent is unavailable. Normal stop clears the agent as soon as notified;
  failed notification falls back to expiry. A frame already in transit can arrive before stop is processed.
- Frames and session capabilities are kept only in memory. PhoneBox does not record or send them to a cloud service.
  The destination computer and applications receiving the virtual camera can record the video.
- The OBS output and controls are available only through the local settings listener, protected by the existing Host,
  Origin and browser fetch checks. Other local processes can read the output, as they can access the settings API.
  Paired phones and webhook clients cannot fetch the output. Do not forward or reverse-proxy the local settings port.
- Windows Frame Server loads the native source from protected Program Files. It reads only the agent's fixed loopback
  frame endpoint, using the current agent port passed through a private activation attribute. It uses no proxy or
  redirects, accepts bounded JPEG frames, and never receives phone pairing or camera-session keys.
- Native installation, removal, enabling and disabling are local-only POST operations. Phone, webhook and custom-widget
  clients cannot invoke them. The component does not open any physical Windows camera or automatically request phone video.
- Camera polling runs once per second only while PhoneBox is visible. Older clients without this feature will not
  show a request; it expires without starting their camera.

## Verification on a real phone

Before relying on the feature, check front/rear preview and orientation, Android permission denial, phone locking,
leaving the app, USB removal, Wi-Fi loss, changing PCs, desktop Stop, request expiry, and OBS Virtual Camera in the
calling application. Reconnection must require a new approval. Check that every stop produces black native video
(or “Camera off” in OBS).

## Building the native component

Install CMake, Visual Studio C++ Build Tools and a Windows SDK at least 22000. From the repository root:

```powershell
./tools/build-native-camera.ps1 -Test
dotnet build agent/PhoneBox.Agent -f net10.0-windows10.0.19041.0
```

The .NET Windows build/publish includes the compiled `camera/PhoneBoxCamera.dll` and `camera/PhoneBoxCameraSetup.exe`
when present. CI builds and tests them before publishing Windows packages. Cross-platform tests need no C++ toolchain.
The CTest suite exercises COM activation, sample events/tokens, shutdown, JPEG rotation, letterboxing and blanking.
After installing the component, a separate integration check uses Windows Frame Server and a loopback test feed:

```powershell
./native/PhoneBox.Camera/dist/bin/Release/PhoneBoxCameraTests.exe "$env:ProgramFiles/PhoneBox/Camera/PhoneBoxCamera.dll" --device
```

That check temporarily enables a session camera, reads a generated image through the registered Windows device,
checks that it becomes black when the test feed stops, and removes its temporary virtual device. It never opens a phone
or physical camera. It must run as the same normal user who will use the virtual camera.
