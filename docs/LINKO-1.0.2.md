# LINKO 1.0.2

Windows EXE와 Android APK는 모두 1.0.2입니다. Android versionCode는 12이며 기존 앱 위에 설치합니다.

## iPhone / iPad 잠금과 PC 복귀

기존에는 Apple 프로필에서도 Galaxy의 Win+L을 전송했습니다. 이제 연결된 기기의 **입력 설정 → Apple 권장값**에서는 Control+Command+Q를 전송합니다. Ctrl/Command 교환을 다시 적용하지 않으며 Apple에는 Galaxy용 System Sleep을 덧붙이지 않습니다. Bluetooth HID 보고서 구성은 바꾸지 않았습니다.

PC 입력 복귀는 Bluetooth 응답을 기다리기 전에 실행합니다. 잠금 직후 휴대폰이 통신을 중단해도 PC 복귀를 취소하지 않고, 읽기 가능한 HID 보고서에 눌린 키를 남기지 않습니다.

Control+Command+Q가 기본 잠금 명령으로 동작하지 않는 iOS 버전/설정에서는 **설정 → 손쉬운 사용 → 키보드 → 전체 키보드 접근 → 명령 → 화면 잠금**에 해당 조합을 지정해야 합니다. 연결한 PC 키보드에서는 Ctrl+Windows+Q를 입력하면 됩니다. F8/F9는 기존대로 사용합니다. 전체 키보드 접근을 켜면 iOS의 다른 탐색 단축키도 활성화될 수 있습니다.

[Apple: 전체 키보드 접근 및 명령 사용자화](https://support.apple.com/guide/iphone/control-iphone-with-an-external-keyboard-ipha4375873f/ios)

## iOS 클립보드 안내

연결 탭과 클립보드 탭에 다음 제한을 표시합니다: iOS는 보안·클립보드 접근 제한으로 LINKO의 PC ↔ iPhone/iPad 복사·붙여넣기를 지원하지 않습니다. iOS 기기 안에서의 복사·붙여넣기는 그대로 사용할 수 있습니다. 이는 앱 없이 HID만 사용하는 LINKO의 지원 범위이며, iOS의 모든 앱에서 복사·붙여넣기가 불가능하다는 뜻은 아닙니다.

## Android 업데이트

- 릴리스 목록 대신 `/releases/latest`로 최신 정식 버전을 확인합니다.
- `/latest`에서 `/tag/<version>`으로 이동해도 조회를 계속하고 그 태그의 APK를 선택합니다.
- 웹 캐시를 끄고, APK 링크를 직접 누르는 경우도 앱의 다운로드·서명 검증·설치 경로로 연결합니다.
- GitHub 로그인 쿠키는 github.com에만 전송합니다. 다른 저장소/서버/앱/서명/구버전은 설치하지 않습니다.
- 비공개 저장소이므로 접근 권한이 있는 GitHub 계정 로그인이 필요합니다. APK에 계정 토큰을 포함하지 않습니다.
- 기존 업데이터가 실패하면 이번 1.0.2 APK는 바탕화면 파일이나 릴리스에서 한 번 내려받아 덮어 설치하세요. 설치 권한과 Android 설치 확인은 사용자가 승인해야 합니다.

## 검증과 한계

핵심 HID·입력·텍스트 전송 회귀 검사, Windows 시작 실패 격리 검사, Android 업데이트 경로 단위 테스트, EXE/APK 로컬 빌드를 수행합니다. iOS 실기기의 화면 잠금과 Android WebView 로그인부터 설치까지의 종단 검증은 연결된 휴대폰 없이 확인할 수 없습니다. HID 알림 전송 성공만으로 화면 잠금 성공을 표시하지 않습니다.
