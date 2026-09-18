# Con.Phone 0.2.1 RC regression review

This is a release candidate, not a claim of physical-device validation.

## Code defects addressed

- 0.2.0's hotkey text field competed with the always-on low-level shortcut handler. Recording now enters the same routing engine with higher priority. It consumes repeats as well as key-up. Physical modifier state is tracked from input events; GetAsyncKeyState is not used inside the hook callback to decide chord modifiers.
- Enter/return direction is represented explicitly; a later UI callback does not infer direction from already-modified state. Activation waits for all keys to be released. Modifier key-ups stay with the destination that received key-down.
- Old fixed-width 790/720px nested autosizing and side-navigation docking were replaced with percent-width tables, wrapping text/buttons and vertical scroll areas. CI renders the actual Windows forms at normal/small window sizes and enlarged logical layouts.
- Clipboard calls, hashing, image encoding and storage have moved off UI and input-hook threads into a separate STA message loop. Listener registration is checked. Busy reads are retried and failures are visible. Image restoration creates a persistent native clipboard copy before managed objects are disposed. Text/image readback, retention, exclusion markers and DPAPI round trips are tested on Windows.
- Clipboard history is session-only by default. Optional persistence uses current-user Windows DPAPI. Older 0.2.0 plaintext files are neither silently imported nor deleted. Phone clipboard transfer is NOT implemented and is not advertised as supported.
- Repeated BluetoothLEDevice creation/disposal during polling was removed. Device handles are retained until shutdown. Advertisement status, active session state, host suspend and input readiness are separate. Native notifications remain serialized even after a caller stops waiting. Input events are paced and coalesced. No auto-resume of remote input after a real disconnect.
- The failed keyboard Power experiment has been removed; the 0.1.6 report map is restored. An explicitly enabled Android Meta+L request uses the existing keyboard report. A successful BLE send is not reported as a confirmed phone lock.

## Validation boundaries

Core tests use synthetic keyboard events and mock native task completion. Windows smoke tests use real WinForms controls, OS clipboard and DPAPI with test-only data. UI logical scaling is not equivalent to testing physical monitors at every Windows DPI setting. Physical Bluetooth stability, manufacturer power-saving behavior, phone locking and phone clipboard sharing are not established by CI.

## Public implementation references

- https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener
- https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.clipboard.setdataobject?view=windowsdesktop-10.0
- https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/services/core/java/com/android/server/policy/PhoneWindowManager.java
- https://developer.android.com/about/versions/10/privacy/changes#clipboard-data
