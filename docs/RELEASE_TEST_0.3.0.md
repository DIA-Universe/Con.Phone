# Con.Phone 0.3.0 실기기 릴리스 체크리스트

자동화가 통과해도 아래 항목은 실제 Windows PC + Galaxy/iPhone에서 확인해야 한다.

## 공통 준비

- Windows Mobile Hotspot OFF
- Windows Bluetooth ON
- 휴대폰에서 이전 Con.Phone HID 등록 삭제 후 0.3.0으로 재페어링
- Con.Phone Companion 0.3.0 설치
- 같은 Wi-Fi에는 연결하지 않은 상태로 테스트
- 클립보드 동기화 ON

## Galaxy / Android

1. K/M/LOCK/MEDIA 구독 상태 확인
2. PC -> Galaxy 입력 전환 즉시성 확인
3. 마우스 연속 이동 60초: 버벅임/포인터 trail/입력 누락 확인
4. 클릭/드래그/휠 우선순위 확인
5. 한영키 Right Alt 기본 확인
6. 실패 시 LANG1 -> Ctrl+Space -> Shift+Space 순서 확인
7. WIN+L 잠금 확인
8. 볼륨 +/-, 음소거, 재생/일시정지, 이전/다음 확인
9. PC 텍스트 복사 -> 입력 전환 -> Galaxy 붙여넣기
10. 작은 PNG 이미지 BLE fallback 확인
11. 큰 이미지에서 Direct Wi-Fi/P2P 승인 -> TCP/AES-GCM 전송 확인
12. Android -> PC 현재 클립보드 보내기 확인
13. 다른 앱에서 공유 -> Con.Phone -> PC 텍스트/이미지 전송 확인
13. 다른 Android 앱에서 텍스트 공유 -> Con.Phone -> PC 자동 대기/전송 확인
14. 다른 Android 앱에서 이미지 공유 -> Con.Phone -> PC 자동 대기/전송 확인
15. 공유 직후 PC 연결이 없어도 연결 후 pending payload가 자동 전송되는지 확인
16. P2P 연결 중 Bluetooth 순간 단절 시 데이터 채널 유지/복구 확인

## iPhone / iPad

1. HID 키보드/포인터 연결 확인
2. 텍스트 BLE clipboard 수신 확인
3. 이미지 BLE fallback 확인
4. "P2P 고속 연결" 버튼 -> iOS Wi-Fi 승인 -> P2P TCP 연결 확인
5. 큰 이미지 전송 후 대상 앱 붙여넣기 확인
6. iOS -> PC 사용자 버튼 전송 확인
7. 다른 앱에서 공유 -> Con.Phone Share Extension -> 앱 활성화 -> PC 자동 전송 확인
7. 다른 iOS 앱에서 텍스트 공유 -> Con.Phone Share Extension -> 앱 실행 -> PC 자동 전송 확인
8. 다른 iOS 앱에서 이미지 공유 -> App Group staging -> 앱 실행 -> PC 자동 전송 확인
9. PC 연결이 120초 안에 준비되지 않으면 App Group payload가 유지되어 다음 실행에 재시도되는지 확인
10. 앱 background/foreground 전환 후 BLE 재연결 확인
11. iPad/Mac 프로필에서 Ctrl <-> Command 매핑 확인

## 다중기기

1. 최대 5대 등록
2. 등록 목록이 앱 재시작 후 유지
3. 활성 대상 전환 시 HID provider 재시작 없이 즉시 전환
4. 원격 입력 중 F7 -> 이전 기기 release -> 다음 기기 원격 입력 유지 확인
4. 이전 대상에 키/마우스가 새어가지 않는지 확인
5. 비활성 Companion이 P2P 설정을 읽지 못하는지 확인
6. 비활성 Companion의 clipboard write가 거부되는지 확인
7. 활성 대상 변경 시 이전 P2P AP 세션이 끊기고 새 대상만 연결되는지 확인

## Windows 절전 / 잠금 / 재부팅

1. 절전 진입 시 로컬 입력 복귀
2. 절전 복귀 후 HID 검색 신호/구독 복구
3. Windows 잠금 후 입력 상태 정상화
4. 앱 재시작 후 신뢰 기기/활성 대상 복구
5. 재부팅 후 재페어링 없이 HID 재연결 가능한지 확인

## 업데이트

1. 0.3.0 single-file ConPhone.exe를 별도 경로에서 선택
2. stage -> 기존 프로세스 종료 -> 파일 교체 -> 재실행 확인
3. 낮은/동일 버전 적용 시 경고 확인
4. 쓰기 권한 없는 위치에서 오류 메시지 확인
5. 설정/신뢰 기기/클립보드 기록이 업데이트 후 유지되는지 확인

## 실패 시 반드시 저장할 진단

- ConPhone diagnostics JSON
- Windows 버전
- Bluetooth 어댑터/드라이버 버전
- Galaxy/iPhone 모델 및 OS 버전
- K/M/LOCK/MEDIA 구독 수
- MousePacingIntervalMs
- ClipboardP2PLinkRunning
- ClipboardP2PChannelConnected
- ClipboardP2PHostReady

클립보드 본문, 입력 문자, raw Bluetooth device id, P2P 비밀번호는 진단 파일에 넣지 않는다.
