# ExtendScreen (AzurateMirror)

Turn an Android tablet into a **real second Windows monitor** over USB or WiFi — a fully local,
privacy-focused alternative to commercial screen-extender apps like spacedesk. No cloud, no
telemetry, no accounts. Just your PC and your tablet on the same USB cable or LAN.

Built because Miracast/Cast-to-Device is broken on a lot of real devices (firmware bugs are common
and effectively unfixable from userspace), and because "just install a third-party app" wasn't
good enough when the whole point is not handing your screen contents to someone else's server.

## What it does

- **Extend mode** (default): the tablet becomes a genuine Windows-recognized second display via a
  virtual display driver — drag any window onto it and it behaves exactly like a real monitor.
- **Mirror mode**: shows your PC's whole main screen instead, for a simple duplicate view.
- **USB or WiFi**: pick USB for zero network exposure (this app runs `adb reverse` for you — no
  manual setup), or WiFi to use it without a cable, on the same local network.
- **Touchpad relay** (optional, off by default): tap/drag on the tablet to move and click this
  PC's mouse, long-press for right-click. Gated by a checkbox on *both* ends — either side can
  turn it off. See [Known limitations](#known-limitations) below for an important caveat.
- **Zero telemetry**: no analytics, no crash reporters, no outbound network calls beyond the
  direct PC↔tablet connection you set up yourself.

## Requirements

- **Windows 10/11** PC with a GPU that supports DXGI Desktop Duplication (basically any GPU from
  the last decade) and a hardware H.264 encoder (Intel Quick Sync, NVIDIA NVENC, or AMD VCE/VCN —
  again, common on anything reasonably recent).
- **[.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** on the PC (only if you use
  the framework-dependent build; the self-contained release build needs nothing extra).
- **[VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)**
  installed on the PC — this is what makes Extend mode a real Windows display. MIT licensed, not
  bundled here; install it separately (see their releases page) before first use.
- **Android 8.0+** tablet or phone.
- For USB mode: a data-capable USB cable and USB debugging enabled on the Android device
  (Settings → About → tap Build Number 7 times → Developer Options → USB debugging).
- For WiFi mode: both devices on the same local network, and the PC's WiFi network profile set to
  **Private** (not Public) in Windows Settings, or the connection will time out.

## Getting started

1. Install the Virtual Display Driver on the PC (link above) — reboot if it asks.
2. Run `AzurateMirror.Sender.exe` on the PC. On first Start, allow the Windows Firewall prompt for
   the **Private** network only.
3. Install the APK on your Android device (`adb install AzurateMirror-receiver.apk`, or copy it
   over and install manually with "install from unknown sources" allowed).
4. On the PC app: pick USB or WiFi, then click **Start**. A UAC prompt appears — this is for
   enabling/disabling the virtual display driver programmatically, not for running the app itself
   elevated.
5. On the tablet: open AzurateMirror, leave the IP field as `127.0.0.1` for USB, or type the PC's
   LAN IP (shown right in the PC app's window) for WiFi. Tap Connect.
6. Drag a window onto the new "display 4"-ish monitor on the PC and watch it show up on the tablet.

## Building from source

**Windows sender** (requires .NET 8 SDK):
```
cd windows/src/AzurateMirror.Sender
dotnet build
```

**Android receiver** (requires JDK 17 and the Android SDK — set `sdk.dir` in
`project/local.properties`, which is gitignored since it's machine-specific):
```
cd project
./gradlew assembleDebug
```

## Known limitations

- **Touchpad relay moves the real system mouse cursor, not a separate touch pointer.** Windows'
  native touch-injection API (`InjectTouchInput`) was tried first — it's the architecturally
  correct choice, since real touchscreens report as distinct touch input and don't steal the mouse
  cursor from other monitors. It works fine on real physical displays, but fails specifically when
  targeting the virtual display driver's monitor (no registered digitizer/HID touch device backing
  it — a driver-level limitation, not something this app can work around). The fallback uses
  classic mouse-simulation APIs instead: the cursor's position is saved before a touch and restored
  after it lifts, so it doesn't permanently disturb a physical mouse in use elsewhere, but truly
  *simultaneous* use of both a real mouse and the tablet touch at the same instant can't be
  perfectly resolved — Windows only has one system cursor.
- No audio relay yet — video/control only.
- No multi-client support — one tablet at a time.

## Privacy

No accounts, no cloud relay, no telemetry, no crash reporting SDKs. The Windows app binds to LAN
interfaces only (never a public/internet-facing listener) and everything is a direct PC↔device
connection you initiate. Review the source — it's small enough to read in an afternoon.

## Credits

- [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT) — the virtual monitor driver Extend mode depends on.
- Vendored `adb.exe` + platform-tools DLLs from the Android SDK, used for the automatic USB `adb reverse` setup.
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) — the DXGI/Direct3D11/Media Foundation .NET bindings this project is built on.

## License

MIT — see [LICENSE](LICENSE).
