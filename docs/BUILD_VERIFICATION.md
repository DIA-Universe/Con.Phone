# 0.3.0 build verification

## Reproduce

Windows: install .NET SDK 10, then run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-now.ps1 -Target windows
```

This runs 103 Core assertions, a Release build, the real Windows clipboard self-test (including Korean text, replay/self-notification suppression and truncated PNG rejection), four preview snapshots, and a self-contained x64 single-file publish. It validates the PE header and executes the published EXE's self-test with a 30-second limit. Native command failures stop the script. Self-test history uses a unique temporary directory; the test temporarily writes the system clipboard, so avoid copying while it runs. UI snapshots use preview data and do not establish Bluetooth connectivity.

Android: JDK 17 and Android SDK platform/build-tools 35 are required. Set `JAVA_HOME` and `ANDROID_HOME`, then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-now.ps1 -Target android
```

The checked-in Gradle 8.11.1 Wrapper verifies the distribution SHA-256. On Windows, non-ASCII source paths are staged into a fresh ASCII directory under LocalAppData. The script copies the APK back to both `companion/android/app/build/outputs/apk/debug/app-debug.apk` and `artifacts/test-now/ConPhone-Companion-0.3.0-debug.apk`, writes its checksum and retains JUnit XML under `artifacts/test-now/android-test-results`.

For direct Gradle execution, use an ASCII checkout path and run inside `companion/android`:

```powershell
.\gradlew.bat testDebugUnitTest
.\gradlew.bat assembleDebug
```

On Linux/macOS use `./gradlew`. There are nine Android protocol unit tests. Inspect the APK with Android SDK `aapt dump badging` and `apksigner verify`.

## GitHub Actions blocker, 2026-09-20

All three original runs were rejected before steps started:

| Platform | Run |
| --- | --- |
| Windows | https://github.com/DIA-Universe/Con.Phone/actions/runs/35483710675 |
| Android | https://github.com/DIA-Universe/Con.Phone/actions/runs/35483710646 |
| iOS | https://github.com/DIA-Universe/Con.Phone/actions/runs/35483710683 |

Exact annotation: “The job was not started because recent account payments have failed or your spending limit needs to be increased. Please check the 'Billing & plans' section in your settings”. Jobs have zero steps; Windows runner name is empty. Repository Actions permissions are enabled with `allowed_actions=all`. The organization budgets API reports an Actions product budget of **0**, with **prevent_further_usage=true**. This confirms a billing/budget gate; the annotation alone does not prove a failed payment. No billing settings were changed.

An organization owner/billing manager should open **DIA-Universe → Settings → Billing & licensing → Budgets and alerts**, edit the Actions budget and choose an authorized budget/stop-usage policy. If the account still reports failed payments, inspect its payment information. See https://docs.github.com/en/billing/how-tos/set-up-budgets. Code changes cannot remove this gate.

After resolving billing, dispatch the branch workflows:

```sh
gh workflow run build.yml --repo DIA-Universe/Con.Phone --ref feature/clipboard-text-image-sync
gh workflow run android.yml --repo DIA-Universe/Con.Phone --ref feature/clipboard-text-image-sync
gh workflow run ios.yml --repo DIA-Universe/Con.Phone --ref feature/clipboard-text-image-sync
gh run list --repo DIA-Universe/Con.Phone --branch feature/clipboard-text-image-sync --limit 12
```

All workflows run for feature-branch pushes and PR `synchronize`. Mobile path filters were removed so shared protocol/Windows changes cannot bypass mobile builds. Concurrency groups by workflow, source repository and source branch, cancelling duplicate push/PR runs and older commits while keeping fork branches separate. Windows retains safe extraction to `RUNNER_TEMP/conphone-src`, never a `CON.*` directory. Its local and CI build use the same verification script. Action major-version refs were resolved through GitHub and all workflows passed actionlint 1.7.12.

Expected artifacts: `ConPhone-win-x64-single-exe`, `ConPhone-win-x64-sha256`, `ConPhone-ui-snapshots`, `ConPhone-Companion-android-debug`. iOS additionally uploads `ConPhone-Companion-ios-simulator-unsigned` and test results after successful execution. There are no successful new hosted builds while the billing gate remains.

## Verification boundary

| Behavior | Executed automatically | Still required |
| --- | --- | --- |
| Korean UTF-8, chunk reassembly/out-of-order, conflicting duplicates, BLE MTU, transfer replay | Core + Android protocol tests | Phone interoperability |
| ACK encoding and BLE fragmentation | Core + Android protocol tests | End-to-end ACK completion, timeout/retry under real connection loss |
| Bad image signatures, declared size bounds | Core + Android protocol tests | Platform decoder coverage on phones |
| Truncated PNG, Korean clipboard, duplicate remote application/self-notification | Real Windows self-test | Cross-device loop prevention |
| P2P preference / BLE size restrictions | Core route-selection assertions | Actual Direct Wi-Fi failure and BLE fallback |
| Late inactive-device input | Source inspection only | Device switching during an in-flight transfer |
| Android share menu | Compiles and APK is produced | Share text/image from another app on a device |
| iOS Share Extension | Source/project configuration only | Xcode build, embedded extension check, simulator tests and iPhone sharing |

iOS was not run locally: this host is Windows. The workflow runs XcodeGen, simulator unit tests and a simulator Debug build, and checks `ConPhoneShare.appex` is embedded. A simulator `.app` is not an installable iPhone IPA. Device installation needs Apple signing/provisioning.

Two Windows self-test runs (published EXE and dotnet run) failed intermittently with `A real re-copy after self-write suppression was incorrectly hidden.` Five subsequent published-EXE runs, eight diagnostic dotnet runs and complete reruns passed. Failure diagnostics now include event counts and whether the clipboard still matches, without logging clipboard text. The intermittent failure's cause is not established; do not treat the successful reruns as proof it is fixed. Keep this as a reliability follow-up alongside device testing.

## Minimum device test

1. Run the generated Windows EXE. Install the debug APK on an Android 12+ test device and start Companion.
2. Enable Bluetooth, pair the intended PC/phone and register/activate it in Con.Phone. Confirm actual HID subscriptions.
3. Copy `안녕하세요 🙂 ABC 123` on the PC, switch to the phone and paste. Share text/image from a phone app to Con.Phone and paste on Windows.
4. Test a large text and an image, disconnect the P2P link to exercise BLE fallback within the configured image limit, and interrupt a transfer to test ACK timeout/retry.
5. Switch the active device while a transfer is pending; verify the previous device cannot overwrite the new target's clipboard and no echo loop occurs.

Use `docs/RELEASE_TEST_0.3.0.md` for the full device checklist. Local build success does not establish Galaxy/iPhone release compatibility.
