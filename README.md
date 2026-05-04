# UniPeek — Unity Editor Plugin

Stream the Unity Game View to the **UniPeek app** on your iOS or Android device in real-time over a local Wi-Fi network.

---

## Features

| Feature | Free | Pro (via app) |
| --- | --- | --- |
| Stream Game View as JPEG over WebSocket | ✅ | ✅ |
| WebRTC low-latency streaming | ✅ | ✅ |
| mDNS / DNS-SD auto-discovery | ✅ | ✅ |
| QR code pairing | ✅ | ✅ |
| Single touch input | ✅ | ✅ |
| Touch gizmo overlay (Game View circles) | ✅ | ✅ |
| Multi-touch injection | — | ✅ |
| Gyroscope / accelerometer injection | — | ✅ |
| Reverse connection (Android, no USB) | — | — *(Under development)* |
| USB / ADB port forwarding | — | — *(Coming soon)* |
| 540p + 720p streaming | ✅ | ✅ |
| 1080p streaming | — | ✅ |
| 20 fps cap | ✅ | - |
| Multiple connected devices | — | ✅ |

> **Note:** The plugin itself never enforces Pro limits — those are controlled by the companion app tier.

---

## Unity Version Requirements

| Unity | Status |
| --- | --- |
| 2021 LTS (2021.3.x) | ✅ Supported |
| 2022 LTS (2022.3.x) | ✅ Supported |
| Unity 6 (6000.x) | ✅ Supported |
| 2020 and earlier | ⚠️ Not tested |

Requires **.NET Standard 2.1** API Compatibility Level (`Edit → Project Settings → Player → Other Settings → Api Compatibility Level`).

**Input injection:**

- **Legacy Input Manager** — single touch works via internal reflection (`Input.SimulateTouch`). Best-effort; gyroscope and accelerometer are not available.
- **New Input System** (`com.unity.inputsystem`) — full touch, multi-touch, gyroscope, and accelerometer injection via virtual devices. Recommended.
- **Both** — UniPeek injects into both backends simultaneously.

**Optional:**

- `com.unity.webrtc` ≥ 3.0.0 — enables WebRTC streaming mode.

---

## Installation

### 1 — Install the plugin

Download the latest `.unitypackage` from the [Releases page](https://github.com/TolgaDurman/UniPeek/releases/latest) and import it into your project via **Assets → Import Package → Custom Package**.

### 2 — Open the window

```text
Unity menu → Window → UniPeek
```

## Windows

> On first launch on Windows, UniPeek will prompt for a one-time UAC elevation to add a Windows Firewall inbound rule for TCP port 7777.

## macOS & Linux

> No additional permissions are required.

---

## Quick-start: Pairing via QR Code

1. Open the **UniPeek** window (`Window → UniPeek`).
2. Click **▶ Start Streaming**.
   A QR code appears showing the local IP and port.
3. Open the **UniPeek** companion app on your phone.
4. Tap **Scan QR** and point the camera at the QR code.
5. The connection indicator in the Editor turns green; the phone now shows the live Game View.

---

## Quick-start: Pairing via mDNS (no QR)

The plugin broadcasts `_unipeek._tcp` on the local network using mDNS / DNS-SD (RFC 6762).
The companion app will discover the host automatically — just tap the machine name when it appears.

Both the Unity host and the phone must be on the **same Wi-Fi network** (or the same network segment).

---

## Quick-start: Reverse Connection (Android) — Under development

> This feature is currently under development and may not work in all builds.

Use this when your phone cannot reach the PC (hotel Wi-Fi, corporate networks with client isolation, no shared Wi-Fi):

1. In the UniPeek app, switch to **Listen** mode.
2. In Unity, click **▶ Start Streaming**, then expand the **Reverse Connection** panel.
3. Enter your phone's IP address and click **Connect to Phone**.
4. The editor dials out to the phone on port **7778**.

---

## Quick-start: USB / ADB (Android) — Coming soon

Automatic ADB port forwarding is not yet implemented. In the meantime use the **Reverse Connection** mode above, or set up port forwarding manually:

```sh
adb reverse tcp:7777 tcp:7777
```

Then connect the app to `localhost:7777`.

---

## Settings Reference

| Setting | Options | Description |
| --- | --- | --- |
| **Editor Name** | Text field | Display name shown in the app's device list. Defaults to the machine name. |
| **Socket Mode** | WebSocket / WebRTC | Transport layer. WebRTC requires `com.unity.webrtc` ≥ 3.0.0. |
| **Max Bitrate (Mbps)** | Slider | WebRTC video bitrate cap. Higher values improve quality but use more bandwidth. |
| **STUN URL** | Text field | Optional WebRTC STUN server for VPNs, hotspots, or unusual subnet setups. Leave empty for local-only LAN behavior. |
| **Run in Play Mode** | On / Off | **On:** streaming only runs while the Editor is in Play Mode. **Off:** streaming runs in both Edit and Play Mode (stream will briefly drop on domain reloads). |
| **Capture Method** | Camera Render / Async GPU Readback | Camera Render is synchronous. Async GPU Readback reduces main-thread stall at the cost of ~1 frame of extra latency. |
| **Log Level** | None / Error / Warning / All | Console verbosity for UniPeek diagnostic messages. |
| **Port** | Integer (default 7777) | TCP port the WebSocket server listens on. Only editable when not streaming. |

WebRTC mode requires Play Mode and captures the composited Game View at end-of-frame so Screen Space Overlay canvases remain visible. Direct camera capture is intentionally not used for WebRTC because it can miss overlay UI.

Settings are persisted in `EditorPrefs` and restored on next launch.

---

## Message Protocol

### Outgoing (Unity → Phone)

**Binary frames:** Each binary WebSocket message is one complete JPEG frame. No headers or framing.

**Text messages (JSON):**

```json
{ "type": "playmode",  "playing": true }
{ "type": "shutdown" }
{ "type": "offer",     "sdp": "v=0\r\n..." }
```

### Incoming (Phone → Unity)

```json
{ "type": "hello",     "client": "flutter", "tier": "free", "deviceName": "iPhone 15 Pro" }
{ "type": "config",    "resolution": "1280x720", "quality": 75, "fps": 30, "landscape": false }
{ "type": "touch",     "phase": "began", "x": 0.47, "y": 0.63, "fingerId": 0 }
{ "type": "gyro",      "x": 0.1, "y": -0.3, "z": 0.05 }
{ "type": "accel",     "x": 0.0, "y": 0.9,  "z": 0.1  }
{ "type": "ping",      "ts": 1234567890 }
{ "type": "answer",    "sdp": "v=0\r\n..." }
{ "type": "candidate", "candidate": "...", "sdpMid": "0", "sdpMLineIndex": 0 }
```

Touch `x`/`y` are normalised [0, 1]; `x=0` is the left edge, `y=0` is the **top** edge of the phone screen.
`gyro` values are in rad/s; `accel` values are in g-force (Y ≈ 1.0 when the phone lies flat).

---

## Input Injection

### New Input System (required)

Input injection requires `com.unity.inputsystem`. UniPeek creates virtual devices and injects events via `InputSystem.QueueStateEvent`:

- `Touchscreen` — single and multi-touch from the phone (multi-touch requires Pro)
- `Accelerometer` — gravity + motion data (Pro)
- `AttitudeSensor` — gyroscope / rotation-rate data (Pro)

Ensure you have `com.unity.inputsystem` in your `Packages/manifest.json`.

### Legacy Input Manager

Single touch injection is supported via internal Unity reflection (`Input.SimulateTouch(Touch)`). This is best-effort and may break on future Unity versions. Gyroscope and accelerometer injection are **not available** in Legacy mode — use the new Input System for those.

When **Active Input Handling** is set to **Both**, UniPeek injects into the Legacy Input Manager and the new Input System at the same time.

---

## Performance Notes

| Resolution | Expected FPS |
| --- | --- |
| 540p | >60 fps stable |
| 720p | 40 – 60 fps |
| 1080p | 30 – 40 fps |

- **Main-thread budget:** < 2 ms per frame (capture + blit only).
- **JPEG encoding** runs entirely on a background thread via `Task.Run()`.
- The capture loop drops frames automatically when the encoder is still busy (back-pressure).

---

## Troubleshooting

| Problem | Solution |
| --- | --- |
| QR code shows `127.0.0.1` | Machine has no active Wi-Fi / Ethernet. Connect to the network first. |
| Phone can't find host via mDNS | Make sure both are on the same subnet. Some corporate Wi-Fi isolates clients — try the QR code or Reverse Connection mode instead. |
| Firewall rule prompt never appears | Click **Reset FW** in the UniPeek toolbar, then Start Streaming again. |
| Game View is black / null capture | Open a **Game** tab in the Editor and make sure it is visible (not behind other panels). In Edit Mode, ensure a camera tagged `MainCamera` exists. |
| Touch events not registering | Check **Active Input Handling** in Player Settings. Legacy mode uses best-effort reflection. For guaranteed injection, install `com.unity.inputsystem` and set to **Input System Package** or **Both**. |
| High encode latency | Switch Capture Method to **Async GPU Readback**, lower Quality, or reduce Resolution. |
| Stream drops on recompile | Disable **Run in Play Mode** so the stream persists across domain reloads. |
| WebRTC offer/answer hangs | Ensure `com.unity.webrtc` ≥ 3.0.0 is installed. Fall back to WebSocket mode if WebRTC is unavailable. |

---

## License

UniPeek plugin source: **MIT**
QRCoder: **MIT** (<https://github.com/codebude/QRCoder>)
websocket-sharp: **MIT** (<https://github.com/sta/websocket-sharp>)
