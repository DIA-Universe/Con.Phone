# Con.Phone 0.3.3 clipboard reconnect fixes

The running 0.3.2 app showed HID keyboard/mouse subscribed but no companion clipboard channel. Its history contained a previous development clipboard self-test marker; a newer image was captured but could not be sent because no transport was connected. This explains stale phone content without proving every reported copy failure has the same cause.

- Android retries discovery five seconds after a fifteen-second scan expires, instead of stopping discovery permanently. Existing connection and handshake watchdogs remain active.
- Reapproving or selecting the already active phone no longer cancels clipboard transfers, closes P2P and rotates its credentials. Selecting a different phone still resets the channel.
- Startup automatic transmission now waits for a capture of the actual system clipboard instead of using the newest saved history item.
- The Windows clipboard self-test restores the original clipboard formats after completion or failure instead of leaving its test marker behind.

Local verification: Core 110 checks, Windows Release build with zero warnings/errors, real clipboard self-test, four UI renders, single-file publish and published clipboard self-test passed. Android nine protocol tests passed; APK signature matches 0.3.2. Builds ran locally without GitHub Actions.

Install the 0.3.3 APK over the existing app. Open Companion and select the paired PC to reconnect. The PC clipboard tab must show a connected channel before testing new text in both directions. Phone → PC requires reopening Companion or Share → Con.Phone because of Android clipboard access restrictions.

Physical Galaxy S25+ clipboard acceptance after this APK update is not yet verified. HID behavior is unchanged in this release.
