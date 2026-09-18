# Con.Phone Clipboard Bridge Notes

## What the Windows app can do without a phone app

Con.Phone's Bluetooth HID connection is intentionally one-way for keyboard/mouse input. The Windows app can safely keep a local history of Windows clipboard text and images (including screenshots) and restore any saved item back to the Windows clipboard.

## Why phone clipboard contents are different

Bluetooth HID does not provide a channel for the Android host to send arbitrary clipboard bytes back to the HID peripheral. A Ctrl+C key sent to Android tells Android to copy inside Android, but it does not reveal the copied content to Con.Phone.

Therefore Con.Phone must not pretend that it can capture Android clipboard contents through HID alone.

## Optional bridge architecture

If phone-to-PC clipboard synchronization is added later, keep it optional and separate from HID:

1. Keep the current HOGP keyboard/mouse service unchanged.
2. Add a dedicated encrypted GATT data service for clipboard payloads.
3. Use an Android companion app to explicitly send or receive clipboard items.
4. Treat automatic background clipboard reading as restricted on modern Android; default to a visible user action such as "Send current clipboard to PC".
5. Store synchronized history locally and mark the source (Windows / Android).
6. Never send clipboard data to an Internet server.

This separation protects the already-working HID path from clipboard feature regressions and makes the privacy boundary visible to the user.
