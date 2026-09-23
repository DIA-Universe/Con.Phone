# LINKO 1.0.9 — Bluetooth 재연결 반복과 비상 복귀 키 수정

Windows EXE와 Android APK(1.0.9 / versionCode 19)를 함께 업데이트합니다.
기존 페어링, 신뢰 기기, 단축키 설정은 유지합니다. HID Report Map과 BLE UUID,
클립보드 wire protocol, 수신 확인 및 replay 보호는 변경하지 않았습니다.

## 확인한 코드 결함과 수정

- Android는 PC에서 사용자가 기기를 승인하기를 기다리는 시간에도 15초 연결 제한을
  적용했습니다. CCCD 등록 완료 뒤 `WAITING_AUTHORIZATION`으로 전환해 연결 제한을
  해제합니다. 인증 거절/잘못된 인증 응답은 같은 연결에서 5초 뒤 다시 확인합니다.
- 읽기 시작이 네 번 거절되거나 쓰기 시작이 한 번 거절되면 GATT 전체를 닫았습니다.
  이제 제출되지 않은 요청은 250ms부터 최대 5초까지 간격을 늘려 같은 연결에서
  재시도합니다. 쓰기는 제출되지 않은 프레임을 앞에 보존하여 순서를 유지합니다.
  완료된 ATT 쓰기 오류도 전송을 보류하고 대상 상태를 확인하며, HID 연결을 닫지 않습니다.
- 서비스 구성 실패, 실제 연결 해제나 제출한 작업의 무응답에는 정리가 필요합니다. 이때는 기존 세대별
  정리/단일 작업 제한을 사용하며, 자동 재연결은 1/2/4/8/16초 간격으로 최대 5회입니다.
  계속 실패하면 중지 안내를 표시합니다. **연결** 버튼 또는 Bluetooth OFF/ON으로
  다시 시도할 수 있습니다. 짧은 연결 성공은 예산을 초기화하지 않고 60초 안정 연결
  이후 초기화합니다. 수동 연결 해제 의사는 계속 보존합니다.
- 필수 단계였던 MTU 517 요청을 제거했습니다. 기본 MTU 23에서 서비스 검색을 시작하고,
  OS가 MTU 변경을 알려주면 그 크기를 적용합니다. 필수적이지 않은 MTU callback이
  누락됐다는 이유로 HID와 공유하는 연결을 다시 닫지 않습니다. 기본 MTU에서는 큰
  텍스트의 조각 수가 늘 수 있습니다. 프로토콜은 작은 MTU도 지원합니다.
- 마우스 전송마다 키보드·마우스 구독을 반복 열거하던 부분을 해당 Report의 구독 조회
  한 번으로 줄였습니다. 기본 전송 간격은 16ms에서 8ms로 바꾸되 한 번에 하나의
  Notify만 진행하고, 지연 중 들어온 이동은 합칩니다. 타이머 대기가 작업 스레드를
  계속 점유하지 않도록 변경했습니다. 반대 방향 휠/가로 스크롤은 서로 합쳐 사라지지
  않도록 순서를 보존합니다. 실제 무선 전송률 125Hz를 보장하는 변경은 아닙니다.
- Windows 폼이 비상 복귀(F9 기본값)를 항상 `RegisterHotKey`로 등록하여 노트북에서도
  키를 가로챘습니다. 비상 복귀와 휴대폰 잠금 단축키 등록을 입력 스레드로 옮겨
  휴대폰 제어 중에만 등록합니다. 복귀/오류/종료 시 해제하고, 지연된 `WM_HOTKEY`도
  로컬 상태에서는 동작시키지 않습니다. 입력 훅이 사라졌을 때의 대체 복귀 경로는 유지합니다.

## 이전 HID/연결 수정 재검토

1.0.8의 상대 ID·세대별 대표 GATT 세션 소유권을 유지합니다. 같은 연결의 키보드,
마우스, 클립보드 래퍼가 달라도 활성 세션을 교체·Dispose하지 않습니다.
클립보드 구독 해제는 HID 소유자가 남은 세션을 닫지 않습니다. 마지막 소유자만 한 번
정리하고, 이전 세대 callback은 새 연결을 정리하지 않습니다. 이 검사는 .NET 회귀
검사에 계속 포함됩니다. Android도 선택한 PC만 연결하며 검색, 역방향 연결 요청,
임의 재페어링은 추가하지 않았습니다. WinForms 비동기 종료 후 숨은 프로세스가
남던 이전 수정도 회귀 검사에 포함됩니다.

이 결함들은 코드에서 확인했습니다. 이번 환경에는 ADB로 연결된 휴대폰이 없어
사용자가 보고한 키보드 닫힘/Instagram 새로고침의 실제 원인이 이것만인지,
수정 후 해당 기기에서 증상이 사라지는지는 확인하지 못했습니다.

## 검증 (2026-09-23)

| 검사 | 결과와 범위 |
|---|---|
| Windows Release 빌드/단일 EXE publish | PASS, .NET 10 / win-x64 |
| .NET 회귀 검사 | PASS, 171개. HID 순서, 상대 전환, 세션 소유권, UTF-8/MTU/ACK/replay 포함 |
| Android 빌드/단위 검사 | PASS, 44개. 승인 대기 5분, 요청 거절 backoff, 연결 반복 제한, 명시적 재시도 포함 |
| `--release-selftest` | PASS, 설정/클립보드 정책/정밀 타이머/취소 |
| `--hotkey-scope-selftest` | PASS, 실제 Windows 전역 단축키 소유권, 모의 hook 입력, 실제 WM_HOTKEY 처리. F9/사용자 지정 키/20회 전환/훅 해제 후 복귀 |
| `--ui-lifecycle-selftest` | PASS, 실제 WinForms 루프에서 일시 오류 유지와 종료 3개 경로 |
| `--ble-lifecycle-selftest` | PASS, 실제 Windows GATT 서버 생성·광고·정리 20회. 휴대폰 연결 검사는 아님 |
| `--input-selftest` (OS 가상 키 주입) | FAIL. 수정 전 HEAD에서도 실패했고 주입 이벤트 전달이 일관되지 않았음. 검사 세션에 LogonUI 프로세스가 실행 중인 것도 확인. 물리 키의 종단 동작 성공으로 간주하지 않음 |
| APK 서명 | PASS, 이전 버전과 같은 Android 서명 인증서 |
| 실제 휴대폰 화면/마우스/양방향 텍스트 | 미실행, 연결된 ADB 기기 없음 |

`scripts/verify-windows.ps1`에 단축키 범위 검사를 추가했습니다. 세부 실행 로그는
로컬 `artifacts/compatibility-*.log`, Android JUnit 결과는
`artifacts/test-now/android-test-results/`에 있습니다.

## 실기기 확인 순서

1. 기존 LINKO를 트레이에서 종료하고 새 EXE를 실행합니다. Android APK도 덮어 설치합니다.
2. 기존 Bluetooth 페어링을 유지하고 앱에서 PC를 연결합니다. PC 승인 전 1분 이상
   기다려도 반복 연결/해제가 발생하지 않는지 확인한 뒤 기기를 승인합니다.
3. 휴대폰에서 텍스트를 입력하고 Instagram을 5분 이상 사용합니다. 키보드가 닫히거나
   화면이 다시 로드되는지, 연결 로그의 세션 번호가 반복 증가하는지 확인합니다.
4. F8로 제어한 뒤 이동/드래그/휠을 60초 이상 확인하고, 동시에 양방향 텍스트를 복사합니다.
5. F9로 PC로 돌아온 뒤 노트북 앱에서 F9가 본래 기능을 수행하는지 확인합니다.
   휴대폰 제어 중에는 Ctrl을 누른 상태에서도 F9가 복귀해야 합니다. 사용자 지정
   비상 키도 같은 방법으로 확인합니다.
6. PC 앱 종료 및 Bluetooth OFF/ON 뒤 선택한 PC로만 복구하는지 확인합니다.
   반복 실패로 중지 안내가 뜨면 PC 상태를 확인한 뒤 앱의 **연결**을 누릅니다.

API 근거: [Android BluetoothGatt](https://developer.android.com/reference/android/bluetooth/BluetoothGatt),
[Windows RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[Windows UnregisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey).
