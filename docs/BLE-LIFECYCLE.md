# BLE lifecycle audit and validation

2026-09-29 연결 상태 후속: 1.0.12에서는 목록만 복구됐으며 연결해도 표시가 바뀌지 않는다는
보고가 이어졌습니다. 1.0.13은 누락되어 있던 Windows `System.Devices.Aep.IsConnected`를
조회하고 실제 Bluetooth 연결과 HID 준비 상태를 구분합니다. 세션 수명/연결 동작 변경 없이
상태 조회·표시를 수정한 범위이며, 실제 휴대폰 입력 재연결은 미검증입니다.
[LINKO-1.0.13.md](LINKO-1.0.13.md)를 참고하세요.

2026-09-29 목록 누락 후속: 1.0.11 이후 휴대폰이 목록에 나오지 않는다는 신고가 있어,
1.0.12에서 조회와 세션 수명 변경을 분리합니다. 아래의 ‘현재 구독 목록과 대조하여
세션 정리’는 철회한 구현입니다. 페어링 기록/등록 기록/현재 입력 구독을 별도로 읽고,
이미 Active인 세션에 도착한 늦은 Closed 알림은 무시합니다.
[LINKO-1.0.12.md](LINKO-1.0.12.md)에 재현 검사와 실기기 검증 한계를 기록했습니다.

2026-09-29 후속: 1.0.11은 같은 native 세션이 재활성화된 상태에서 이전 연결의 정리가
도착하는 경우를 보강합니다. 원격 Closed/구독 소실은 논리 세대와 이벤트 참조만 정리하고
native Close를 호출하지 않습니다. 누락된 Closed 알림은 현재 구독 목록과 대조합니다.
최신 설명은 [LINKO-1.0.11.md](LINKO-1.0.11.md)에 있습니다.

2026-09-29 추가: 1.0.10에서는 실제 연결 해제와 클립보드 작업 지연을 구분합니다.
서비스 검색/CCCD 실패와 작업 timeout만으로 GATT를 닫지 않으며, Windows 검색 신호
복구 실패도 기존 HID 서비스를 폐기하지 않습니다. 최신 검증과 남은 실기기 확인은
[LINKO-1.0.10.md](LINKO-1.0.10.md)를 참고하세요. 아래의 timeout 시 정리 설명은 과거 구현입니다.

2026-09-23 추가: 1.0.9는 승인 대기와 연결 timeout을 분리하고, 요청 시작 거절 시
연결을 유지하며, 반복 재연결 횟수를 제한합니다. 아래의 1.0.7 MTU 협상과 무제한
재연결 설명은 과거 구현입니다. 현재 동작과 검증은 [LINKO-1.0.9.md](LINKO-1.0.9.md)를 참고하세요.

2026-09-22 추가: 1.0.7 실사용 로그에서 Windows 세션 소유권 회귀가 확인되었습니다.
아래 1.0.7 검증 기록은 당시 실행 범위이며 실기기 성공을 뜻하지 않습니다.
수정 내용과 후속 검증은 [LINKO-1.0.8.md](LINKO-1.0.8.md)를 참고하세요.

Baseline: `b61904f` (LINKO 1.0.6). This audit precedes the lifecycle changes.

## Roles and existing paths

Windows `BleHidServer` and `BleClipboardService` publish local GATT services
(`GattServiceProvider` / `GattLocalCharacteristic`). Android
`ClipboardBridgeService` is the central/client and creates `BluetoothGatt` with
`connectGatt(autoConnect=false)`. Android's OS HID client shares the physical
link but is not owned by the companion. Closing the companion must not unpair
the phone or claim to terminate the OS HID client's connection.

There is intentionally no BLE scanner or DeviceWatcher. Android connects only
to the user-selected, already bonded PC. Windows reads association metadata,
not `BluetoothLEDevice.FromIdAsync`; it must never initiate a reverse connection.
There are no Windows client-side `BluetoothLEDevice`, `GattDeviceService`, or
remote `GattCharacteristic` objects to dispose. The client owns CCCD writes.

| Path | Baseline implementation / defect |
|---|---|
| Connect / timeout | Android `connectDevice`, shared callback, 25-second handshake timer; object identity alone gates connects. |
| Discovery / MTU | `requestMtu` followed by an unconditional 2-second discovery fallback: discovery can overlap an outstanding MTU request. |
| Characteristics / CCCD | Missing TX returns without cleanup; notification registration and descriptor start results ignored; failed descriptor callback only updates text. |
| Reads / writes | Separate read/write gates, no shared gate covering bootstrap/CCCD. Write start/error does not invalidate a failed connection. |
| Recovery | `restartGatt` calls disconnect then synthesizes a callback after 1.5 seconds; manual target change also synthesizes a callback. Several delayed retries coexist. |
| Disconnect / close | Normal callback closes, but no common cleanup or CCCD disable; stale callbacks repeatedly call close. Service destroy only calls close and leaves characteristic references. |
| Radio | Android handles ON and ACL-connected broadcasts, not OFF. Windows recovery reuses providers, without a radio transition reset. |
| Partial transfer | Outstanding fragments/read timers/ACKs have separate lifetimes; partial receive assemblies survive a dropped transport until expiry. |
| Windows sessions | HID retains GattSession references and anonymous status handlers; replacement/closed/Dispose remove dictionary entries without disposing sessions or detaching handlers. |
| Windows server | StopAdvertising alone retains providers/characteristics/handlers; partial service construction explicitly demands app restart. Clipboard pending notifications lack subscription-lifetime cancellation. |
| Shutdown | Windows disposes HID semaphores before awaiting the input pump; late releases can race disposal. Android Activity is correctly separate from the foreground service. |
| Foreground/background | Activity onPause removes UI polling, not the service. Preserve this ownership. Windows window close hides to tray; explicit Exit shuts down. |

These are confirmed code defects, not proof that a particular OS/driver retained
a physical link. The original 08:14:50 log is insufficient to identify a status
code or reproduce the hardware failure.

## Planned changes

Introduce a testable Android lifecycle owner with connection generations,
one operation at a time, bounded timers, idempotent close and one capped retry.
Integrate it into the existing foreground service and retain all codecs, ACKs,
MTU fragmentation and replay rules. Clear incomplete assemblies without erasing
completed-transfer replay protection. Preserve the current text-only UI policy;
image codecs/PNG validation remain covered by protocol tests.

On Windows, detach local service callbacks, dispose owned session references,
cancel sends on subscription loss, reset providers on radio changes and clean
up every reachable application exit path. Do not add client APIs to a server.

Files: Android ClipboardBridgeService, new BleConnectionLifecycle and tests,
MainActivity, Protocol; Windows BleHidServer, BleClipboardService,
ClipboardSyncCoordinator, MainForm, Program; core reassembly and regression
tests; runnable validation script and this document.

## API references

- [Android BluetoothGatt](https://developer.android.com/reference/android/bluetooth/BluetoothGatt)
- [Android client ownership and close](https://developer.android.com/develop/connectivity/bluetooth/ble/connect-gatt-server)
- [Windows local GATT provider](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattserviceprovider)

Force-killing a process does not execute finally/onDestroy/exit handlers. The OS
owns that release; recovery after force kill must be verified on the real pair.

## 수정 결과 (1.0.7)

### 원인

Android의 누락된 특성/알림 실패 처리가 GATT를 남길 수 있었고, 서비스 종료는
disconnect 없이 close만 호출했습니다. 복구와 대상 변경은 callback을 직접
호출해 상태를 바꾸었으며, MTU callback을 기다리는 동안 서비스 검색을 시작할
수 있었습니다. Bluetooth OFF 정리와 재연결 예약의 단일 소유자도 없었습니다.

Windows는 수신한 GattSession을 교체하거나 제거하면서 이벤트 해제/Dispose를
하지 않았습니다. 로컬 GATT provider/characteristic 이벤트도 계속 남았고,
세마포어 Dispose가 전송 완료보다 앞서 실행될 수 있었습니다. Clipboard의
notify/receipt/ACK 대기는 구독의 수명과 연결되지 않았습니다.

OS의 stale connection이 원래 08:14:50 오류를 일으켰다는 사실까지 확인한
것은 아닙니다. 위 결함은 코드로 확인했으며, 원래 장애의 재현은 휴대폰이 필요합니다.

### 핵심 수정

- Android `BleConnectionLifecycle`이 GATT 소유권, 상태, 세션 번호, timeout,
  operation, cleanup, 재연결 예약을 관리합니다. 모든 호출은 main Handler에서
  직렬로 처리합니다. 이전 GATT 객체와 이전 세션 번호의 callback은 무시합니다.
- 상태: DISCONNECTED → CONNECTING → DISCOVERING_SERVICES → CONNECTED.
  정리는 DISCONNECTING → DISCONNECTED, 자동 복구는 RECONNECT_WAIT를 거칩니다.
  SCANNING은 상태 정의에만 포함하며, 주변 기기 검색을 다시 도입하지 않았습니다.
- 연결/handshake 제한 15초, 개별 GATT 작업 제한 10초. MTU 완료 이후 discovery,
  필수 특성 확인 이후 notification/CCCD, 그 후 인증 읽기와 일반 읽기/쓰기를
  시작합니다. 실패한 API 반환값도 확인합니다.
- 정상 수동 해제는 로컬 Notify OFF와 CCCD OFF를 시도하고 최대 750ms까지만
  기다립니다. 이미 끊긴 연결, 진행 중인 ATT 작업, radio OFF, 종료 중에는
  새로운 ATT 작업을 겹치지 않고 disconnect/close까지 즉시 진행합니다.
  Android에는 개별 ATT 요청 취소 API가 없으므로 disconnect/close로 중단합니다.
- disconnect가 예외를 던져도 close를 실행합니다. close 전에 소유권을 제거해
  중복 callback과 반복 cleanup으로 같은 GATT를 다시 닫지 않습니다.
- 자동 복구는 1/2/4/8/10초, 최대 10초이며 한 개만 예약됩니다. 사용자의 연결
  해제 의사는 저장되어 Activity 재개, 서비스 재생성, ACL 이벤트, Bluetooth
  ON으로 되살아나지 않습니다. 홈의 **연결** 또는 PC 선택으로 다시 허용합니다.
- Windows `BleResourceLifetime`은 native 작업 취소 → 이벤트 해제 → provider
  광고 중지를 수행합니다. `SharedBleResource<GattSession>`은 HID/Clipboard가
  같은 객체를 쓰는 경우 마지막 소유자만 Dispose하도록 합니다. 마지막으로
  characteristic/provider/session 참조를 비웁니다. GattLocalService와 provider는
  IDisposable API가 없으므로 StopAdvertising, 이벤트 해제, 참조 해제가 적용됩니다.
- Windows 서버가 원격 CCCD를 쓰거나 BluetoothLEDevice를 생성하지 않습니다.
  CCCD OFF는 소유자인 Android에서 수행합니다. Windows는 구독 해제에 맞춰
  notify, receipt, application ACK 대기를 취소합니다.
- Windows Radio.StateChanged와 Android adapter 상태 이벤트로 OFF 자원을
  정리합니다. ON은 선택된 상대만 복구합니다. Windows의 기존 검색 신호 복구
  제한(최소 4초)도 유지합니다.
- Windows 정상 종료, ApplicationExit, ProcessExit, SessionEnding, 처리되지 않은
  예외에서 정리를 호출합니다. Android onDestroy는 다른 자원 정리보다 먼저
  GATT를 닫습니다. Activity background 전환은 foreground service를 종료하지 않습니다.
- 미완성 fragment/chunk만 버리고 완료된 전송의 replay 기록은 유지합니다.
  중단된 송신 내용은 기존 TTL/크기 제한 내에서 새 전송으로 재시도합니다.
  protocol version, UUID, UTF-8, PNG 검증, MTU 분할, 순서 뒤바뀜/중복/conflict
  검사, receipt와 application ACK 형식은 바꾸지 않았습니다.
- 진단에는 `[BLE][Session …]`, 상태, GATT status, HRESULT를 기록하며 클립보드
  본문과 기기 주소는 추가 로그에 넣지 않습니다. Windows 종료 로그는 UI queue와
  별개로 파일에 기록합니다.

### 테스트 결과

2026-09-22, 로컬 Windows 노트북에서 실행했습니다.

| 자동 검사 | 결과 | 범위 |
|---|---|---|
| Windows Release 빌드 / 단일 EXE publish | PASS | net10.0-windows, win-x64 self-contained |
| Android debug APK / 단위 테스트 | PASS | JDK 17, SDK 35, 테스트 39개, 실패 0 |
| .NET 핵심 회귀 검사 | PASS | 157개, 프로토콜/PNG/MTU/ACK/replay 및 cleanup 취소 포함 |
| 배포 EXE `--release-selftest` | PASS | 기존 설정/입력/HTML/동기화 정책 회귀 |
| 실제 Windows API 서버 생성·광고·정리 | PASS | 같은 프로세스에서 20회. 상대 휴대폰 없이 실행 |
| APK 서명 및 버전 | PASS | 1.0.7 / versionCode 17, 기존 APK와 같은 서명 인증서 |

| 요청한 실기기 검사 | 결과 | 자동 검사로 확인한 부분 |
|---|---|---|
| Test 1 정상 연결, 양방향 text | 미실행 | 기존 UTF-8 및 wire/MTU 재조립 회귀 PASS |
| Test 2 Windows 강제 종료 후 재연결 | 미실행 | Windows 서버 재생성 20회 PASS (강제 종료 검사는 아님) |
| Test 3 Android 강제 종료 후 재연결 | 미실행 | 서비스 shutdown cleanup/cancel 모델 PASS |
| Test 4 Android 및 Windows Bluetooth OFF/ON | 미실행 | radio OFF close / ON retry / 수동 해제 유지 모델 PASS |
| Test 5 연결 버튼 10회 연타 | 미실행 | 실제 사용한 lifecycle 클래스에서 생성 횟수 1회 PASS |
| Test 6 CONNECTING 중 추가 연결 | 미실행 | CONNECTING/DISCOVERING/CONNECTED 중 중복 차단 PASS |
| Test 7 휴대폰과 연결/해제 20회 | 미실행 | 가짜 GATT close 정확히 1회 ×20 + 실제 Windows 서버 반복 PASS |
| Test 8 전송 중 끊김 후 새 전송 | 미실행 | receipt 대기 취소/대기자 제거/부분 조립 초기화/replay 유지 PASS |

ADB `devices`에 연결된 휴대폰이 없었습니다. 위 여덟 항목은 실제 휴대폰에서
성공했다고 표시하지 않았습니다. 검사 절차와 PASS 기준, 결과 저장은
`scripts/test-ble-lifecycle.ps1 -Mode Hardware -AndroidSerial <serial>`로 실행합니다.
`-Mode Plan`은 모든 항목을 미실행으로 기록하고 절차만 출력합니다.

검사 로그는 `artifacts/ble-core-tests.log`, `artifacts/ble-android-build.log`,
`artifacts/test-now/android-test-results/`, `artifacts/ble-packaged-release-selftest.log`,
`artifacts/ble-packaged-native-test.log`에 있습니다.

### 남은 위험

- OS가 프로세스를 강제로 죽이면 종료 handler 자체가 실행되지 않습니다.
  OS의 native handle 회수와 각 제조사 Bluetooth 드라이버의 재연결 동작은
  실기기 검사가 필요합니다. 모든 기종에서 재부팅이 절대 불필요하다는 보장은
  이 검사 결과만으로 하지 않습니다.
- Android OS HID client와 companion은 물리 링크를 공유합니다. Companion
  close 후 OS HID 연결이 유지되는 것은 별도 소유자의 정상 연결일 수 있습니다.
  이것을 companion GATT 누수와 구분해야 합니다.
- 기준 버전 1.0.6은 이미 이미지 송신을 UI/동기화 정책에서 차단하고 있습니다.
  이를 변경하지 않았고 이미지 codec/PNG/중복·충돌 처리는 기존 회귀 검사를
  유지했습니다. 실기기 대용량 중단 검사는 큰 텍스트로 실행하도록 했습니다.
- 1.0.7은 이 범위를 명시한 검증 빌드입니다. 기존 페어링/설정을 유지한 상태로
  실제 노트북–휴대폰 조합의 검증을 완료해야 합니다.

### 수정 파일 전체 (23개)

| 파일 | 변경 |
|---|---|
| `README.md` | 개발 빌드와 검증 문서 연결 |
| `companion/android/app/build.gradle.kts` | 1.0.7 / 17 |
| `companion/android/app/src/main/java/universe/dia/conphone/BleConnectionLifecycle.kt` | GATT 수명주기 소유자 (신규) |
| `companion/android/app/src/main/java/universe/dia/conphone/ClipboardBridgeService.kt` | 수명주기, 작업 직렬화, cleanup, radio, callback 통합 |
| `companion/android/app/src/main/java/universe/dia/conphone/MainActivity.kt` | 명시적 연결 / 연결 해제 |
| `companion/android/app/src/main/java/universe/dia/conphone/Protocol.kt` | 부분 조립만 초기화 |
| `companion/android/app/src/main/java/universe/dia/conphone/ReliableBleReceiver.kt` | 이전 연결의 receipt cache 초기화 |
| `companion/android/app/src/test/java/universe/dia/conphone/BleConnectionLifecycleTest.kt` | lifecycle fault/callback/timeout 검사 (신규) |
| `companion/android/app/src/test/java/universe/dia/conphone/BleCleanupProtocolTest.kt` | cleanup 후 재조립/replay 회귀 (신규) |
| `src/ConPhone.Core/BleResourceLifetime.cs` | 취소·정리 scope (신규) |
| `src/ConPhone.Core/SharedBleResource.cs` | GATT 참조의 공동 소유권 (신규) |
| `src/ConPhone.Core/ClipboardSync.cs` | 부분 조립만 초기화 |
| `src/ConPhone.Windows/BleHidServer.cs` | 서버/세션 cleanup, radio, generation |
| `src/ConPhone.Windows/BleClipboardService.cs` | 구독 수명, 취소, 알림 직렬화, 서버 cleanup |
| `src/ConPhone.Windows/ClipboardSyncCoordinator.cs` | radio 및 종료 연동 |
| `src/ConPhone.Windows/MainForm.cs` | 안전한 종료 순서, radio 연동, 종료 로그 |
| `src/ConPhone.Windows/Program.cs` | 종료/예외 경로와 native 검사 진입점 |
| `src/ConPhone.Windows/BleLifecycleSelfTest.cs` | 실제 Windows 서버 반복 검사 (신규) |
| `src/ConPhone.Windows/ConPhone.Windows.csproj` | 1.0.7 |
| `tests/ConPhone.Tests/Program.cs` | 자원 해제·취소·재조립 회귀 |
| `scripts/test-ble-lifecycle.ps1` | 8개 실기기 검사 및 결과 기록 (신규) |
| `docs/BLE-LIFECYCLE.md` | 사전 분석과 결과 (신규) |
| `docs/LINKO-1.0.7.md` | 빌드/검사 안내 (신규) |
