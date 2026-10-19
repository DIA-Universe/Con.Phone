# LINKO 1.0.5 · Bluetooth 입력 복귀 보완

## 확인한 문제

사용자가 보고한 `Bluetooth 전송 실패/지연` 메시지는 전송 루프가 예외를 받으면 즉시 PC 입력으로 돌아가는 경로다. 기존 보고서 전송 제한은 750ms여서, 연결 상태 감시의 1.5초 유예와 관계없이 한 번의 전송 지연으로 PC로 복귀했다. GATT 종료 메시지도 실제 현재 세션인지와 무관하게 출력되고 세션 관련 상태가 지워졌다.

Android 앱은 5초마다 클립보드 상태를 확인한다. 읽기 요청을 Android가 시작하지 못해 `false`를 반환하면 곧바로 재연결했다. 이는 실제 연결 끊김과 구분되지 않았다.

이 환경의 저장된 로그에는 보고된 08:14:50 사건이 없고, ADB에 연결된 휴대폰도 없었다. 따라서 실제 무선 단절의 원인은 확정하지 않았다.

## 변경

- Windows의 입력 전환마다 `ThroughputOptimized` 요청/해제를 반복하던 코드와 Android companion의 연결마다 HIGH 우선순위를 강제하던 코드를 제거했다. OS와 휴대폰이 협상한 연결 조건을 유지하고 Windows 마우스 전송은 조회된 연결 간격에 맞춘다.
- 구독 복구 대기와 진행 중인 HID 전송에 최대 3초를 허용한다. 실제 알림을 제출하기 전에 구독이 사라졌을 때만 기다렸다가 보낸다. 이미 제출한 상대 마우스 이동/휠/키 입력은 자동 재전송하지 않는다. 알림 실패 결과는 즉시 PC로 복귀하며 원인을 기록한다.
- 전송 및 UI 상태 감시의 복구 유예를 같은 상수로 맞췄다. F9로 즉시 PC 복귀 가능하며, 복구 제한을 넘겨 PC로 돌아온 뒤에는 자동으로 휴대폰 입력을 재개하지 않는다.
- 오래된 전송 완료/실패는 새 F8 활성화나 대상 변경을 취소하지 않는다. 실패 처리가 UI에 반영되기 전에는 다음 보고서를 보내지 않는다.
- 이전 GATT 세션의 이벤트가 교체된 세션의 상태를 지우지 못하게 했다. 종료 이벤트의 상태/오류/HID 준비 여부를 기록한다.
- Android 읽기 요청이 시작되지 않은 경우 250ms 간격으로 최대 3회 재시도한다. 실제로 시작한 요청은 중복하지 않고 기존 10초 응답 제한을 유지한다. 연결 종료 상태 코드도 표시한다.

API 근거: [Microsoft의 ThroughputOptimized 설명](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothlepreferredconnectionparameters.throughputoptimized), [Android BluetoothGatt 읽기 API](https://developer.android.com/reference/android/bluetooth/BluetoothGatt#readCharacteristic(android.bluetooth.BluetoothGattCharacteristic)). 고속 설정이 동시 연결 수에 영향을 줄 수 있다는 사실과 실제 사용자 환경의 단절 원인 확정은 구분한다.

## 검증

- Core 143개 검사 통과: 지연된 알림 1회 처리, 구독 복구 후 키/마우스 순서, 불명확한 전송 결과 재전송 금지, 영구 단절의 제한 시간, 이전 활성화의 뒤늦은 실패 무시, 종료 취소 포함.
- Windows Release 빌드: 오류/경고 0개.
- Windows release self-test 및 배포용 단일 EXE의 release self-test 통과.
- Android 18개 단위 테스트 및 debug APK 빌드 통과. APK 버전 1.0.5 / versionCode 15, 기존 1.0.4 APK와 동일한 서명 확인.
- 실제 휴대폰과 30분 혼합 입력, Bluetooth 재연결 및 F8/F9 검증은 필요하다. 코드 검사와 빌드 성공을 실기기 검증으로 간주하지 않는다.

## 적용

기존 Windows LINKO를 완전히 종료한 후 새 `LINKO.exe`를 실행한다. Android는 `LINKO-1.0.5.apk`를 기존 앱 위에 설치한다. 기존 페어링/기기 등록은 유지한다. 재발하면 도움말 로그의 구체적인 전송 오류와 Android의 연결 종료 상태 코드를 함께 확인한다.

로컬 바탕화면 `LINKO/LINKO.exe`는 1.0.4 파일을 백업한 후 1.0.5로 교체했다. APK도 같은 폴더에 복사했다. 휴대폰 APK 설치와 실제 Bluetooth 입력 검증은 아직 수행하지 않았다.
