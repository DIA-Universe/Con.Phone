# LINKO

PC의 키보드와 마우스를 Bluetooth로 휴대폰에서 사용하세요.

**Windows / Android 1.0.6** · [다운로드](https://github.com/DIA-Universe/Con.Phone/releases/tag/v1.0.6)

연결 끊김 후속 수정 빌드 **1.0.10**: [다운로드](https://github.com/DIA-Universe/Con.Phone/releases/tag/v1.0.10) · [수정 내용과 검증 범위](docs/LINKO-1.0.10.md). 1.0.9에 남아 있던 클립보드 timeout의 연결 종료, Windows 검색 신호 복구 실패의 HID 서비스 폐기, 검색 신호 오류 시 기기 목록 갱신 중단을 수정했습니다. **Windows EXE와 Android APK를 함께 업데이트하세요.** 실기기에서 끊김과 재연결 문제가 해소됐는지는 아직 확인하지 못했습니다. 1.0.7은 활성 GATT 세션을 닫는 결함이 확인된 버전입니다.

## 시작하기

1. Windows에서 `LINKO.exe`를 실행합니다.
2. 휴대폰 Bluetooth 설정에서 이 PC를 연결합니다. 표시되는 이름은 Windows PC 이름일 수 있습니다.
3. LINKO에서 기기를 선택하고 **이 기기 사용**을 누릅니다.
4. **F8**로 휴대폰을 조작하고 **F9**로 PC로 돌아옵니다. 1.0.9에서는 PC 사용 중 F9가 일반 키로 동작합니다.

## iPhone · iPad

앱 설치 없이 키보드·마우스만 사용합니다. iPhone은 **설정 → 손쉬운 사용 → 터치 → AssistiveTouch**를 켜고 **기기 → Bluetooth 기기**에서 연결하세요. iPad는 **설정 → Bluetooth**에서 연결합니다. PC 입력 설정에 **Apple 권장값**이 있습니다.

실제 iPhone/iPad와 Windows 어댑터 조합의 동작은 아직 검증하지 못했습니다. [Apple 연결 안내](https://support.apple.com/en-gb/111775)를 참고하세요.

## Android 클립보드

`LINKO-1.0.6.apk`를 설치하고 처음 실행할 때 Bluetooth 권한을 허용하세요. 홈의 **연결할 PC 선택**에서 페어링한 PC를 한 번 선택하면 이후 해당 PC에만 자동 연결합니다. 기존에 저장된 PC는 그대로 사용합니다. 주변 키보드·마우스를 검색해 자동 접속하지 않습니다. 텍스트 동기화는 기본으로 켜져 있으며 기존 Con.Phone / LINKO에 덮어 설치할 수 있습니다.

**이미지는 전달되지 않습니다.** PC에서 복사한 텍스트는 휴대폰으로 자동 전송됩니다.

휴대폰의 **다른 앱에서 복사 → PC 자동 전송**은 LINKO의 **다른 앱에서도 자동 복사 설정**에서 설정합니다. 삼성 키보드를 그대로 유지하며, [Shizuku](https://shizuku.rikka.app/download/) 설치 후 무선 또는 USB 디버깅으로 시작하고 LINKO 권한을 승인해야 합니다. 재부팅하면 Shizuku를 다시 시작해야 합니다. 일반 Bluetooth 권한만으로 Android 백그라운드 클립보드를 읽을 수는 없습니다. Shizuku 설정 전이나 중지 상태에서는 LINKO 화면을 열거나 **공유 → LINKO**로 텍스트를 보낼 수 있습니다. 이 자동 모드의 Galaxy S25+ 실기기 검증은 아직 남아 있습니다.

## 설정과 도움말

감도·한영키·단축키는 **입력 설정**, 연결 상세 정보와 로그는 **도움말**에 있습니다. 마우스 속도는 1–300% 범위에서 **1% 단위**로 직접 입력하거나 화살표로 조절하고 저장합니다. 파일 전송과 화면 미러링은 포함하지 않습니다. Windows Bluetooth LE Peripheral 지원이 필요합니다.

## 개발 / 검증

.NET 10, JDK 17, Android SDK 35를 사용합니다. `scripts/test-now.ps1 -Target all`은 EXE/APK 버전 일치를 검사한 뒤 로컬에서 빌드합니다. GitHub Actions를 실행할 필요가 없습니다.

1.0.6은 Android의 주변 HID 기기 자동 접속과 Windows의 상대 기기 접근/연결 유지 요청을 제거합니다. 기존 3초 입력 복구 유예는 유지합니다. 검증 범위와 실기기 확인이 남은 항목은 [1.0.6 검토 기록](docs/LINKO-1.0.6.md), 자동 복사 설정은 [1.0.0 기록](docs/LINKO-1.0.0.md)에 정리했습니다. 저장소 주소와 기존 설치 데이터 식별자는 호환성을 위해 유지합니다.

