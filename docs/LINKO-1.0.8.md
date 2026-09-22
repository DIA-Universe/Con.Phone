# LINKO 1.0.8 — Windows 연결 중 세션 해제 및 창 종료 수정

## 원인

사용자가 제공한 2026-09-22 13:26:48 / 13:28:14 로그에서 키보드 구독이 1이 된
직후 HID 세션 해제/추적이 반복되고 `COMException HRESULT=0x80010108`
(`RPC_E_DISCONNECTED`)로 UI 오류 처리가 실행되었습니다.

1.0.7 `BleHidServer.TrackSession`은 같은 휴대폰의 GATT 세션을 객체 참조로
비교했습니다. 키보드·마우스 등의 구독에서 다른 WinRT 래퍼가 전달되면 이전
세션을 교체하면서 Dispose하여 진행 중인 연결을 끊었습니다. `SharedBleResource`의
참조 동일성 기반 카운트도 서로 다른 래퍼가 공유하는 네이티브 연결을 보호하지
못했습니다. Clipboard의 세션 참조 비교도 같은 연결의 요청을 거절할 수 있었습니다.

이것은 매핑 이전의 Bluetooth HID 연결을 깨뜨리는 결함입니다. 로그가 증명하는
범위는 실행 중인 1.0.7의 연결 해제이며, LINKO 프로세스가 전혀 없는 상태에서의
Bluetooth 연결 실패 원인까지 이 로그만으로 단정할 수는 없습니다.

UI 상태 조회는 일부 WinRT 예외만 처리하여 `RPC_E_DISCONNECTED`가 전파되었습니다.
전역 UI 오류 처리는 BLE를 영구 Dispose하고 Application.Exit을 요청했습니다.
비동기 FormClosing 정리 뒤 다시 Close할 때 UserClosing으로 재진입해 창을 숨길 수
있는 별도 결함도 있었습니다. 해당 종료 경로는 실제 WinForms 메시지 루프 테스트에서
수정 전 FAIL(강제 종료 watchdog 필요), 수정 후 PASS로 확인했습니다.

## 핵심 수정

- 활성 연결은 휴대폰 ID와 연결 세대로 관리하며 하나의 대표 GattSession을 유지합니다.
- 다른 구독/요청의 래퍼가 전달되어도 정상 세션을 교체하거나 닫지 않습니다.
- HID와 clipboard 소유자가 같은 대표 세션을 공유합니다. 마지막 소유자의 정리에서
  한 번만 Dispose하며, 해제 중 재획득과 이전 세대 정리가 새 세대에 미치는 영향을 차단합니다.
- Clipboard 요청은 활성 세션과 상대 ID, 구독 수명을 검증합니다. 닫힌 세션은 거절합니다.
- 상태 조회 중 끊긴 COM 객체는 연결 해제 상태로 처리합니다. 같은 일시 오류가 UI까지
  도달해도 창과 서비스를 유지합니다. 그 밖의 UI 오류는 안내 후 완전히 종료합니다.
- 종료가 시작되면 상태 타이머를 중지하고, 정리 후 Close는 트레이 숨기기에 들어가지 않습니다.
- 예외 타입/HRESULT/호출 스택을 기록합니다. 예외 메시지·인수·파일 경로·클립보드 본문은 제외합니다.

Android 동작 및 프로토콜은 변경하지 않았고 배포 버전만 올렸습니다. Android 1.0.7과
Windows 1.0.8은 프로토콜 호환입니다. UTF-8, PNG 검증, MTU/조각 재조립, ACK, replay
처리를 그대로 유지하며 기존 이미지 송신 비활성화 정책도 그대로입니다.
주변 기기 스캔이나 Windows의 역방향 연결 요청은 추가하지 않았습니다.

## 수정 파일

- `src/ConPhone.Core/SharedBleResource.cs`
- `src/ConPhone.Windows/BleHidServer.cs`
- `src/ConPhone.Windows/BleClipboardService.cs`
- `src/ConPhone.Windows/BleNativeState.cs`
- `src/ConPhone.Windows/MainForm.cs`
- `src/ConPhone.Windows/MainFormLifecycleSelfTest.cs`
- `src/ConPhone.Windows/Program.cs`
- `src/ConPhone.Windows/SessionDiagnostics.cs`
- `src/ConPhone.Windows/ReleaseSelfTest.cs`
- `src/ConPhone.Windows/ConPhone.Windows.csproj`
- `tests/ConPhone.Tests/Program.cs`
- `companion/android/app/build.gradle.kts`
- `scripts/verify-windows.ps1`
- `README.md`
- `docs/BLE-LIFECYCLE.md`
- `docs/LINKO-1.0.7.md`
- `docs/LINKO-1.0.8.md`

## 테스트 결과

| 검사 | 결과 | 범위 |
|---|---|---|
| Core 회귀 검사 | PASS | 167 checks. 다른 래퍼 4개, 공유 소유권, 이전 세대 정리, 동시 해제 100회, 모의 재연결 20회 포함 |
| WinForms 종료 회귀 | PASS | ApplicationExitCall → 비동기 정리 → Close, 사용자 X → 트레이 종료, UI 치명 오류 후 종료 |
| WinForms 연결 해제 예외 | PASS | 합성 RPC_E_DISCONNECTED 주입 후 창/서비스 수명 유지 |
| Windows release 검사 | PASS | 닫힌 COM 객체 처리, 기존 클립보드/시작 순서/입력 타이머 검사 |
| Windows 빌드 | PASS | Release, 컴파일 오류/경고 없음 |
| Android 빌드/단위 테스트 | PASS | APK 생성, 39 tests, 실패 0 |
| Test 1 정상 페어 연결 및 텍스트 양방향 전송 | 미실행 | 수정본을 실제 휴대폰에 연결하지 못함 |
| Test 2 Windows 강제 종료 후 재연결 | 미실행 | 동일 |
| Test 3 Android 강제 종료 후 재연결 | 미실행 | 동일 |
| Test 4 양쪽 Bluetooth OFF/ON | 미실행 | 동일 |
| Test 5 연결 버튼 10회 | 미실행 | Android 모의 테스트만 통과, 실기기 아님 |
| Test 6 연결 중 추가 연결 요청 | 미실행 | Android 모의 테스트만 통과, 실기기 아님 |
| Test 7 실제 페어 연결/해제 20회 | 미실행 | 자원 소유권 모의 검사와 별개 |
| Test 8 전송 도중 연결 종료 | 미실행 | 취소/재조립 단위 검사는 통과, 실기기 아님 |

실행 가능한 회귀 검사:

```powershell
dotnet run --project tests/ConPhone.Tests -c Release
dotnet run --project src/ConPhone.Windows -c Release -- --release-selftest
dotnet run --project src/ConPhone.Windows -c Release -- --ui-lifecycle-selftest
scripts/test-now.ps1 -Target android
scripts/test-ble-lifecycle.ps1 -Mode Hardware -AndroidSerial <adb-serial>
```

## 적용 및 남은 위험

기존 LINKO 1.0.7을 트레이의 종료로 완전히 종료합니다. 남은 프로세스가 있으면
작업 관리자에서 LINKO만 종료하고 1.0.8 EXE를 실행합니다. 휴대폰 Bluetooth 설정에서
PC 연결이 유지되는지 먼저 확인한 뒤, HID 입력 및 clipboard 검증을 진행합니다.
기존 페어링/승인 정보는 수정본이 삭제하지 않습니다.

실기기 연결의 회복, Windows 드라이버가 유지하는 링크, 페어링/서비스 캐시 상태는
실제 조합에서 추가 확인해야 합니다. 이 빌드는 실기기 성공을 확인한 정식 배포가
아니며, 재부팅 없이 항상 복구된다고 아직 보장하지 않습니다.
