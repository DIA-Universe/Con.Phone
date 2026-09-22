[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Hardware')][string]$Mode = 'Plan',
    [ValidateRange(1, 8)][int[]]$Test = @(1, 2, 3, 4, 5, 6, 7, 8),
    [string]$Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
    [string]$AndroidSerial,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\ble-hardware')
)
$ErrorActionPreference = 'Stop'
$cases = @(
    @('정상 연결 / 양방향 UTF-8', 'Windows와 Android의 수정 빌드를 실행하고 등록된 PC를 선택합니다. Windows → Android, Android → Windows로 서로 다른 한글·이모지 텍스트를 복사합니다. 휴대폰 앱을 앞에 두고 실제 대상 클립보드를 확인합니다. P2P 경로가 아닌 BLE 전송 로그와 적용 ACK를 확인합니다.', '두 방향의 텍스트가 정확히 일치하고 적용 ACK가 도착함'),
    @('Windows 강제 종료', '연결된 상태에서 작업 관리자 → LINKO → 작업 끝내기를 실행합니다. 같은 수정 EXE를 다시 실행합니다. 기기 재부팅이나 페어링 삭제 없이 Android 자동 재연결과 양방향 텍스트를 확인합니다.', '재부팅 없이 새 세션 CONNECTED 및 양방향 전송 성공'),
    @('Android 강제 종료', '연결된 상태에서 Android 설정 → 앱 → LINKO → 강제 중지를 누릅니다. LINKO를 다시 열고 연결을 확인합니다. 강제 중지 상태에서는 Android가 앱 자동 실행을 막으므로 반드시 앱을 다시 여세요.', '앱 재실행 후 재부팅 없이 연결과 양방향 전송 성공'),
    @('양쪽 Bluetooth OFF/ON', '연결 후 휴대폰 Bluetooth를 OFF, 3초 뒤 ON 합니다. 복구와 양방향 전송을 확인합니다. 이어 Windows Bluetooth도 OFF, 3초 뒤 ON 하고 같은 검사를 합니다. 앱은 종료하지 않습니다.', '두 장치 각각 앱 재시작 없이 새 세션 연결 및 전송 성공'),
    @('연결 버튼 10회 연타', 'Android 설정 탭의 연결 해제로 시작합니다. 홈의 연결 버튼을 빠르게 10회 누릅니다. 이 구간 Android 로그에서 CONNECTING 세션이 하나인지 확인합니다. 기존 PC 선택은 유지합니다.', '버튼 10회에 실제 connectGatt 시도 한 개, 연결 성공'),
    @('CONNECTING 중 연결 요청', '연결 해제 후 연결을 누릅니다. Android 로그의 state=CONNECTING 직후 연결 버튼을 다시 누릅니다. 재현이 어려우면 PC를 잠시 종료한 상태에서 시작하되 15초 timeout 전에 추가 클릭하고 PC를 다시 실행합니다.', '첫 시도가 끝나기 전 추가 CONNECTING 세션이 없고 기존 시도 유지'),
    @('20회 연결 / 해제', 'Android 연결 → CONNECTED 확인 → 설정의 연결 해제 → cleanup completed 확인을 20회 반복합니다. 매 회 새 텍스트 송수신도 확인합니다. 각 세션의 GATT closed가 정확히 한 번인지 검사합니다. 마지막에는 연결 상태로 둡니다.', '20회 모두 재부팅 없이 성공, 종료된 각 세션 close 한 번, 이후 정상 전송'),
    @('전송 중 연결 종료', '64 KiB 이상의 비민감 테스트 텍스트를 BLE로 전송하는 중 휴대폰 Bluetooth를 OFF 합니다. ON 후 새 짧은 텍스트를 양방향 전송합니다. 대기 중이던 내용은 기존 TTL 정책에 따라 새 전송으로 재시도될 수 있습니다. 현재 제품은 이미지 송신이 비활성화되어 있어 큰 텍스트로 실기기 검증하며 PNG는 별도 프로토콜 회귀 테스트로 검사합니다.', '충돌 없음, 이전 세션의 write/ACK 대기 취소, 새 세션 정상 송수신')
)

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resultPath = Join-Path $OutputDirectory ('results-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$results = foreach ($number in $Test) {
    $case = $cases[$number - 1]
    [pscustomobject]@{ Test = $number; Name = $case[0]; Steps = $case[1]; PassCondition = $case[2]; Result = '미실행'; Evidence = ''; AndroidLog = '' }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding utf8
foreach ($item in $results) {
    Write-Host "`nTest $($item.Test): $($item.Name)"
    Write-Host $item.Steps
    Write-Host "PASS 조건: $($item.PassCondition)"
    if ($Mode -eq 'Plan') { continue }
    $startTime = Get-Date -Format 'MM-dd HH:mm:ss.fff'
    Read-Host '위 절차를 실행한 다음 Enter를 누르세요' | Out-Null
    if ($AndroidSerial -and (Test-Path -LiteralPath $Adb)) {
        $logPath = Join-Path $OutputDirectory "test-$($item.Test)-android.log"
        & $Adb -s $AndroidSerial logcat -d -v threadtime -T $startTime 'LINKO-BLE:I' '*:S' 2>&1 |
            Set-Content -LiteralPath $logPath -Encoding utf8
        $item.AndroidLog = $logPath
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Android 로그 수집 실패: 결과 판정 전에 기기에서 확인하세요.' }
    }
    do { $answer = (Read-Host '실제 결과를 입력하세요: PASS / FAIL / SKIP').Trim().ToUpperInvariant() }
    while ($answer -notin @('PASS', 'FAIL', 'SKIP'))
    $item.Result = if ($answer -eq 'SKIP') { '미실행' } else { $answer }
    $item.Evidence = Read-Host '확인한 세션 번호, 양방향 일치 여부 또는 실패 증상 (클립보드 내용은 입력하지 마세요)'
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding utf8
}
Write-Host "`n결과 기록: $resultPath"
if ($Mode -eq 'Plan') { Write-Host '계획만 생성했습니다. 실기기 검사는 수행하지 않았습니다.' }
