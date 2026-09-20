param(
    [ValidateSet("all", "windows", "android")]
    [string]$Target = "all"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo "artifacts/test-now"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

function Require-Command([string]$Name, [string]$Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name 명령을 찾지 못했습니다. $Hint"
    }
}

Write-Host "Con.Phone 0.3.0 TEST NOW" -ForegroundColor Cyan
Write-Host "Repo: $repo"

if ($Target -in @("all", "windows")) {
    Require-Command "dotnet" ".NET 8 SDK를 설치한 뒤 다시 실행하세요: https://dotnet.microsoft.com/download/dotnet/8.0"

    Write-Host ""
    Write-Host "== Windows/Core 검증 + EXE 생성 ==" -ForegroundColor Cyan
    $verifyArgs = @("-Configuration", "Release", "-OutputRoot", (Join-Path $artifacts "windows"))
    & (Join-Path $PSScriptRoot "verify-windows.ps1") @verifyArgs

    $exe = Join-Path $artifacts "windows/win-x64/ConPhone.exe"
    if (!(Test-Path $exe -PathType Leaf)) {
        throw "Windows 테스트 EXE가 생성되지 않았습니다."
    }

    Write-Host "Windows TEST EXE: $exe" -ForegroundColor Green
}

if ($Target -in @("all", "android")) {
    $gradle = Get-Command "gradle" -ErrorAction SilentlyContinue
    if ($null -eq $gradle) {
        Write-Warning "Gradle을 찾지 못해 Android APK 빌드는 건너뜁니다."
        Write-Host "Android Studio에서 companion/android 을 열고 app > assembleDebug 를 실행하세요."
    }
    else {
        Write-Host ""
        Write-Host "== Android Companion APK ==" -ForegroundColor Cyan
        & gradle --no-daemon -p (Join-Path $repo "companion/android") :app:assembleDebug

        $apk = Join-Path $repo "companion/android/app/build/outputs/apk/debug/app-debug.apk"
        if (!(Test-Path $apk -PathType Leaf)) {
            throw "Android debug APK가 생성되지 않았습니다."
        }

        $apkOut = Join-Path $artifacts "ConPhone-Companion-0.3.0-debug.apk"
        Copy-Item $apk $apkOut -Force
        Write-Host "Android TEST APK: $apkOut" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "== 실기기 최소 테스트 순서 ==" -ForegroundColor Cyan
Write-Host "1. Windows Mobile Hotspot을 끕니다."
Write-Host "2. 휴대폰의 기존 Con.Phone Bluetooth 등록을 삭제하고 다시 페어링합니다."
Write-Host "3. Windows ConPhone.exe와 Android/iOS Companion 0.3.0을 실행합니다."
Write-Host "4. 같은 Wi-Fi에는 연결하지 않은 상태에서 테스트합니다."
Write-Host "5. 마우스 60초 이동/드래그/휠, 한영키, WIN+L, 미디어키를 확인합니다."
Write-Host "6. PC 텍스트 복사 -> 휴대폰 전환 -> 붙여넣기를 확인합니다."
Write-Host "7. 큰 이미지는 Direct Wi-Fi/P2P 승인을 거쳐 전송되는지 확인합니다."
Write-Host "8. 휴대폰 -> PC는 보내기 버튼 또는 공유 -> Con.Phone으로 확인합니다."
Write-Host ""
Write-Host "자세한 체크리스트: docs/RELEASE_TEST_0.3.0.md"
