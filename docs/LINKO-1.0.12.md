# LINKO 1.0.12 — 휴대폰 선택 목록 누락 수정

사용자는 1.0.11로 변경한 뒤 ‘사용할 휴대폰 선택’에서 휴대폰이 사라졌다고 보고했습니다.
해당 PC의 장애 로그와 실제 휴대폰 연결은 확보하지 못했습니다. 따라서 아래 코드 문제를
수정했지만, 사용자의 물리 Bluetooth 단절 원인을 확정하거나 해결됐다고 선언하지 않습니다.

## 확인한 조건과 수정

- 이전 목록은 LINKO 등록 기록과 **활성 HID 구독**만 사용했습니다. Windows에서 페어링되어
  있어도 LINKO 등록 전 입력 구독이 사라지면 후보가 0개가 될 수 있습니다. 이제 Windows의
  페어링된 Bluetooth LE 기기도 후보로 읽습니다. 휴대폰 이외의 페어링 기기도 보일 수 있으며
  자동 등록/선택/연결은 하지 않습니다. 입력 전송 승인은 여전히 실제 페어링과 HID 준비 상태를
  확인합니다. 저장된 이름이나 페어링 기록을 ‘입력 연결 성공’으로 취급하지 않습니다.
- 1.0.11의 목록 조회는 현재 구독을 읽기 전에 공유 세션의 참조와 이벤트를 정리했습니다.
  이 처리를 제거했습니다. 목록은 GATT 세션을 정리하거나 서비스를 시작하지 않습니다.
  HID 구독 이벤트에서는 해당 입력 보고서의 세션만 추적합니다.
- 이미 Active로 돌아온 세션에 늦은 Closed 이벤트가 도착해도 논리 세대를 폐기하지 않습니다.
  HID/클립보드 종료 이벤트 안에서 동기적으로 재추적하던 호출도 제거했습니다.
  비활성 세션의 정리에서 native Close를 호출하지 않는 보호는 유지합니다.
- 페어링 조회 실패 시 이전 후보를, 개별 이름 조회 실패 시 기기 행을 유지합니다. 현재 입력
  상태는 메타데이터 조회 후 다시 읽으므로 오래된 키보드/마우스 연결 표시가 남지 않습니다.
  수동/자동 새로 고침의 늦은 결과가 새 결과나 사용자의 선택을 덮어쓰지 못하게 합니다.
- 진단 로그에 페어링 후보/등록/현재 입력/표시 행 수와 조회 오류를 기록합니다.
  기기 주소와 클립보드 내용은 이 로그에 넣지 않습니다.

`GetDeviceSelectorFromPairingState(true)`와 `DeviceInformation.FindAllAsync`는 페어링된
기록을 조회하는 데만 사용합니다. `BluetoothLEDevice.FromIdAsync`, GATT 클라이언트 연결,
주변 미페어링 기기 검색, 재페어링, Bluetooth 초기화는 추가하지 않았습니다.

## 검증

- Windows Release 빌드: 경고/오류 0개.
- 실제 제품 목록 클래스 검사: 페어링만 된 미등록 기기, HID 없음, 한쪽 HID만 연결,
  재연결, 조회 중 단절, 메타데이터 오류, 재시작 후 등록 기기, 중복 후보, 페어링 해제.
- 같은 검사에서 실제 Windows paired-only API 조회 성공: 로컬 PC 후보 5개.
  제품 `GetPeersAsync` 호출 전후 서비스 미시작/입력 준비 상태 유지 확인.
  이는 사용자 휴대폰이 조회됐다는 검사가 아닙니다.
- Core 178개 회귀 검사. native 광고 오류 복구, 실제 GATT 서비스 생성/정리 20회,
  로컬/원격 단축키 범위 검사. 최종 패키지와 Android 결과는 GitHub 릴리스에 기록합니다.
- Android 연결 동작은 변경하지 않았으며 1.0.10/1.0.11과 같습니다.
- 실제 휴대폰 재연결, 입력 장치 구독 복구, 장시간 끊김/마우스 지연은 미검증입니다.
  기존 OS 키 주입 검사 제한은 [1.0.9 기록](LINKO-1.0.9.md)을 유지합니다.

## 적용

PC 트레이에서 LINKO를 종료하고 1.0.12 `LINKO.exe`로 실행합니다.
이번 수정 때문에 휴대폰 APK를 다시 설치하거나 기존 페어링을 삭제할 필요는 없습니다.
제공 APK는 버전 일치용 1.0.12/versionCode 22입니다.

목록에 표시된 뒤에도 ‘입력 연결 대기’라면 목록 누락과 별개로 실제 HID 구독이 없는 상태입니다.
**도움말 → Bluetooth 진단 보기 → Bluetooth 다시 검사**와
**도움말 → 상세 로그 보기 → 진단 파일 저장**에서 상태를 확인할 수 있습니다.

API: [paired-only selector](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.getdeviceselectorfrompairingstate),
[DeviceInformation.FindAllAsync](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformation.findallasync).
