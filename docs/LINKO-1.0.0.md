# LINKO 1.0.0

Windows EXE and Android APK use version 1.0.0; Android versionCode is 10.
Existing Android package/signing identity, PC settings, Bluetooth UUIDs and
pairing remain compatible. Built locally without GitHub Actions.

## Changed behavior

- Clipboard transfer is text-only. Both receive and send reject images before
  transport/application; image actions and image history rows are removed from
  the interface. Existing historical files are not destructively deleted.
  The UI states: “이미지는 전달되지 않습니다.”
- PC text capture/sync defaults on, including migration from disabled settings.
  Android requests Bluetooth permissions on first launch and connects without
  a separate connection button. Sharing on a cold start now starts discovery.
  Reconnect is attempted after Bluetooth reconnects, reboot and package update
  when permissions already exist. Force-stopped apps still require reopening.
- Mouse speed is 1–300%, adjustable by 1% using slider, numeric entry or arrows.
  Integer fractional accumulation preserves small movements exactly. The input
  sender no longer resumes on the UI context. A high-resolution waitable timer
  replaces coarse delays while preserving negotiated BLE pacing, motion
  coalescing and urgent button/wheel ordering. Radio interference/adapter limits
  remain outside this timing fix; smoothness on the actual phone is unverified.
- Phone-to-PC focused capture handles repeated values after a remote update and
  retries failed durable writes. Samsung keyboard stays unchanged.

## Other Android apps → PC, with Samsung keyboard

Normal Android apps cannot read the background clipboard simply by receiving
Bluetooth/notification permission. LINKO offers an **optional Shizuku mode**:

1. Install Shizuku from its official download page.
2. Start it using wireless debugging (Wi-Fi needed for initial setup), or USB
   debugging with a computer. This implementation uses the shell/ADB mode.
3. In LINKO, open **다른 앱에서도 자동 복사 설정**, enable the switch and approve
   the explanation and Shizuku permission prompt.
4. Keep the existing Samsung keyboard selected. Copy text in another app, then
   paste on the connected PC. Bluetooth text transport remains unchanged.

Shizuku must be restarted after a phone reboot. LINKO reports missing approval,
disconnection and read rejection in this settings page. Disabling the switch
unbinds the reader; revoking Shizuku permission prevents subsequent reads and
enqueueing. Clipboard polling pauses on lock. No default IME change, accessibility
screen scraping, invisible focus-stealing window or root installation is used.

The reader runs as a user-approved Shizuku user service. It exposes only a text
snapshot, not arbitrary shell execution, accepts calls only from the LINKO UID,
and reads that user's default-display clipboard. Images/URIs are not returned.
It uses the Android 12–16 IClipboard signatures (package, attribution, user and
optional device). OEM differences can still cause read denial, which is shown
instead of claiming success. Polling is every 750 ms and can miss intermediate
copies made faster than that. The text snapshot limit is 100,000 UTF-16 units
to stay below Binder limits. Copy timestamps distinguish repeated text; remote
LINKO updates are excluded to prevent echo loops. Clipboard text is not logged.

Without Shizuku, foreground LINKO capture and **Share → LINKO** remain available.
PC → phone automatic text sync does not require Shizuku. The mode does require
phone-side setup; the APK alone cannot grant itself shell access.

References: [Android background clipboard restriction](https://developer.android.com/about/versions/10/privacy/changes#clipboard-data),
[Android clipboard permission enforcement](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android16-release/services/core/java/com/android/server/clipboard/ClipboardService.java),
[Shizuku setup](https://shizuku.rikka.app/guide/setup/),
[Shizuku API/user services](https://github.com/RikkaApps/Shizuku-API).

## Verification and remaining device checks

- 120 core checks passed, including exact 1%/101% movement conservation.
- Windows release checks passed: legacy setting migration, image rejection,
  high-resolution timer execution and cancellation. 30 × 8 ms waits measured
  approximately 248 ms in this session; this is not a Bluetooth latency metric.
- Windows Release build, single EXE publication and four rendered UI tabs passed;
  mouse numeric field and clipboard text-only notice visually reviewed.
- 15 Android JVM tests passed, including new focused/background copy-event
  deduplication, remote echo exclusion and retry after failed persistence.
- Android APK builds locally with the Shizuku API/provider 13.1.5 dependency.
- Live Windows clipboard verification was blocked at the initial preservation
  snapshot by `0x800401D0`. That test did not reach a roundtrip and is not counted
  as a pass. The verification script keeps this check required rather than
  silently skipping it.
- No attached Android device was available (`adb devices` empty). Android UI,
  Shizuku read access on Galaxy S25+, Samsung keyboard retention in actual use,
  end-to-end phone→PC clipboard and mouse smoothness have NOT been verified.

Device acceptance: test multiple Korean/emoji text copies in KakaoTalk/browser,
repeat the same text after changing PC clipboard, PC→phone without echo, F8/F9
repeatedly, 60 seconds of motion/drag/wheel, 1%/101% speed, Bluetooth reconnect,
screen lock/unlock, permission denial/revocation, Shizuku restart and phone reboot.
Confirm no image transfers. iOS remains app-free HID only; see the 0.4.0 record.
