# Windows 0.3.4 clipboard transfer fixes

The running 0.3.3 app retried a 5.8 KiB image every two minutes without an application ACK. HID keyboard/mouse subscriptions were absent. Windows changes in this release:

- Images up to 64 KiB use BLE directly without creating/waiting for a Wi-Fi Direct group. Existing P2P connections remain preferred.
- Each BLE notification has a five-second timeout; after all bytes are sent, application ACK wait is limited to eight seconds. P2P sends have their own eight-second deadline so a stalled socket can fall back to BLE.
- Automatic sends wait for the companion to read its active-target capabilities, with a brief grace period for the phone callback. Merely subscribing to notifications no longer means the receiver is ready. Logs distinguish credential read, active-target read, bytes sent and final application ACK.
- A new local copy cancels the older automatic transfer. Failed automatic sends back off and stop after three attempts until another copy; manual send remains available.
- Identical remote content is only suppressed while the actual Windows clipboard sequence still matches its last application. An old duplicate ACK cannot mark unrelated new local content as sent.

Validation: Windows Release build, Core 110 checks, real clipboard self-test (including remote A → local B → remote A regression), four UI renders, and published executable clipboard validation. The earlier candidate's native input self-test passed. No changes to the HID report map or Android wire protocol. Existing Companion 0.3.3 is compatible; no new APK is required for these Windows fixes.

Live investigation reached successful Bluetooth notification completion for 5,975 bytes, but the phone did not return the application ACK. This does not establish successful phone clipboard application. HID reconnection and end-to-end phone clipboard acceptance still require the phone's connection state/error to be checked. Do not present the whole device issue as resolved on the basis of unit/build tests.

Built locally without GitHub Actions. Logs: artifacts/fix-034/windows-verify-final.log.
