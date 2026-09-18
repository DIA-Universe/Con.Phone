# Con.Phone

**노트북의 키보드·마우스 → 내장 Bluetooth → 갤럭시(Android) / iPhone.**

추가 동글이나 USB 연결 없이 사용하는 Windows용 무선 입력 프로그램입니다. 키보드·마우스는 표준 BLE HID로 동작합니다. 텍스트/이미지 기기간 복붙을 사용할 때만 Android/iOS용 Con.Phone Companion을 함께 사용합니다.

> **0.1 실험 버전입니다. 두 운영체제 모두 실제 연결·입력을 아직 검증하지 않았습니다.** Windows 빌드와 자동 테스트 통과는 실기기 호환성 인증이 아닙니다. 첫 실기기 시험 대상은 갤럭시이며 iPhone 검증은 미완료로 유지합니다. 이지블루보다 빠르거나 안정적이라는 성능 주장을 하지 않습니다.

## 다운로드 / 실행

저장소의 **Actions → Windows build and core tests → 성공한 실행 → Artifacts → ConPhone-win-x64**에서 내려받아 전체 압축을 푼 뒤 `ConPhone.exe`를 실행합니다. .NET 런타임을 포함하는 x64 배포본입니다. 코드 서명은 되어 있지 않습니다. 조직 보안 정책을 우회하거나 백신을 끄지 마세요.

**이름 주의:** GitHub 저장소/제품 이름은 Con.Phone이지만, Windows에서는 `CON` 및 `CON.*`가 예약되어 있습니다. 로컬 폴더와 실행 파일은 반드시 `ConPhone`처럼 점을 뺀 이름을 사용합니다. 저장소 이름 자체를 바꿀 필요는 없습니다.

## 갤럭시에서 첫 시험

1. Windows와 갤럭시의 Bluetooth를 켭니다. 케이블이나 추가 동글은 사용하지 않습니다.
2. 앱의 **Bluetooth 검사**에서 `Peripheral 역할: True`인지 확인합니다. False이면 이 내장 어댑터/드라이버 조합에서 현재 방식은 동작하지 않습니다.
3. **휴대폰 검색 허용**을 누르고 갤럭시의 Bluetooth 설정에서 이 PC를 검색합니다. 이름은 Windows가 제공하므로 반드시 `Con.Phone`으로 표시되는 것은 아닙니다. 양쪽 페어링 요청과 코드/기기 이름을 직접 확인하세요.
4. 앱이 HID 연결 목록을 자동 갱신합니다. 키보드·마우스가 모두 True이고 페어링도 True인 본인 기기를 **승인**합니다. 기기가 하나면 자동 선택됩니다. 구독 목록이 비어 있으면 아직 HID로 연결된 것이 아닙니다.
5. 갤럭시에서 Samsung Notes 등 비민감한 시험 입력 화면을 열고, 누른 키·마우스 버튼을 모두 놓은 다음 **F8**을 눌러 전환합니다. **F9**는 즉시 노트북 복귀입니다.
6. 갤럭시 설정의 물리 키보드 메뉴에서 한국어 배열/언어 전환 설정을 확인합니다. 메뉴 위치는 One UI 버전에 따라 다릅니다. `안녕하세요`, `SAP CO 2026`, 받침 수정과 드래그·휠을 시험합니다.
7. 실패하면 **진단 저장**으로 JSON을 저장합니다. 입력 내용, 키코드, 페어링 코드, 기기 이름·주소는 내보내지 않습니다.

**광고 시작 → OS 페어링 → HID 구독 → 기기 승인 → 입력 반영**은 각각 다른 단계입니다. 광고가 시작됐다는 이유로 입력 가능하다고 표시하지 않습니다.

## iPhone

같은 BLE HID 구현을 사용합니다. 별도 iPhone 제어 API나 가짜 연결 화면을 넣지 않았습니다. 포인터를 사용하려면 iPhone에서 `설정 → 손쉬운 사용 → 터치 → AssistiveTouch`를 확인해야 합니다. iPhone 기기가 없어 실제 발견·페어링·한글·드래그·스크롤은 검증하지 않았습니다. 프로필 선택은 안내/진단 분류이며 호환성 보증이 아닙니다.

## 현재 구현

- Windows LE Peripheral 지원/무선 상태 진단, HID 서비스 0x1812 광고.
- 키보드·상대 마우스 HID Report Map, Report Reference, Boot Protocol, Control Point, LED 출력 처리.
- 암호화된 특성 접근, OS 페어링 확인, 사용자가 선택한 단일 기기에만 알림 전송.
- 별도 스레드의 입력 수집, F8 전환/F9 복귀, 한글·한자 물리 키 매핑.
- 순서를 보존하는 제한된 전송 큐, 버튼/키 경계를 넘지 않는 마우스 이동 병합.
- 키 해제 보고서, 오류·구독 변경·절전·잠금 시 로컬 복귀 및 자동 원격 재개 방지.
- 트레이 상태, 입력 내용을 제외한 진단 저장, 자체 동작 테스트, Windows 자동 빌드.

## 중요 제약

- Windows 11 x64 + 내장 Bluetooth LE Peripheral 지원을 우선 대상으로 합니다. Windows 10 API를 사용하지만 모든 OS/칩셋 지원을 보장하지 않습니다.
- Windows가 GAP/DIS 등 일부 서비스를 관리하므로 이 프로그램은 이를 재정의하지 않습니다. 휴대폰의 HID 탐색 정책 때문에 PC가 목록에 안 보이거나 페어링 후 구독이 안 될 수 있습니다. 이는 **실기기 검증이 필요한 기술 위험**입니다.
- F8/F9는 앱 실행 중 예약 키입니다. 휴대폰에 그대로 전달하지 않습니다. 펜/태블릿 등 절대 좌표 장치는 이 버전에서 지원하지 않습니다.
- 보안 데스크톱, 관리자 앱, UAC, Ctrl+Alt+Delete는 일반 앱이 완전히 제어할 수 없습니다. 관리자 권한으로 강제 실행하거나 보안 기능을 끄지 않습니다.
- 연결이 이미 끊긴 뒤에는 휴대폰에 키 해제 신호를 보낼 수 없습니다. 로컬 입력부터 복구하며, 휴대폰에 눌림이 남으면 Bluetooth 연결을 해제해야 합니다.
- 파일/폴더 전송과 화면 미러링은 포함하지 않습니다. 텍스트/이미지 복붙은 Companion 데이터 채널을 사용하며, 같은 LAN/P2P가 있으면 고속 암호화 채널을 우선하고 없으면 BLE로 fallback합니다. 동일 Wi-Fi는 필수가 아닙니다.

## 텍스트 · 이미지 복붙

클립보드 동기화는 HID와 분리되어 있습니다.

- Windows → 휴대폰: Windows에서 복사 후 휴대폰 입력으로 전환하면 최신 텍스트/이미지를 Companion으로 동기화합니다.
- 휴대폰 → Windows: 모바일 OS의 클립보드 개인정보 보호 정책을 지키기 위해 Companion의 **현재 텍스트/이미지를 PC로 보내기** 동작을 사용합니다.
- 전송 우선순위: LAN/P2P 암호화 채널 → BLE fallback.
- 파일/폴더는 전송하지 않습니다.

Android 프로젝트는 companion/android, iOS 프로젝트 소스는 companion/ios에 있습니다. 세부 프로토콜·보안·제약은 docs/CLIPBOARD_SYNC.md를 참고하세요.

## 개발

.NET 10 SDK와 Windows 개발 환경이 필요합니다. Windows에서 clone할 때 대상 디렉터리를 명시합니다.

```powershell
git clone https://github.com/DIA-Universe/Con.Phone.git ConPhone
cd ConPhone
dotnet run --project tests/ConPhone.Tests -c Release
dotnet build src/ConPhone.Windows -c Release
dotnet run --project src/ConPhone.Windows
dotnet publish src/ConPhone.Windows -c Release -r win-x64 --self-contained true -o artifacts/ConPhone-win-x64
```

기술 구조와 검증 범위는 `docs/ARCHITECTURE.md`, 실기기 시험표는 `docs/TESTING.md`를 확인하세요.
