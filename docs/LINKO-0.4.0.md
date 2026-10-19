# LINKO 0.4.0

Windows EXE and Android APK share version 0.4.0 (Android versionCode 9).
Windows executable, title, taskbar/tray icon, Android launcher/share label,
notification icon and home header use LINKO. The logo has editable SVG and
Android vector sources; `scripts/build-brand.ps1` builds the PNG and ICO.

## iPhone / iPad without an app

Only keyboard and mouse input are offered on iOS in this distribution. Pair
the Windows PC through Bluetooth, select the device in LINKO, and use F8/F9.
iPhone pointer setup: Settings > Accessibility > Touch > AssistiveTouch >
Devices > Bluetooth Devices; enable AssistiveTouch. iPad can pair from
Settings > Bluetooth. Apple keyboard preset uses Ctrl+Space for language
switching and maps the PC Ctrl key to Command for ordinary shortcuts.

Reviewed `BleHidServer.IsReady`, HID report map/characteristics, target
selection and `ToggleAsync`: input readiness requires approved keyboard and
mouse subscriptions, with no companion or clipboard connection requirement.
No change to the HID report map or input transport was needed. Both keyboard
and mouse must subscribe, so iPhone pointer setup matters.

Sources: [Apple AssistiveTouch pointer guide](https://support.apple.com/en-gb/111775)
and [Apple iPad mouse guide](https://support.apple.com/guide/ipad/connect-a-mouse-ipad10939edf/ipados).
These establish OS support, not proof that a specific Windows Bluetooth
adapter works with every Apple device. Physical iPhone/iPad pairing, typing,
pointer movement, scrolling and F8/F9 have not been verified here. No IPA is
required or shipped; existing iOS companion source remains historical source.

## Interface and compatibility

- Android home contains connection, send and received-image actions; settings
  contains device selection, update, stop and detailed connection status.
- Windows foregrounds device switching and the clipboard toggle. Technical
  settings and diagnostics expand on demand. Raw errors remain available in
  logs; the main status avoids protocol jargon and error codes.
- Android application ID, signing key, content-provider authority, Windows
  settings/vault paths, instance mutex, Bluetooth UUIDs and repository URL are
  unchanged, preserving updates, pairing and data across the rename.
- New APK updater accepts LINKO names and legacy names. A byte-identical legacy
  APK asset is included in the release so already-installed 0.3.5/0.3.6 updaters
  can discover this update. Desktop files use LINKO names only.
- The local build script rejects different Windows/Android release versions.

## Verification

117 core checks and 11 Android JVM tests passed. Windows Release compilation,
single-file publication and four rendered UI pages passed; default pages were
visually reviewed. Android APK label, adaptive icon, versionCode/versionName
and unchanged signing certificate were verified. Android UI changes compiled;
no usable emulator or attached phone was available for visual execution.

The Windows live clipboard self-test could not acquire the clipboard
(`0x800401D0`) before taking its preservation snapshot on two attempts in this
session. It did not run the roundtrip or modify the clipboard. No live clipboard
test pass is claimed for 0.4.0. Existing image recovery unit tests still pass;
real Galaxy/KakaoTalk image delivery remains unverified.
