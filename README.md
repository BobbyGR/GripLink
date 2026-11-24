# GripLink: a virtual PS5 controller for the MSI Claw

GripLink makes Windows on your MSI Claw see a **DualSense (PS5) controller**, driven by the Claw's built-in controls.
PS Remote Play, Steam and games then treat the Claw as a PS5 controller: PlayStation button prompts, rumble, and the controller's microphone and speaker.

> GripLink is an independent project. It is not affiliated with, endorsed by or sponsored by Sony Interactive Entertainment or Micro-Star International (MSI). "PlayStation", "DualSense" and "PS Remote Play" are trademarks of Sony Interactive Entertainment; "MSI" and "Claw" are trademarks of Micro-Star International. They are used here only to describe compatibility.

## Install

1. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
2. Install [usbip-win2](https://github.com/vadimgrn/usbip-win2/releases). It provides the signed driver that plugs the virtual controller into Windows.
3. Put the Claw's controller in **XInput mode** in MSI Center M.
4. Download `GripLink.zip` from [Releases](../../releases), unzip it anywhere, and run `GripLink.exe`.

Optional:
* In MSI Center M, set **M1 = F13** and **M2 = F14**. GripLink maps those keys to controller buttons.
* Install [HidHide](https://github.com/nefarius/HidHide) and hide the Claw's own Xbox controller from games, so they don't see two controllers. Allow-list `GripLink.exe`.

## The app

Run **`GripLink.exe`**. It shows one window and lives in the tray.

| Setting | What it does |
|---|---|
| **ON / OFF** | Plugs the virtual PS5 controller in or out. The tray menu has the same switch. |
| **Status** | Shows whether Windows sees the controller, whether the Claw's controls are found, whether a game or Remote Play is using it, and the light-bar colour the game set. |
| **Model** | DualSense, or DualSense Edge (M1/M2 become back paddles). |
| **Connection** | USB cable (default; needed for PS Remote Play) or Bluetooth protocol. |
| **Rumble** | Game rumble on the Claw's motors, with a strength slider. |
| **Stick anti-dead zone / dead zone** | Anti-dead zone cancels a game's own dead zone. Dead zone is off by default, so sticks are raw. |
| **Controller mic** | Silent, or a real microphone (e.g. the Claw's) feeding the controller's mic. Includes a device picker, volume and mute. |
| **Controller speaker** | Discard, or play what Windows sends to the controller's speaker on a real speaker. |
| **Keep my default sound devices** | Stops Windows switching your default speaker and mic to the controller when it plugs in. |
| **Start with Windows / Start hidden in the tray** | Starts at sign-in, quietly, in the tray. |
| **Turn the controller on when GripLink starts** | Plugs the controller in automatically. |
| **Closing keeps running in the tray** | The X button hides the window; quit with Exit in the tray menu. |

Settings are saved in `%APPDATA%\GripLink\settings.json`. The first run copies `appsettings.json` from next to the exe, which also holds the advanced options: button map, key bindings, combos and motion axes.

Only one copy runs at a time; launching it again opens the existing window.

## How it works

```
Claw built-in pad (XInput) ─┐
M1/M2 (keys you assign)  ───┼─> ClawMapper ─> VirtualDualSense ─> USB/IP server ─> usbip-win2 ─> Windows HID ─> games
Claw IMU (Windows sensors) ─┘        ▲                │  (BT report protocol)  127.0.0.1:3240
Battery                               └── rumble <─────┘
```

* **Bluetooth protocol, as a real controller speaks it:**
  * The controller starts in BT *simple mode* and sends a 10-byte report `0x01`.
  * When the host reads calibration (feature `0x05`) or sends output `0x31`, it switches to BT *enhanced mode*. It then sends a 78-byte report `0x31` with sticks, buttons, gyro, accelerometer, touchpad, battery and a CRC-32 (seed `0xA1`).
  * Feature reports `0x05` (calibration), `0x09` (pairing/MAC) and `0x20` (firmware) carry BT CRCs (seed `0xA3`).
  * Output report `0x31` (rumble, lightbar, player LEDs, adaptive triggers) is parsed and checked against its CRC (seed `0xA2`). Rumble is sent on to the Claw's motors.
* SDL, Steam and most DualSense-aware games decide "Bluetooth or USB" from these report sizes and IDs, so they treat GripLink as a Bluetooth DualSense.
* **Transport:** Windows has no supported way for an app to create a Bluetooth HID device. GripLink therefore exports the controller over **USB/IP**. The signed, open-source [usbip-win2](https://github.com/vadimgrn/usbip-win2) driver attaches it, and Windows creates a real HID device `VID_054C&PID_0CE6` from it. The one place this differs from a real BT pad: Device Manager lists it under the USB/IP host controller, not the Bluetooth radio.

## Transport modes (`"Transport"` in appsettings.json)

* **`Usb` (default, works with PS Remote Play).**
  * Its USB descriptors are a byte-for-byte copy of a real DualSense on a USB cable, captured from real hardware: the device, configuration and 289-byte HID report descriptor, plus the feature reports.
  * That includes its USB-audio speaker (4 channels) and microphone (2 channels, 48 kHz), streamed in real time. By default the speaker audio is discarded and the microphone is silent; the app can connect both to real devices.
  * Like a real controller, it runs at high speed with the HID controller part on interface 3 (`MI_03`).
* **`Bluetooth`.** The Bluetooth report protocol described above. SDL and Steam treat it as a Bluetooth DualSense, but PS Remote Play ignores it.

**What PS Remote Play checks.** Remote Play skips a PS5 controller without even opening it unless it looks like a real cable-connected one, speaker and microphone included. A HID-only virtual DualSense is never detected, even with the right IDs and reports. It only looks for a controller once you connect to a console.

**Side effect.** Windows makes "Speakers (DualSense Wireless Controller)" the default sound output, just as it does for a real controller. GripLink's "Keep my default sound devices" option switches it back.

## Build

Needs the .NET 8 SDK.


```powershell
dotnet publish GripLink.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

The result is `publish\GripLink.exe`, which needs the .NET 8 Desktop Runtime. Command-line tools (pipe them to see the output):

* `GripLink.exe --selftest | Out-Host` checks the report layouts, CRCs, audio streaming and a full USB/IP session over loopback. It needs no driver and no Claw.
* `GripLink.exe --dump | Out-Host` shows the live Claw input as DualSense values. Add `--hex` to also print the raw report.
* `GripLink.exe --tray` starts hidden in the tray; this is what "Start with Windows" uses.

## Button mapping (default)

| Claw | DualSense |
|---|---|
| A / B / X / Y | Cross / Circle / Square / Triangle |
| LB / RB, LT / RT | L1 / R1, L2 / R2 (analog plus digital bit) |
| LS / RS click | L3 / R3 |
| View / Menu | Create / Options |
| Guide | PS |
| View + Menu together | Touchpad click |
| M1 (F13) / M2 (F14) | Touchpad click / Mic mute |
| Gyro + accelerometer | DualSense motion |
| Claw battery | DualSense battery level and charging state |

Everything above can be changed in `appsettings.json`. Set `"Model": "DualSenseEdge"` to expose M1/M2 as real Edge back paddles (`EdgeBackL` / `EdgeBackR`).

## Tuning motion

The IMU axis mapping is a best guess for holding the Claw like a controller. Run `--dump` and tilt the device:

* Tilting the top of the screen away from you should change `gyro X`.
* Turning left or right should change `gyro Y`.
* At rest with the screen facing up, `acc Y` should read about +8192.

If an axis moves the wrong way, flip its sign in `Motion` (for example `"GyroY": "-Z"`).

## Files

| File | What it does |
|---|---|
| `DualSense/DualSenseReports.cs` | BT input/feature report builders and output parser (byte layouts) |
| `DualSense/Crc32.cs` | Bluetooth CRC-32 |
| `DualSense/Descriptors.cs` | USB device/config/string descriptors and the HID report descriptor |
| `DualSense/VirtualDualSense.cs` | USB/HID control requests, simple to enhanced mode switch, report streaming |
| `UsbIp/UsbIpServer.cs` | USB/IP 1.1.1 server (devlist, import, submit, unlink) |
| `Input/ClawMapper.cs` | Claw input to DualSense state; rumble back to the Claw |
| `Input/MotionSource.cs` | Gyro and accelerometer through Windows.Devices.Sensors |
| `Engine.cs` | Turns the controller on/off; wires input, rumble, audio and usbip attach |
| `Audio/AudioBridge.cs` | Real mic to controller mic, controller speaker to real speaker (NAudio) |
| `Audio/DefaultDeviceGuard.cs` | Restores your default sound devices after the controller plugs in |
| `UI/MainForm.cs` | The window and tray icon |
| `UI/AutoStart.cs` | Start with Windows (per-user Run key) |
| `SelfTest.cs` | Offline tests |

## Limits

* This works only on the Claw itself. It cannot make the Claw act as a controller for a PS5 console: the console checks controllers with Sony's cryptographic authentication.
* No touchpad surface. The Claw has no touchpad, so only touchpad *click* is mapped.
* Adaptive-trigger effects and lightbar colours are received and logged. The Claw has no hardware for them, apart from forwarding rumble.
* Not yet checked on real hardware: the gyro axis signs, and whether M1/M2 come through as keys on your firmware.
* Most of the input lag in PS Remote Play comes from the video stream itself; GripLink adds about 1 ms. A wired PS5 and lower stream resolution help most.

## License

MIT, see [LICENSE](LICENSE). Uses [NAudio](https://github.com/naudio/NAudio) (MIT).
