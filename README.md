# LINKO

PC의 키보드와 마우스를 Bluetooth로 휴대폰에서 사용하세요.

**자동 전환 테스트 버전 1.1.0-preview.1** · [테스트 EXE 다운로드](https://github.com/DIA-Universe/Con.Phone/releases/download/v1.1.0-preview.1/LINKO.exe) · [사용법과 검증 범위](docs/LINKO-1.1.0-preview.1.md)

휴대폰 앱 없이 PC 화면의 왼쪽 또는 오른쪽 끝에서 휴대폰으로 넘어가는 기능을 추가했어요. **입력 설정 → 이동 범위 보정**을 마치면 자동 전환이 켜져요. 돌아올 때는 보정한 이동량으로 위치를 추정하므로 실제 휴대폰 경계와 어긋날 수 있어요. Bluetooth 전송과 Windows 입력 처리를 분리하고 지연 시 PC로 복귀하도록 바꿨어요. Android·iPhone 실기기 장시간 검증은 남아 있어 테스트 버전으로 배포해요. 정식 최신 버전은 아래 1.0.15를 유지해요.

**Windows / Android 1.0.15** · [EXE 다운로드](https://github.com/DIA-Universe/Con.Phone/releases/download/v1.0.15/LINKO.exe) · [Android APK](https://github.com/DIA-Universe/Con.Phone/releases/download/v1.0.15/LINKO-1.0.15.apk) · [릴리스](https://github.com/DIA-Universe/Con.Phone/releases/tag/v1.0.15)

**1.0.15 연결 안정화:** 연결 중 검색 신호 오류만으로 Bluetooth 서비스를 반복 게시하지 않도록 바꿨습니다. Android LINKO는 외부 키보드·마우스 연결 상태가 바뀌어도 화면을 유지하고, 같은 알림을 반복 갱신하지 않습니다. PC의 EXE를 교체하고 Android LINKO를 사용한다면 APK도 함께 업데이트하세요. 기존 페어링과 설정은 유지합니다. 자동 검사와 Windows 네이티브 서비스 검사는 통과했지만, 사용자가 겪은 실제 휴대폰의 끊김·화면 깜빡임 개선 여부는 아직 확인하지 못했습니다. [변경 내용과 검증 범위](docs/LINKO-1.0.15.md).

## 시작하기

1. Windows에서 `LINKO.exe`를 실행합니다.
2. 휴대폰 Bluetooth 설정에서 이 PC를 연결합니다. 표시되는 이름은 Windows PC 이름일 수 있습니다.
3. LINKO에서 기기를 선택하고 **이 기기 사용**을 누릅니다.
4. **F8**로 휴대폰을 조작하고 **F9**로 PC로 돌아옵니다. 1.0.9에서는 PC 사용 중 F9가 일반 키로 동작합니다.

## iPhone · iPad

앱 설치 없이 키보드·마우스만 사용합니다. iPhone은 **설정 → 손쉬운 사용 → 터치 → AssistiveTouch**를 켜고 **기기 → Bluetooth 기기**에서 연결하세요. iPad는 **설정 → Bluetooth**에서 연결합니다. PC 입력 설정에 **Apple 권장값**이 있습니다.

실제 iPhone/iPad와 Windows 어댑터 조합의 동작은 아직 검증하지 못했습니다. [Apple 연결 안내](https://support.apple.com/en-gb/111775)를 참고하세요.

## Android 클립보드

`LINKO-1.0.15.apk`를 설치하고 처음 실행할 때 Bluetooth 권한을 허용하세요. 홈의 **연결할 PC 선택**에서 페어링한 PC를 한 번 선택하면 이후 해당 PC에만 자동 연결합니다. 기존에 저장된 PC는 그대로 사용합니다. 주변 키보드·마우스를 검색해 자동 접속하지 않습니다. 텍스트 동기화는 기본으로 켜져 있으며 기존 Con.Phone / LINKO에 덮어 설치할 수 있습니다.

**이미지는 전달되지 않습니다.** PC에서 복사한 텍스트는 휴대폰으로 자동 전송됩니다.

휴대폰의 **다른 앱에서 복사 → PC 자동 전송**은 LINKO의 **다른 앱에서도 자동 복사 설정**에서 설정합니다. 삼성 키보드를 그대로 유지하며, [Shizuku](https://shizuku.rikka.app/download/) 설치 후 무선 또는 USB 디버깅으로 시작하고 LINKO 권한을 승인해야 합니다. 재부팅하면 Shizuku를 다시 시작해야 합니다. 일반 Bluetooth 권한만으로 Android 백그라운드 클립보드를 읽을 수는 없습니다. Shizuku 설정 전이나 중지 상태에서는 LINKO 화면을 열거나 **공유 → LINKO**로 텍스트를 보낼 수 있습니다. 이 자동 모드의 Galaxy S25+ 실기기 검증은 아직 남아 있습니다.

## 설정과 도움말

감도·한영키·단축키는 **입력 설정**, 연결 상세 정보와 로그는 **도움말**에 있습니다. 마우스 속도는 1–300% 범위에서 **1% 단위**로 직접 입력하거나 화살표로 조절하고 저장합니다. 파일 전송과 화면 미러링은 포함하지 않습니다. Windows Bluetooth LE Peripheral 지원이 필요합니다.

## 개발 / 검증

.NET 10, JDK 17, Android SDK 35를 사용합니다. `scripts/test-now.ps1 -Target all`은 EXE/APK 버전 일치를 검사한 뒤 로컬에서 빌드합니다. GitHub Actions를 실행할 필요가 없습니다.

1.0.6은 Android의 주변 HID 기기 자동 접속과 Windows의 상대 기기 접근/연결 유지 요청을 제거했습니다. 당시 3초였던 입력 전송 유예는 1.1.0-preview.1에서 500ms로 줄였습니다. 과거 검증 범위는 [1.0.6 검토 기록](docs/LINKO-1.0.6.md), 자동 복사 설정은 [1.0.0 기록](docs/LINKO-1.0.0.md)에 정리했습니다. 저장소 주소와 기존 설치 데이터 식별자는 호환성을 위해 유지합니다.

