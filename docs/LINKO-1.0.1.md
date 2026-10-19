# LINKO 1.0.1 — HID connection recovery

The user reported loss of Galaxy keyboard/mouse input after updating to 1.0.0,
and requested another iOS review. EXE/APK both use 1.0.1 (Android code 11).

## Findings and changes

Instrumenting the existing 1.0.0 startup on this PC showed that input hooks and
HID startup succeeded. When the second, clipboard advertisement started, HID
reported another Aborted → Started transition. No phone HID subscriptions were
observed. This establishes a competing advertisement transition, not definitive
proof of the user's phone-side failure or proof that the phone is repaired.

- Reserve connectable advertising for HID. Publish the clipboard GATT service
  with `IsDiscoverable=true, IsConnectable=false`; Android already scans for
  HID UUID 0x1812 as well as the legacy clipboard UUID and discovers services
  after connecting. Characteristic UUIDs, report descriptor and encryption stay
  unchanged, avoiding an unnecessary HID profile change/re-pair requirement.
- Non-connectable service publication leaves AdvertisementStatus at Created on
  this Windows stack. Track successful publication explicitly; otherwise health
  maintenance calls StartAdvertising repeatedly and Windows throws an illegal
  state exception. Clear the flag after stop and terminal provider events.
- Isolate optional clipboard preparation/start failures from HID startup. Keep
  successful GATT preparation before HID advertising to stabilize the database.
- Do not run automatic transport repair until initial startup finishes. Serialize
  HID initialization and clipboard preparation against concurrent recovery.
- Replace the stranded pause action with **연결 다시 확인**. Starting is still
  automatic; recovery preserves the existing provider and trusted device data.
- Show that phone keyboard/mouse subscriptions are pending, separately from an
  Android app's clipboard connection. Remove the unconditional stale-profile
  re-pair warning because this release does not change the HID report map.
- Write bounded local connection diagnostics to
  `%LOCALAPPDATA%/ConPhone/diagnostics/session.log`. No copied text or key events
  are included. This allows startup failures to be inspected without relying on
  a successful clipboard export or on UI screenshots.

API references: [Microsoft GATT server publication](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-server)
and [Microsoft foreground server sample](https://github.com/microsoft/Windows-universal-samples/blob/main/Samples/BluetoothLE/cs/Scenario3_ServerForeground.xaml.cs).

## iOS review

Both Galaxy and iOS use `BleHidServer`, HID UUID 0x1812 and the same keyboard and
mouse report characteristics. HID readiness depends on the approved phone's
keyboard/mouse subscriptions and suspend state, not Android APK, Shizuku or a
clipboard channel. Startup now preserves that separation too. Apple keyboard
modifier mapping and report sizes pass the existing core tests. No IPA required.

iPhone needs AssistiveTouch enabled for pointer use. In Settings → Accessibility
→ Touch → AssistiveTouch → Devices → Bluetooth Devices, connect this PC. iPad
uses its Bluetooth settings. Both keyboard and mouse subscriptions are required
by LINKO's full-input mode. [Apple pointer setup](https://support.apple.com/en-gb/111775).
No physical iPhone/iPad was connected for end-to-end validation in this session.

## Verification

- 120 core checks passed, including Apple Ctrl/Command mapping, keyboard and
  pointer report sizes, key release ordering, pacing and 1% movement precision.
- Windows release checks passed: optional preparation failure still starts HID;
  HID failure cannot start a clipboard-only advertisement; optional start failure
  does not unwind HID; advertisement roles; existing settings/timer/image checks.
- Android build and 15 JVM tests passed. Android runtime code is unchanged from
  1.0.0; versionCode/versionName are updated to keep the release pair in sync.
- Windows Release build, single EXE and four UI snapshots generated. Connection
  tab visually reviewed. Live installed EXE reports title LINKO 1.0.1 and responds.
- Final local runtime observed: HID=Started, input=RanToCompletion,
  clipboardPublished=True, paused=False. Maintenance no longer emitted the
  repeated publication errors seen while developing this fix.
- At observation time keyboard=0 and mouse=0. Galaxy/iOS actual input, discovery
  of the non-advertised clipboard service, F8/F9 under live input and phone-to-PC
  background clipboard still require device confirmation. Android `adb devices`
  was empty. No successful phone test is claimed.

On the phone, reconnect the existing PC entry and try F8/F9. If subscriptions
still remain zero, inspect the phone Bluetooth input-device state and iPhone
AssistiveTouch setup before deleting any existing pairing. New runtime logs
separate PC startup, subscriptions and input switching to narrow any remaining
failure without another speculative release.
