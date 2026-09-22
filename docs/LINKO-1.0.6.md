# LINKO 1.0.6 · 주변기기 자동 연결 요청 제한

## 문제와 변경

사용자가 연결해서 쓰는 휴대폰 외의 주변기기에 연결/페어링 팝업이 반복된다고 보고했다. 실제 팝업이나 무선 패킷을 수집하지 못했으므로 발신 주체를 확정하지는 않았다. 코드에서 다음 두 경로를 확인하고 제거했다.

1. Android는 저장된 PC에 바로 연결하지 못하면 일반 HID 서비스 UUID까지 검색하고, 검색된 임의 기기에 `connectGatt`를 호출했다. LINKO 서비스가 없는 기기도 접속 후에야 제외했으며 30초 후 다시 후보가 됐다. 이제 Bluetooth 스캔을 하지 않고 **사용자가 선택했거나 기존 LINKO 연결로 저장된 PC 중 현재 페어링이 유지된 기기**만 연결한다. PC가 없거나 페어링이 해제되면 사용자 선택/페어링을 기다린다. 잘못 선택한 기기에 LINKO 서비스가 없으면 자동 재시도를 멈추고 다시 선택하도록 안내한다.
2. Windows는 HID 구독 이벤트 및 5초 주기 목록 갱신에서 `BluetoothLEDevice.FromIdAsync`로 상대 기기에 접근하고, 수신 GATT 세션에 `MaintainConnection=true`를 설정했다. 이름/페어링 조회를 `DeviceInformation.CreateFromIdAsync(..., AssociationEndpoint)`로 바꾸고 연결 유지 요청을 제거했다. 앱은 휴대폰이 시작한 수신 HID 세션을 처리한다.

Android 홈에 **연결할 PC 선택** 버튼을 추가했다. Bluetooth 검색 권한도 제거했고, 부팅 자동 시작·공유·백그라운드 클립보드의 권한 검사도 연결 권한만 사용하도록 맞췄다. 이미 연결하거나 연결 중인 PC를 다시 선택해도 기존 연결을 끊지 않는다.

Windows의 기본 16ms 마우스 전송 간격을 사용한다. 연결 간격을 조회하기 위한 Bluetooth 클라이언트 객체도 만들지 않는다. 기존 입력 병합, F8/F9 전환, 3초 복구 유예는 유지한다. HID 검색 신호 자체는 새 휴대폰에서 수동 연결할 수 있도록 유지한다.

참고: [Microsoft GATT client 문서](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)는 `FromIdAsync`가 접근 동의를 요청할 수 있고 `MaintainConnection`이 연결을 유발할 수 있음을 설명한다. [DeviceInformation 조회](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformation.createfromidasync)는 기기 메타데이터를 읽는 데 사용한다.

## 검증

- Core 143개 검사 통과.
- Windows Release 빌드 및 배포 EXE 자체 검사 통과.
- 이 PC의 저장된 기기 1개에 대해 Bluetooth 연결을 시작하지 않고 이름/페어링 정보를 읽는 조회 및 실제 `GetPeersAsync` 호출을 확인했다. 결과는 `AssociationEndpoint`, `IsPaired=true`였다. 기기 이름/주소는 검증 출력에 남기지 않았다.
- Android 자동 연결 정책 검사 6개 추가: 주변 HID/다른 페어링 기기 제외, PC 미선택, 페어링 해제, 연결 중 중복 호출, 대상 변경, 미지원 기기 자동 재시도 금지.
- Android 전체 24개 검사 및 APK 빌드 통과. APK의 검색 권한 제거, 버전 1.0.6 / versionCode 16, 기존 APK와 같은 서명을 확인했다.
- 실제 주변기기의 반복 팝업 소멸과 휴대폰의 장시간 HID 입력은 아직 실기기 검증하지 않았다.

## 적용

Windows와 Android를 모두 1.0.6으로 업데이트한다. 기존 앱/페어링을 지울 필요가 없다. Android에서 연결할 PC 선택 안내가 나오면 홈의 버튼으로 LINKO가 실행된 노트북을 한 번 선택한다.
