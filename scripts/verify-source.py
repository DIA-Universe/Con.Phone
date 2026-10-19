#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
errors: list[str] = []

def read(path: str) -> str:
    p = ROOT / path
    if not p.exists():
        errors.append(f"missing required file: {path}")
        return ""
    return p.read_text(encoding="utf-8")

def require(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)

win_settings = read("src/ConPhone.Windows/AppSettings.cs")
win_ble = read("src/ConPhone.Windows/BleClipboardService.cs")
win_p2p = read("src/ConPhone.Windows/P2PClipboardServer.cs")
win_direct = read("src/ConPhone.Windows/DirectClipboardLink.cs")
core = read("src/ConPhone.Core/ClipboardSync.cs")
win_project = read("src/ConPhone.Windows/ConPhone.Windows.csproj")

android_manifest = read("companion/android/app/src/main/AndroidManifest.xml")
android_service = read("companion/android/app/src/main/java/universe/dia/conphone/ClipboardBridgeService.kt")
android_protocol = read("companion/android/app/src/main/java/universe/dia/conphone/Protocol.kt")
android_gradle = read("companion/android/app/build.gradle.kts")
android_main = read("companion/android/app/src/main/java/universe/dia/conphone/MainActivity.kt")
android_share = read("companion/android/app/src/main/java/universe/dia/conphone/ShareActivity.kt")
android_share_store = read("companion/android/app/src/main/java/universe/dia/conphone/AndroidSharedPayloadStore.kt")
android_protocol_test = read("companion/android/app/src/test/java/universe/dia/conphone/ProtocolTest.kt")
android_p2p_security = read("companion/android/app/src/main/java/universe/dia/conphone/P2PSecurity.kt")
android_workflow = read(".github/workflows/android.yml")

ios_bridge = read("companion/ios/ConPhoneCompanion/ClipboardBridge.swift")
ios_protocol = read("companion/ios/ConPhoneCompanion/ClipboardProtocol.swift")
ios_p2p = read("companion/ios/ConPhoneCompanion/P2PClipboardChannel.swift")
ios_project = read("companion/ios/project.yml")
ios_app_entitlements = read("companion/ios/ConPhoneCompanion/ConPhoneCompanion.entitlements")
ios_share_entitlements = read("companion/ios/ShareExtension/ConPhoneShare.entitlements")
ios_share = read("companion/ios/ShareExtension/ShareViewController.swift")
ios_share_store = read("companion/ios/Shared/SharedPayloadStore.swift")
ios_protocol_test = read("companion/ios/Tests/ClipboardProtocolTests.swift")
ios_p2p_security = read("companion/ios/ConPhoneCompanion/P2PSecurity.swift")
ios_workflow = read(".github/workflows/ios.yml")

trusted_store = read("src/ConPhone.Windows/TrustedDeviceStore.cs")
clipboard_history = read("src/ConPhone.Windows/ClipboardHistoryService.cs")
direct_link = read("src/ConPhone.Windows/DirectClipboardLink.cs")
update_service = read("src/ConPhone.Windows/LocalUpdateService.cs")
authenticode = read("src/ConPhone.Windows/AuthenticodeVerifier.cs")
p2p_security = read("src/ConPhone.Core/P2PSecurity.cs")

# Released Windows and Android versions must agree. The archived iOS companion
# is versioned separately; iPhone HID works without installing that companion.
win_version = re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", win_project)
android_version = re.search(r'versionName = "(\d+\.\d+\.\d+)"', android_gradle)
require(win_version is not None and android_version is not None and
        win_version.group(1) == android_version.group(1), "Windows/Android version mismatch")
require('MARKETING_VERSION: "0.3.0"' in ios_project, "iOS marketing version must be 0.3.0")

# There must be no cloud relay/Railway dependency in product source.
for path, text in [
    ("AppSettings.cs", win_settings),
    ("BleClipboardService.cs", win_ble),
    ("ClipboardSync.cs", core),
    ("Android service", android_service),
    ("iOS bridge", ios_bridge),
]:
    require("Railway" not in text, f"{path}: Railway reference is not allowed")
    require("ClipboardRelay" not in text and "RelayClipboard" not in text,
            f"{path}: internet relay reference is not allowed")

# Shared-LAN discovery must not return.
for path, text in [
    ("Windows P2P", win_p2p),
    ("Android service", android_service),
    ("iOS P2P", ios_p2p),
]:
    require("CONPHONE_DISCOVER_V1" not in text, f"{path}: shared-LAN discovery returned")
    require("DiscoveryPort" not in text, f"{path}: UDP discovery port returned")

# Wi-Fi Direct publisher status values must come from the Wi-Fi Direct enum only.
require("StartedWithoutAllAdvertisementData" not in win_direct,
        "Windows P2P uses an invalid GATT-only publisher status value")

# Clipboard OFF must close the Windows P2P listener and allow later restart.
require("StopListeningAsync" in win_p2p,
        "Windows P2P server must support listener shutdown/restart")
coordinator = read("src/ConPhone.Windows/ClipboardSyncCoordinator.cs")
require("await _p2p.StopListeningAsync()" in coordinator,
        "Clipboard disable path must close the P2P TCP listener")
require("_started" not in coordinator and "StartCoreAsync" in coordinator,
        "Clipboard coordinator must restart P2P after disable/re-enable")
require("_lifecycle" in coordinator,
        "Clipboard transport start/stop must be lifecycle-serialized")
require("_shutdown.Token" in coordinator and "sendToken" in coordinator,
        "Clipboard sends must link to coordinator shutdown cancellation")
require("content,\n                        sendToken," in coordinator,
        "BLE fallback must use coordinator shutdown cancellation")
require("_send.Dispose()" not in coordinator and "_lifecycle.Dispose()" not in coordinator and
        "_shutdown.Dispose()" not in coordinator,
        "Clipboard coordinator async primitives must not be disposed while callers unwind")

# P2P protocol v2 must match on all platforms.
for path, text in [
    ("Windows P2P", win_p2p),
    ("Android service", android_service),
    ("iOS P2P", ios_p2p),
]:
    require("CPHELLO2" in text or "P2PSecurity.HelloMagic" in text,
            f"{path}: P2P v2 hello missing")
    require("CPREADY2" in text or "P2PSecurity.ReadyMagic" in text,
            f"{path}: P2P v2 ready missing")
    require("ConPhone-Clipboard-P2P-v2" in text or "P2PSecurity.AadPrefix" in text,
            f"{path}: P2P v2 AAD missing")

# P2P v2 KDF must be centralized and pinned to the same Windows vector on mobile.
require("deriveSessionKey" in android_p2p_security and
        "ConPhone-P2P-Session-v2" in android_p2p_security,
        "Android P2P v2 KDF helper missing")
require("P2PSecurity.deriveSessionKey" in android_service,
        "Android P2P channel must use centralized KDF")
require("p2pSessionKeyMatchesWindowsVector" in android_protocol_test and
        "16BE739CCB5056785C93854CFD3C384105BE7F799292A5BEBA8FFE2BE73248D0" in android_protocol_test,
        "Android P2P KDF vector test missing")

require("deriveSessionKey" in ios_p2p_security and
        "ConPhone-P2P-Session-v2" in ios_p2p_security,
        "iOS P2P v2 KDF helper missing")
require("P2PSecurity.deriveSessionKey" in ios_p2p,
        "iOS P2P channel must use centralized KDF")
require("testP2PSessionKeyMatchesWindowsVector" in ios_protocol_test and
        "16BE739CCB5056785C93854CFD3C384105BE7F799292A5BEBA8FFE2BE73248D0" in ios_protocol_test,
        "iOS P2P KDF vector test missing")

# Clipboard safety constants aligned.
require("MaxTextBytes = 1024 * 1024" in core, "Core text max must be 1 MiB")
require("MaxImageBytes = 32 * 1024 * 1024" in core, "Core image max must be 32 MiB")
require("MAX_TEXT = 1024 * 1024" in android_protocol, "Android text max must be 1 MiB")
require("MAX_IMAGE = 32 * 1024 * 1024" in android_protocol, "Android image max must be 32 MiB")
require("static let maxText = 1024 * 1024" in ios_protocol, "iOS text max must be 1 MiB")
require("static let maxImage = 32 * 1024 * 1024" in ios_protocol, "iOS image max must be 32 MiB")

# BLE service/characteristic UUIDs must agree across desktop + companions.
uuids = [
    "67A7F001-7E5B-4A2E-9B8B-1C9A5B6D1001",
    "67A7F002-7E5B-4A2E-9B8B-1C9A5B6D1001",
    "67A7F003-7E5B-4A2E-9B8B-1C9A5B6D1001",
    "67A7F004-7E5B-4A2E-9B8B-1C9A5B6D1001",
    "67A7F005-7E5B-4A2E-9B8B-1C9A5B6D1001",
    "67A7F006-7E5B-4A2E-9B8B-1C9A5B6D1001",
]
for uuid in uuids:
    for path, text in [
        ("Windows BLE", win_ble),
        ("Android BLE", android_service),
        ("iOS BLE", ios_bridge),
    ]:
        require(uuid.lower() in text.lower(), f"{path}: missing UUID {uuid}")

# Android connects only to the selected bonded PC; it no longer scans HID peers.
require("AutoConnectPolicy.canConnect" in android_service and "adapter.bondedDevices" in android_service and
        "startScan(" not in android_service and "BLUETOOTH_SCAN" not in android_manifest,
        "Android must use the selected paired PC without HID scanning")
require('CBUUID(string: "1812")' in ios_bridge,
        "iOS HID-service discovery fallback missing")
require("rejectedBleDevices" in android_service,
        "Android unrelated-HID rejection cache missing")
require("rejectedPeripheralUntil" in ios_bridge,
        "iOS unrelated-HID rejection cache missing")

# Application ACK must exist on every platform.
require("ClipboardAckCodec" in win_ble, "Windows BLE application ACK missing")
require("decodeAck" in android_service and "encodeAck" in android_protocol,
        "Android BLE application ACK missing")
require("decodeAck" in ios_bridge and "encodeAck" in ios_protocol,
        "iOS BLE application ACK missing")
require("CAPABILITIES_UUID" in android_service and
        "parseActiveTarget" in android_service and
        "parseBleImageLimit" in android_service,
        "Android capabilities/active-target/BLE-limit negotiation missing")
require("capabilitiesUUID" in ios_bridge and
        "parseActiveTarget" in ios_bridge and
        "parseBleImageLimit" in ios_bridge,
        "iOS capabilities/active-target/BLE-limit negotiation missing")
require("response[9]" in win_ble and "_p2pRequested()" in win_ble,
        "Windows BLE capabilities must expose explicit P2P-demand state")
require("parseP2PRequested" in android_service and "pendingNeedsP2P" in android_service,
        "Android must only probe DirectLink for actual P2P demand")
require("parseP2PRequested" in ios_bridge and "pendingNeedsP2P" in ios_bridge,
        "iOS must only probe DirectLink for actual P2P demand")
require("bulkTransfer && !_p2p.HasClient(id)" in coordinator and "_direct.Stop()" in coordinator,
        "Speculative Windows P2P must stop after successful BLE fallback")

# Android foreground service / optional P2P permission model.
require('android:foregroundServiceType="connectedDevice"' in android_manifest,
        "Android service must be connectedDevice foreground type")
require("FOREGROUND_SERVICE_DATA_SYNC" not in android_manifest,
        "Android dataSync FGS permission should not be required")
require("NEARBY_WIFI_DEVICES" in android_manifest,
        "Android P2P permission declaration missing")
require("private fun hasBluetoothPermissions()" in android_main,
        "Android Bluetooth permission gate missing")
bluetooth_gate = re.search(
    r"private fun hasBluetoothPermissions\(\): Boolean =(?P<body>.*?)(?:\n\s*private fun|\Z)",
    android_main,
    re.S)
require(bluetooth_gate is not None,
        "Android Bluetooth permission gate body could not be parsed")
if bluetooth_gate is not None:
    require("NEARBY_WIFI_DEVICES" not in bluetooth_gate.group("body"),
            "Android BLE transport must not require Nearby Wi-Fi permission")
require("BLUETOOTH_SCAN" not in android_share and
        "checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)" in android_share,
        "Android Share target must be able to dispatch with Bluetooth-only permission")

# Android protocol must have executable JVM unit coverage.
require('testImplementation("junit:junit:4.13.2")' in android_gradle,
        "Android protocol JUnit dependency missing")
for marker in [
    "textRoundTripOutOfOrder",
    "completedTransferReplayRejected",
    "conflictingDuplicateChunkRejected",
    "bleFragmentRoundTripOutOfOrder",
]:
    require(marker in android_protocol_test,
            f"Android protocol test missing: {marker}")

require(":app:testDebugUnitTest" in android_workflow,
        "Android workflow must execute protocol unit tests before APK build")

# iOS Share Extension must share the same App Group.
app_group = "group.universe.dia.conphone"
require(app_group in ios_app_entitlements, "iOS app App Group missing")
require(app_group in ios_share_entitlements, "iOS Share Extension App Group missing")
require("ConPhoneShare:" in ios_project, "iOS Share Extension target missing")
require("com.apple.developer.networking.HotspotConfiguration" in ios_app_entitlements,
        "iOS Hotspot Configuration entitlement missing")

# Durable phone -> PC Share queues must exist and only drain through the Companion transport.
require("AndroidSharedPayloadStore" in android_share, "Android Share target must use the durable payload store")
require("ACTION_DRAIN_SHARED" in android_service, "Android durable Share queue drain action missing")
require("ACTION_RETRY_P2P" in android_service and "ACTION_RETRY_P2P" in android_main,
        "Android P2P permission retry path missing")
require("MAX_ITEMS = 8" in android_share_store, "Android durable Share queue item cap must be 8")
require("MAX_TOTAL_BYTES = 40L * 1024 * 1024" in android_share_store,
        "Android durable Share queue total cap must be 40 MiB")

require("ShareViewController" in ios_share, "iOS Share Extension controller missing")
require("maxQueuedItems = 8" in ios_share_store, "iOS durable Share queue item cap must be 8")
require("maxQueuedImageBytes = 40 * 1024 * 1024" in ios_share_store,
        "iOS durable Share queue image cap must be 40 MiB")
require("sendPendingShareIfPresent" in ios_bridge,
        "iOS durable Share queue drain path missing")

# Bluetooth recovery cadence must stay separate from the heavier peer refresh.
main_form = read("src/ConPhone.Windows/MainForm.cs")
require("_healthTimer = new() { Interval = 1500 }" in main_form,
        "Windows HID/clipboard health timer must run every 1.5 seconds")
require("_peerTimer = new() { Interval = 5000 }" in main_form,
        "Windows peer refresh must remain at 5 seconds")
require("await _ble.EnsureAdvertisingAsync()" in main_form and
        "await _clipboardSync.EnsureHealthyAsync()" in main_form,
        "Windows periodic HID/clipboard recovery hooks missing")

# Android's shipping share surface is text-only; image codecs remain tested separately.
require("saveText(text)" in android_share and "saveImage(" not in android_share and
        'android:mimeType="image/' not in android_manifest,
        "Android Share target must persist text and not advertise unsupported image sharing")
require("NEARBY_WIFI_DEVICES" not in android_share,
        "Android Share target must not require Nearby Wi-Fi permission for BLE fallback")

# iOS protocol must have executable XCTest coverage.
require("ConPhoneProtocolTests:" in ios_project,
        "iOS protocol unit-test target missing")
for marker in [
    "testTextRoundTripOutOfOrder",
    "testCompletedTransferReplayRejected",
    "testConflictingDuplicateChunkRejected",
    "testBleFragmentRoundTripOutOfOrder",
]:
    require(marker in ios_protocol_test,
            f"iOS protocol test missing: {marker}")
require("iOS protocol unit tests" in ios_workflow and
        "xcodebuild" in ios_workflow and
        " test" in ios_workflow,
        "iOS workflow must execute protocol unit tests")

# Multi-device and active-target isolation.
require("MaxDevices = 5" in trusted_store, "Trusted device cap must remain 5")
require("IsActive(" in win_ble and "IsTrusted(" in win_ble,
        "Windows BLE clipboard trust/active-target separation missing")
require("activeTarget" in android_service, "Android active-target gate missing")
require("activeTarget" in ios_bridge, "iOS active-target gate missing")

# P2P v2 must use a per-session derived key, not the long-term pairing secret directly.
require("DeriveSessionKey" in p2p_security and "SessionLabel" in p2p_security,
        "Core P2P v2 session-key derivation missing")
require("P2PSecurity.DeriveSessionKey" in win_p2p,
        "Windows P2P server must use the shared v2 KDF")

# Windows capture and P2P lifecycle hardening.
require("ClipboardContent.MaxTextBytes" in clipboard_history and
        "ClipboardContent.MaxImageBytes" in clipboard_history and
        "EnsureSafeImageDimensions" in clipboard_history,
        "Windows local clipboard capture must enforce protocol/image safety caps")
require("private readonly object _gate = new();" in direct_link and
        "StartUnsafe" in direct_link and
        "StopUnsafe" in direct_link,
        "Windows P2P publisher start/stop/rotation must be serialized")
require("_sendGate.Dispose()" not in win_ble,
        "BLE clipboard send gate must not be disposed while async sends unwind")
require("_send.Dispose()" not in win_p2p,
        "P2P connection send gate must not be disposed during concurrent close")

# Update integrity/signing path.
require("ChecksumVerified" in update_service, "Windows update checksum verification missing")
require("Authenticode" in update_service and "AuthenticodeVerifier" in authenticode,
        "Windows update Authenticode verification path missing")

# Product intentionally has no file/folder clipboard transfer.
for path, text in [
    ("Core clipboard", core),
    ("Android protocol", android_protocol),
    ("iOS protocol", ios_protocol),
]:
    lower = text.lower()
    require("clipboardcontentkind.file" not in lower and "kind.file" not in lower,
            f"{path}: file clipboard content is not allowed")

if errors:
    print("SOURCE SANITY FAILED")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print("SOURCE SANITY PASSED")
print(f" - Windows/Android version alignment: {win_version.group(1) if win_version else 'missing'}")
print(" - transport: Direct Wi-Fi/P2P -> BLE only")
print(" - no Railway/internet relay")
print(" - P2P listener lifecycle guarded")
print(" - P2P v2 security markers aligned")
print(" - clipboard caps aligned (1 MiB text / 32 MiB image)")
print(" - BLE UUIDs aligned across Windows/Android/iOS")
print(" - Android selected-PC connection / legacy iOS discovery guarded")
print(" - application ACK paths present")
print(" - capabilities UUID/active-target/BLE-limit negotiation guarded")
print(" - Android BLE fallback remains independent of P2P permission")
print(" - Android connectedDevice foreground service")
print(" - iOS App Group + Share Extension aligned")
print(" - durable Android/iOS Share queues guarded")
print(" - active-target isolation + 5-device trust guarded")
print(" - Windows local clipboard/image safety caps guarded")
print(" - P2P/BLE async shutdown races guarded")
print(" - P2P v2 per-session KDF guarded")
print(" - update checksum/AuthentiCode path guarded")
