# 구조와 개발 기준

## 확정 조건

대상 저장소는 DIA-Universe/Con.Phone뿐입니다. Windows 입력 공유 앱이며 Android와 iPhone을 함께 대상으로 합니다. 추가 동글, USB 경로, 서버, DB, 계정은 도입하지 않습니다. 최초 실기기 검증은 갤럭시입니다. iPhone 미검증 상태를 지우거나 지원 완료로 바꾸지 않습니다.

## 데이터 경로

`InputCapture → KeyboardState/PointerPacket → Outbox(epoch) → HID Encode → BleHidServer → 승인한 GATT 구독자`

Core는 Windows에 의존하지 않습니다. Windows 앱은 C#/.NET 10/WinForms 및 Windows WinRT GATT Server API를 사용합니다. 외부 NuGet UI 패키지는 사용하지 않습니다.

BLE HID 서비스는 0x1812, 키보드 report ID 1, 마우스 ID 2입니다. 알림 값에는 report ID를 다시 붙이지 않습니다. 키보드 입력은 8바이트, 마우스 입력은 버튼/상대 X/상대 Y/세로 휠/가로 휠 5바이트입니다. Boot Mouse는 3바이트로 줄이고 3개 버튼만 전송합니다. Report Mode가 기본이며 호스트별 protocol/suspend 상태를 분리합니다.

모든 보고서 읽기/쓰기에는 EncryptionRequired를 사용합니다. Notification은 반드시 특정 GattSubscribedClient를 지정합니다. 추가 연결 기기가 입력 값을 읽으면 0만 반환합니다. 앱이 임의로 OS 페어링을 수락하지 않습니다. 사용자의 명시적 대상 승인도 별도로 필요합니다. 암호화가 모든 MITM 공격을 자동 방어한다고 주장하지 않습니다. 실제 OS 페어링 방식과 사용자 확인을 시험해야 합니다.

입력 훅에서는 네트워크/디스크/UI 동기 작업을 하지 않습니다. 키보드 이벤트는 키 상태로 변환하고, mouse는 Raw Input의 상대 움직임과 버튼을 사용합니다. 로컬 억제와 Raw Input 전달의 실제 조합은 Windows에서 검증해야 합니다. 일반 데스크톱 외의 보안 데스크톱은 지원 범위가 아닙니다.

Outbox는 128개 항목으로 제한됩니다. 인접한 동일 버튼/epoch 마우스 항목만 병합합니다. 키/버튼 전환은 병합 장벽입니다. epoch가 바뀌면 이전 세션 항목을 보내지 않습니다. 전송 제한시간은 750ms이며 하드웨어에서 조정이 필요합니다. 실패 시 로컬 조작권 복구를 우선하고 자동 재개하지 않습니다.

## Windows GATT 제약

Microsoft가 제한 목록에 기재한 서비스는 DIS/GATT/GAP/SCP입니다. **HID 서비스 0x1812가 그 목록에 있다고 잘못 단정하지 않습니다.** 동시에 GATT API로 서비스를 생성할 수 있다는 것만으로 Windows를 모든 휴대폰에서 인증된 HID 액세서리처럼 사용할 수 있다고 단정하지 않습니다. OS가 관리하는 Appearance/이름/서비스 구성, 드라이버, 페어링, 휴대폰 호스트 정책은 실험 대상입니다.

Win32에서도 WinRT Bluetooth API를 호출할 수 있다는 Microsoft 안내에 따라 portable desktop 앱을 우선 제공합니다. 조직 정책/권한에 따라 AccessDenied나 DisabledByPolicy가 발생하면 오류를 그대로 분리해 보고해야 합니다. 보안 정책을 해제하거나 커널 드라이버/테스트 서명을 설치하지 않습니다.

## 보고 원칙

코드 구현, 컴파일, 자동 테스트, Windows 실제 실행, BLE 광고, 양방향 페어링, HID 구독, 실제 입력, 안정성/성능은 각각 별개의 완료 항목입니다. 자동 테스트는 프로토콜 인코딩과 큐 상태만 검증합니다. 저장소에 코드가 있다는 이유로 실기기 성공을 주장하지 않습니다.

## 1차 자료 (설계 확인 기준)

- Microsoft GATT Server: https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-server
- Bluetooth developer FAQ: https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/bluetooth-dev-faq
- 특정 구독자 알림 API: https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattlocalcharacteristic.notifyvalueasync
- Raw Input: https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input
- Apple AssistiveTouch 포인터: https://support.apple.com/ko-kr/111775
