# 0.3.0 UI preview

Windows now uses four familiar navigation tabs: phone connection, input settings,
clipboard, and help/diagnostics. The connection page guides pairing and device
selection. Device rows show human-readable readiness rather than protocol flags.
A persistent footer exposes input switching and emergency PC return; the latter
uses the critical action queue so it is not dropped while a normal action runs.
Clipboard actions are disabled until an item is selected, and action feedback is
visible without opening the diagnostic log. Cards adapt to the window width.

Android uses scrollable connection/send/settings cards, consistent blue primary
buttons and system-bar insets. A fresh launch explains permissions before asking;
explicit share intents continue to start the existing flow. Connection requests
are labelled as requests rather than claiming a successful connection.

The UI preview is built locally and uploaded directly as GitHub prerelease
assets; GitHub Actions is not needed for downloads. Source commits use `[skip ci]`
for this release. Windows remains a self-contained x64 EXE; Android is a debug APK
for Android 12+. iOS is not part of this UI refresh.

Validation uses the existing Windows/Core/Android build scripts. Windows snapshots
are checked at 1060×860 and 900×720. The snapshot mode also accepts a width and
height after its output directory. Snapshot device state is illustrative only.

Real-device Bluetooth/P2P, share-menu interoperability and iOS validation remain
separate. The earlier intermittent clipboard self-test failure is not claimed
fixed by a UI change; ten consecutive self-tests passed during this refresh.
