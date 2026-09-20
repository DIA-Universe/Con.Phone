# LINKO

PC의 키보드와 마우스를 Bluetooth로 휴대폰에서 사용하세요.

**Windows / Android 0.4.0** · [최신 다운로드](https://github.com/DIA-Universe/Con.Phone/releases)

## 시작하기

1. Windows에서 `LINKO.exe`를 실행합니다.
2. 휴대폰 Bluetooth 설정에서 이 PC를 연결합니다. 표시되는 이름은 Windows PC 이름일 수 있습니다.
3. LINKO에서 기기를 선택하고 **이 기기 사용**을 누릅니다.
4. **F8**로 휴대폰을 조작하고 **F9**로 PC로 돌아옵니다.

## iPhone · iPad

앱 설치 없이 키보드·마우스만 사용합니다. iPhone은 **설정 → 손쉬운 사용 → 터치 → AssistiveTouch**를 켜고 **기기 → Bluetooth 기기**에서 연결하세요. iPad는 **설정 → Bluetooth**에서 연결합니다. PC 입력 설정에 **Apple 권장값**이 있습니다.

실제 iPhone/iPad와 Windows 어댑터 조합의 동작은 아직 검증하지 못했습니다. [Apple 연결 안내](https://support.apple.com/en-gb/111775)를 참고하세요.

## Android 클립보드

`LINKO-0.4.0.apk`를 설치하고 PC의 **클립보드 → 기기간 복붙 사용**을 켜세요. 휴대폰에서 복사한 내용은 LINKO를 열거나 **공유 → LINKO**로 보냅니다. 받은 이미지는 **이미지 복사 / 이미지 공유**로 사용할 수 있습니다. 기존 Con.Phone 앱에 덮어 설치할 수 있습니다.

## 설정과 도움말

감도·한영키·단축키는 **입력 설정**, 연결 상세 정보와 로그는 **도움말**에 있습니다. 파일 전송과 화면 미러링은 포함하지 않습니다. Windows Bluetooth LE Peripheral 지원이 필요합니다.

## 개발 / 검증

.NET 10, JDK 17, Android SDK 35를 사용합니다. `scripts/test-now.ps1 -Target all`은 EXE/APK 버전 일치를 검사한 뒤 로컬에서 빌드합니다. GitHub Actions를 실행할 필요가 없습니다.

검증 범위와 아직 확인하지 못한 항목은 [0.4.0 검토 기록](docs/LINKO-0.4.0.md)에 정리했습니다. 저장소 주소와 기존 설치 데이터 식별자는 호환성을 위해 유지합니다.
