# Con.Phone 0.3.2 — repeat switching and companion connection

Fixes for the reported Galaxy S25+ F8 → F9 → F8 failure and unavailable clipboard sync.

## Changes

- Windows registers independent global shortcuts for switching, return, next device and lock/return. Local return and activation restore the low-level hooks on their owning thread and clear stale released-key suppression. Input actions use their own queue; background peer enumeration no longer blocks that queue and device lookup has a timeout.
- Settings loading now uses the same enum converter as saving. Previously enum strings caused loading to silently fall back to defaults, including clipboard sync off.
- BLE credential, capabilities and direct-link reads honor the read offset, allowing long reads at small MTUs.
- Android can connect directly to a paired PC and remembers the selected/authenticated PC. A bounded scan shows instructions when discovery cannot find the service. Authentication failures show the PC approval/sync checks.
- Android shows the installed version and an update/download link to the repository releases page. The page includes preview releases; private repository access requires the user's GitHub login. No GitHub token is embedded in the APK.
- Windows/Android version 0.3.2; Android versionCode 5. Existing APK signing identity retained.

## Local verification, 2026-09-20

- Core: 110 checks passed. Android: 9 protocol tests passed, APK assembled and signature verified.
- Windows: Release build with zero warnings/errors, text/image clipboard roundtrip, four UI renders, x64 single-file executable validation and published clipboard self-test passed.
- Published input self-test passed: settings serialization, long-read continuation, F9, Ctrl+F9, 20 cycles of F8/F9/F8/lock-return through actual Windows hooks with synthesized keys, and independent F9 WM_HOTKEY delivery with hooks removed.
- Before the fix, the running UI reproduced F9 returning locally followed by F8 producing no new input transition. Its clipboard status showed companion connection pending. These observations do not prove a single cause for every reported symptom.
- The new APK has not been installed on the Galaxy by this development session. End-to-end phone clipboard and physical phone lock/unlock acceptance remain unverified. Android background clipboard restrictions still apply: reopen Companion after copying on the phone or use Share → Con.Phone.
- Built locally; no GitHub Actions run requested.

## Install and acceptance

1. Run the updated Windows EXE and update the existing Android app with the 0.3.2 APK.
2. In Android, select **페어링된 PC 직접 선택** and choose this PC. Keep existing Bluetooth pairing. On Windows, ensure the phone is the approved target and clipboard sync is enabled.
3. Check the companion channel becomes connected, then copy fresh text PC → phone. Copy on phone, reopen Companion, then paste on PC.
4. Repeat F8 → F9 → F8 and Ctrl+Alt+L → unlock → F8 several times.
5. Future APK downloads are available through **최신 업데이트 확인 · APK 다운로드** in the app; sign in to GitHub if asked.

## SHA-256

- ConPhone.exe: `5A6C1D3F5F431158AA848F1ED80DDF123EB15D38624D972B22B0D6FAC25A3B15`
- ConPhone-Companion-0.3.2-debug.apk: `46CCACC3ADBC773BD199EDD1C01B312C39EC606689B22C5B73C493AFF1D6D00A`

Build/test logs: `artifacts/fix-032`. This update does not include an iOS IPA.
