# 0.3.6 image BLE delivery

Windows and Android are both 0.3.6. Install the companion APK update to use
Windows' new image path. Text and HID retain their existing transports.

PC image notifications use BLE fragment version 2: the existing 10-byte
header and payload, with byte 1 set to 2. Android replies through the trusted
RX write characteristic with the first eight bytes (C7 02, little-endian
transport ID and fragment index). Windows registers the waiter before sending,
matches both peer and receipt, and permits four attempts per fragment, two
seconds per receipt. Only one image fragment is outstanding at a time.

Android validates and consumes the packet before sending its receipt. It
caches the last successfully consumed packet and its optional application ACK,
so a lost receipt can be retried without reassembly replay rejection or a
second clipboard application. The final image ACK still means clipboard
application succeeded. If it is lost, Windows retries the final fragment up
to twice to retrieve the cached ACK. Cancellation and active-device checks
continue to stop obsolete sends. Partial Android reassembly expires after
30 seconds without progress, rather than 30 seconds since creation.

Local verification:

- 117 core checks, including notification loss, receipt loss, wrong-peer
  receipt, bounded retry exhaustion, and cancellation.
- 11 Android JVM tests. The reliable receiver test uses a synthetic 6,742-byte
  PNG encoded by the Windows core at GATT sizes 182 and 512; it injects a
  missing notification and duplicates every packet to model lost receipts,
  including the last application ACK. Result bytes equal the source and the
  image is consumed exactly once. Receiver processing failures are not cached
  or acknowledged as success.
- Windows Release build, real clipboard self-test, four UI snapshots,
  single-EXE publish, and published EXE clipboard self-test.
- Android debug APK built locally, versionCode 8, same debug signer as 0.3.5.
- Optional native injected-key self-test did not pass in this desktop session:
  new build reported lock/return timeout; installed 0.3.4 baseline also failed
  (F9 hook timeout). This is unresolved test evidence, not a passed HID check.
  HID implementation files are unchanged in this release.

The source PNG and Windows-generated version-1 fragments are committed under
Android test resources; the test changes only the BLE version byte to exercise
the version-2 wrapper. No private clipboard content is in the fixtures.

Not verified: physical Galaxy S25+ Bluetooth delivery and KakaoTalk pasting.
This is a tested recovery mechanism for packet/ACK loss, not proof that packet
loss caused the user's original timeout. Large-image P2P and iOS were not
changed or newly tested. Release remains a preview.
