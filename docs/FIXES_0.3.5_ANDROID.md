# Companion 0.3.5 — received images and in-app APK downloads

Windows remains at 0.3.4; this release changes Android only. The reported remaining case is PC capture → Galaxy S25+ KakaoTalk.

- Received images use immutable, unique `.png` content URIs backed by app files instead of repeatedly overwriting one `/latest` cache URI. MIME stream negotiation and typed asset reads are exposed along with filename, size and MIME metadata. The provider remains private and read-only, with temporary URI grants.
- Companion offers **받은 이미지 다시 복사** and **받은 이미지 공유**. Sharing uses Android's chooser, `image/png`, `EXTRA_STREAM`, ClipData and temporary read permission. The user chooses KakaoTalk and the recipient; the app never sends a chat message automatically. KakaoTalk's support for direct Ctrl+V image paste is outside Companion's control.
- The update button opens an in-app updater. It finds the first release's APK (including preview releases), downloads it, and verifies package identity, signer and version before offering Android's installer. Private repository authentication uses the user's GitHub session in an HTTPS WebView. No account token is embedded. GitHub cookies are never forwarded to asset CDN hosts. The user may need to log in and permit installation from Companion.
- APK versionCode 7 / versionName 0.3.5, existing signing certificate retained.

Validation: Android build and nine protocol unit tests passed; dedicated image-provider instrumentation APK compiled. The instrumentation test covers unique URI identity, MIME negotiation, typed image bytes, filename/size, clipboard MIME and latest-image persistence. GitHub release-page discovery was checked against current GitHub release markup. APK package/signature verified locally. Full private GitHub login/download/install flow and Galaxy KakaoTalk paste/share acceptance still require device testing; do not equate a successful APK build with those end-to-end checks.

Built locally, no GitHub Actions required. Android download naming now follows its own version even when Windows has a different version.

Usage: update the APK, copy a new image on the PC, then paste on the phone. If KakaoTalk does not accept the image clipboard, open Companion → **받은 이미지 공유** → KakaoTalk. Future update checks/downloads happen inside Companion rather than handing the user a browser link.
