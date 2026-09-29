# LINKO 1.0.10 — 연결 끊김 후속 수정

사용자는 1.0.9에서 Bluetooth 연결 자체가 반복해서 끊기고, 휴대폰에서 다시 연결해도
PC가 제대로 인식하지 못한다고 보고했습니다. 현재 환경에는 해당 증상의 최신 로그나
ADB 연결 휴대폰이 없습니다. 아래는 코드에서 확인한 결함과 실행한 검사이며,
사용자 기기에서 끊김의 유일한 원인을 확정하거나 해결을 실증한 기록은 아닙니다.

## 변경

- Android의 10초 클립보드 작업 timeout이 `disconnect/close`를 호출하던 경로를 제거했습니다.
  늦은 응답을 기다리는 동안 같은 작업 슬롯을 유지하므로 후속 읽기/쓰기가 겹치거나
  이미 제출한 쓰기를 재전송하지 않습니다. 해당 작업의 callback이 도착하면 진행합니다.
  15초 연결 timeout은 실제 연결 이전에만 적용합니다.
- 서비스 검색 실패, 클립보드 서비스/특성 부재, CCCD 실패도 기존 Bluetooth를 유지하고
  클립보드만 보류합니다. 사용자가 앱에서 **연결**을 누르면 한 번 정리하고 재시도합니다.
  실제 연결 해제 callback, Bluetooth OFF, 명시적 연결 해제/종료는 기존처럼 정리합니다.
  실제 연결 실패의 재시도 제한과 이전 세대 callback 차단을 유지합니다.
- Windows 검색 신호 상태와 HID 서비스의 생존 상태를 분리했습니다. 검색 신호가
  Aborted이거나 재시작이 실패해도 구성된 서비스/특성/세션을 폐기하지 않습니다.
  기존 서비스의 광고 복구는 어댑터 조회와 15초 생성 timeout을 다시 실행하지 않습니다.
  실제 radio OFF, 명시적 정지와 앱 종료는 정리합니다. 클립보드 서비스도 같은 원칙을 적용합니다.
- 기기 목록 및 클립보드 상태 확인을 검색 신호의 Started 여부로 막지 않습니다.
  연결된 휴대폰의 실제 HID 구독 상태를 계속 확인합니다. 페어링이나 HID Report Map은 변경하지 않았습니다.
- Android **설정 → 도움말 → 연결 진단 로그 공유**에서 버전, 상태 코드, 연결 세대,
  재시도/정리 사유를 공유할 수 있습니다. 앱 내부 저장소에 크기를 제한해 기록하며
  입력 문자·클립보드 내용·Bluetooth 주소는 기록하지 않습니다. 파일 작업은 별도 스레드에서 수행합니다.
- 1.0.9의 마우스 전송 순서/간격 및 휴대폰 제어 중에만 작동하는 비상 복귀 단축키를 유지합니다.

클립보드 채널만 보류된 경우 자동 재연결로 HID를 반복해서 흔들지 않습니다.
callback이 끝내 오지 않거나 클립보드 서비스 구성이 실패하면 사용자의 **연결** 재시도가 필요합니다.

## 실행한 검증 (2026-09-29)

| 검사 | 결과 / 범위 |
|---|---|
| Windows Release 빌드 | PASS, 경고/오류 없음 |
| .NET 회귀 검사 | PASS, 171개. 공유 GATT 세션, 이전 세대 정리, HID 순서 등 |
| Android 단위 검사 / APK 빌드 | PASS, 50개. 지연된 작업 5분 유지, 늦은 callback, 실제 해제 후 재연결, 수동 복구, 로그 회전 포함 |
| `--ble-recovery-selftest` | PASS. 실제 Windows GATT 서비스를 생성한 상태에서 광고 호출 실패 20회 및 Aborted 상태를 주입. 서비스/특성/수명 유지, 동일 서비스의 광고 성공, 명시적 정리 확인 |
| `--ble-lifecycle-selftest` | PASS. 실제 Windows GATT 서비스 생성·광고·정리 20회 |
| `--release-selftest` | PASS. 설정/클립보드 정책/8ms 타이머/취소 |
| `--hotkey-scope-selftest` | PASS. Windows 단축키 소유권, 모의 hook 입력, 실제 WM_HOTKEY 처리 |
| `--ui-lifecycle-selftest` | PASS. WinForms 일시 오류 유지 및 종료 처리 |
| APK 서명 | PASS, 1.0.9와 동일 인증서, versionCode 20 |
| 실제 휴대폰 연결, 마우스 체감, Instagram/키보드 | 미실행. 연결된 ADB 기기 없음 |

`--input-selftest`의 OS 키 주입 검사는 이번에 다시 실행하지 않았습니다. 1.0.9에서
환경상 실패한 기록은 [이전 검증](LINKO-1.0.9.md)에 보존합니다. 단축키 범위 검사가
물리 키의 종단 검증을 대신하지 않습니다. Windows 광고 오류는 주입한 오류이며
실제 무선 단절이나 Android 재연결을 재현한 검사는 아닙니다.

## 설치 및 실제 기기 확인

1. PC의 기존 LINKO를 트레이에서 종료하고 1.0.10 EXE를 실행합니다.
   Android APK도 덮어 설치합니다. 양쪽 설정에서 1.0.10인지 확인합니다.
2. 기존 페어링을 유지하고 휴대폰 Bluetooth 설정에서 PC를 연결합니다.
   PC 목록의 키보드/마우스 연결 표시를 확인하고 기존 승인 기기를 사용합니다.
3. 키보드 입력·Instagram 사용·마우스 이동과 텍스트 동기화를 10분 이상 확인합니다.
4. 휴대폰에서 연결 해제 후 다시 연결하여 PC 목록 및 F8 입력 전환이 복구되는지 확인합니다.
5. 증상이 남으면 Windows의 **도움말 → 상세 로그 보기 → 진단 파일 저장**과
   Android의 **설정 → 도움말 → 연결 진단 로그 공유**를 사용합니다. 두 기록의 시각과
   disconnect 상태 코드를 맞춰 실제 단절 원인을 확인할 수 있습니다.

API 근거: [Android BluetoothGatt](https://developer.android.com/reference/android/bluetooth/BluetoothGatt),
[Windows GattServiceProvider](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattserviceprovider).
