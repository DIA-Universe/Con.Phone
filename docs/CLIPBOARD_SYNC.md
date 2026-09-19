# Con.Phone text/image clipboard sync

## 목표

Con.Phone의 키보드/마우스 경로와 클립보드 데이터 경로를 분리한다.

- 입력: 표준 BLE HID(HOGP)
- 텍스트/이미지: Con.Phone Companion data plane
- 파일/폴더: 지원하지 않음

사용자가 목표로 하는 기본 흐름은 다음과 같다.

1. Windows에서 텍스트 또는 이미지를 복사한다.
2. Con.Phone으로 입력 대상을 휴대폰으로 전환한다.
3. 최신 클립보드가 Companion에 전달된다.
4. 대상 앱이 해당 형식을 지원하면 붙여넣는다.

휴대폰 → Windows는 Companion의 명시적 전송을 사용한다. Android는 ACTION_SEND Share Target으로 다른 앱의 텍스트/이미지를 바로 Con.Phone에 넘길 수 있고, iOS는 Share Extension이 App Group에 payload를 staging한 뒤 Con.Phone 앱이 활성화될 때 기존 BLE/P2P 전송 엔진으로 보낸다.

## 전송 경로

Windows는 다음 순서로 자동 선택한다.

1. **Direct Wi-Fi / P2P 링크** — Windows가 전용 링크를 만들고 Companion이 직접 연결, TCP + AES-256-GCM
2. **Bluetooth LE fallback** — P2P를 사용할 수 없을 때 텍스트/작은 이미지 전송

Wi-Fi Direct/Wi-Fi Aware 등으로 IP 링크가 이미 만들어진 경우에도 1번 프로토콜을 그대로 사용할 수 있다. 현재 앱이 직접 P2P 그룹을 생성하는 기능은 별도 검증 대상이다.

텍스트는 BLE fallback을 허용한다. 이미지는 기본 512 KiB까지 BLE fallback을 허용하며 Windows 설정에서 64~4096 KiB로 조정할 수 있다.

## 승인 및 보안

클립보드 데이터는 HID report에 넣지 않는다.

Windows에서 사용자가 HID 대상 휴대폰을 승인하면:

- Windows Password Vault에 기기별 256-bit 임의 secret을 생성/보관한다.
- Companion은 승인된 동일 BLE peer에서만 암호화된 custom GATT characteristic을 읽을 수 있다.
- characteristic은 deviceHash(32) + secret(32)을 반환한다.
- BLE characteristic 접근에는 GATT encryption을 요구한다.
- P2P 연결은 secret 기반 HMAC challenge/response 후 시작한다.
- 각 P2P clipboard frame은 AES-256-GCM으로 암호화하고 device hash를 AAD에 포함한다.

진단 로그에는 클립보드 본문과 pairing secret을 기록하지 않는다.

## Wire protocol

ConPhone.Core/ClipboardSync.cs가 Windows의 기준 구현이다.

지원 콘텐츠:

- UTF-8 text: 최대 1 MiB
- PNG/JPEG/WebP image: 최대 32 MiB

각 transfer는 SHA-256을 포함하며 chunk 순서가 뒤바뀌거나 중복되어도 재조립한다. 완료 시 전체 길이와 hash를 다시 검증한다.

BLE에서는 wire frame을 다시 ATT notification/write 크기에 맞게 fragment한다. 이 secondary fragmentation은 30초 timeout과 중복 fragment 방지를 포함한다.

## Windows

Windows 앱은 기존 ClipboardHistoryService를 실제 OS adapter로 사용한다.

- WM_CLIPBOARDUPDATE로 text/image를 읽는다.
- Windows bitmap은 PNG로 정규화하여 저장/전송한다.
- 휴대폰 수신 항목을 실제 Windows clipboard에 적용한다.
- remote apply 시 self-write guard를 사용해 echo loop를 방지한다.
- 입력 대상을 휴대폰으로 전환할 때 최신 로컬 항목을 동기화한다.
- 클립보드 탭에서 선택 항목을 수동으로 보낼 수도 있다.

## Android Companion

companion/android

- Android 12(API 31)+
- foreground BLE connection
- encrypted custom GATT service 연결
- BLE credential로 Windows Direct Wi-Fi/P2P 고속 채널 자동 승격
- PC → Android text: ClipboardManager
- PC → Android image: app cache의 content URI를 clipboard에 등록
- Android → PC: Companion 화면의 명시적 보내기 버튼
- P2P 연결이 불가능하면 BLE fallback
- Android가 Windows P2P 링크를 사용할 수 있으면 시스템 승인 후 local-only Wi-Fi로 승격

이미지 paste 성공 여부는 붙여넣기 대상 Android 앱이 image/content URI clipboard를 지원하는지에 따라 달라진다.

## iOS Companion

companion/ios

- iOS 17+
- CoreBluetooth central + background central mode
- PC → iOS text/image를 UIPasteboard에 적용
- iOS → PC는 Companion의 명시적 보내기 버튼
- BLE fallback 지원
- P2P 고속 연결 버튼: iOS 시스템 승인 → Windows 전용 Wi-Fi → AES-GCM TCP 채널

## 자동 검증과 실기기 검증의 구분

CI에서 검증 가능한 항목:

- .NET core protocol tests
- Windows build
- 실제 Windows clipboard text roundtrip
- 모든 Windows UI tab snapshot render
- self-contained Windows x64 EXE publish
- Android debug APK compile
- iOS Simulator SDK compile

CI만으로 검증할 수 없는 항목:

- 특정 Galaxy/iPhone의 HID + custom GATT 동시 연결
- Bluetooth MTU/속도
- Windows 방화벽 환경의 P2P TCP 연결
- 모바일 앱별 image paste 지원
- 절전/재부팅 후 장시간 reconnect

이 항목은 실제 기기 E2E 시험표에서 별도로 확인한다.


## Windows Direct Wi-Fi 주의사항

Con.Phone은 같은 Wi-Fi를 전제로 하지 않는다. 고속 경로는 Windows가 직접 만드는 Wi-Fi Direct/legacy AP를 사용한다.

- Windows Mobile Hotspot과 Wi-Fi Direct는 동시에 사용할 수 없다. Mobile Hotspot이 켜져 있으면 P2P 시작이 실패할 수 있으며 이 경우 BLE fallback을 사용한다.
- P2P SSID는 클립보드 설정의 "P2P 링크 이름"으로 바꿀 수 있다.
- Windows 내장 BLE의 시스템 Bluetooth 페어링 이름은 Con.Phone이 변경하지 않는다. 앱별 GATT 서버에서 시스템 Bluetooth 이름을 안전하게 재정의하는 지원 경로가 없기 때문이다.
- P2P 비밀번호와 per-device clipboard secret은 Windows Password Vault에 보관한다.
- 비활성 신뢰 기기는 clipboard secret을 유지할 수 있지만 P2P 설정 읽기와 clipboard 쓰기는 거부된다.


## P2P 보안 프로토콜 v2

Direct Wi-Fi/P2P 링크 자체의 WPA2 비밀번호만 신뢰하지 않는다.

1. Companion이 BLE에서 받은 per-device 256-bit secret과 device hash를 사용한다.
2. TCP 연결 후 CPHELLO2 / CPREADY2 HMAC challenge-response로 상대를 인증한다.
3. client nonce 16 bytes + server nonce 16 bytes + device hash를 장기 secret과 HMAC-SHA256하여 연결 전용 256-bit session key를 만든다.
4. clipboard wire frame은 session key로 AES-256-GCM 암호화한다.
5. 각 방향 frame sequence를 AAD에 포함하고 1부터 정확히 증가해야 한다.
6. 연결 종료 시 Windows/Android의 session key 메모리를 즉시 폐기한다.
7. wire frame, BLE fragment 모두 완료 transfer replay와 내용이 바뀐 duplicate chunk를 거부한다.
8. 동시에 진행 가능한 미완료 transfer/frame 수를 제한하여 메모리 소모 공격을 막는다.

장기 pairing secret은 P2P 데이터 frame의 AES key로 직접 사용하지 않는다.

## HID 입력과 클립보드 QoS

마우스/키보드 입력이 활성 상태일 때 이미지 bulk payload를 BLE로 동시에 밀지 않는다.

- 텍스트: BLE fallback 허용
- 이미지 + HID 입력 중: P2P를 우선하고 잠시 연결을 기다림
- P2P가 준비되지 않으면 이미지 BLE bulk 전송을 보류하고 HID 입력을 계속 유지
- 사용자가 로컬 입력 상태에서 수동 전송하는 작은 이미지는 설정된 BLE 한도 내에서 fallback 가능

이 정책은 이미지 전송 때문에 상대좌표 mouse report가 Bluetooth queue에 밀리는 현상을 줄이기 위한 것이다.


## 휴대폰 → PC 공유 UX

### Android

- 다른 앱의 공유 메뉴에서 Con.Phone을 선택할 수 있다.
- 텍스트는 즉시 서비스 대기열에 넣는다.
- 이미지는 앱 cache에 임시 staging 후 40 MiB staging cap과 40MP decode cap을 적용한다.
- PC 연결이 아직 없으면 memory pending queue에 유지하고 BLE credential 또는 P2P 연결 준비 시 자동 전송한다.
- 파일/폴더 공유는 받지 않는다.

### iOS / iPadOS

- Share Extension은 텍스트 또는 이미지 하나만 받는다.
- Share Extension 자체가 BLE/P2P 장기 연결을 수행하지 않는다.
- App Group `group.universe.dia.conphone`에 payload를 staging하고 Con.Phone 본 앱이 활성화될 때 읽는다.
- 작은 payload는 BLE, 큰 이미지는 사용자 승인형 P2P 경로로 전송한다.
- 120초 안에 transport가 준비되지 않으면 memory pending은 만료시키되 App Group payload는 유지하여 다음 앱 실행 때 재시도한다.
- 실제 transport queue에 들어간 뒤에만 App Group payload를 삭제한다.


## Windows 방화벽 진단

P2P 데이터 채널은 TCP 47911을 사용한다. Con.Phone은 시작 시 전용 방화벽 규칙 상태를 비권한 조회로 확인하며, 규칙이 없거나 확인되지 않아도 BLE fallback을 막지 않는다. 사용자가 P2P 허용 버튼을 누른 경우에만 UAC를 요청해 inbound rule을 생성하고 다시 검증한다.
