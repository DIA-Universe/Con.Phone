# LINKO 1.0.7 개발 빌드

**사용 중단 권장:** 연결 시 Windows 세션을 잘못 해제하는 결함이 실사용 로그에서
확인되었습니다. [1.0.8 수정 내역](LINKO-1.0.8.md)을 참고하세요.

Android GATT client와 Windows GATT server의 연결 수명주기를 정리했습니다.
기존 선택한 페어링 PC 전용 연결 정책, HID 입력 복구, 텍스트 동기화 및
전송 프로토콜을 유지합니다. 기존 이미지 송신 비활성화 정책도 변경하지 않습니다.

상세 원인, 수정 파일, 테스트 결과와 한계는 [BLE-LIFECYCLE.md](BLE-LIFECYCLE.md)를 참고하세요.

로컬 빌드:

```powershell
scripts/test-now.ps1 -Target android
dotnet build src/ConPhone.Windows -c Release
dotnet run --project tests/ConPhone.Tests -c Release
dotnet run --project src/ConPhone.Windows -c Release -- --release-selftest
```

실제 Windows 서버 생성·정리 20회 검사 (실행 중인 LINKO는 먼저 완전 종료):

```powershell
dotnet run --project src/ConPhone.Windows -c Release -- --ble-lifecycle-selftest
```

실기기 8개 시나리오를 실행하며 결과를 기록:

```powershell
scripts/test-ble-lifecycle.ps1 -Mode Hardware -AndroidSerial <adb-serial>
```

위 스크립트는 절차를 안내하고 사용자가 확인한 PASS/FAIL/미실행을 JSON으로
저장합니다. 로그 수집만으로 자동 PASS 판정을 내리지 않습니다.

`artifacts/ble-lifecycle-1.0.7/`에 실행 파일과 APK를 별도로 보관합니다.
[GitHub 1.0.7 검증 빌드](https://github.com/DIA-Universe/Con.Phone/releases/tag/v1.0.7)에서
EXE/APK를 받을 수 있습니다. 비공개 저장소이므로 접근 권한이 있는 계정으로
로그인해야 합니다. 기존 1.0.6 배포 파일은 변경하지 않았습니다.
