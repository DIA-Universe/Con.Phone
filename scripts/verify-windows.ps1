param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "artifacts/local-verify"
}

$shots = Join-Path $OutputRoot "ui"
$publish = Join-Path $OutputRoot "win-x64"
New-Item -ItemType Directory -Force -Path $shots, $publish | Out-Null

Write-Host "== .NET SDK ==" -ForegroundColor Cyan
dotnet --info

Write-Host "== Core behavior tests ==" -ForegroundColor Cyan
dotnet run --project (Join-Path $repo "tests/ConPhone.Tests") -c $Configuration

Write-Host "== Windows build ==" -ForegroundColor Cyan
dotnet build (Join-Path $repo "src/ConPhone.Windows") -c $Configuration

Write-Host "== Real Windows clipboard roundtrip ==" -ForegroundColor Cyan
dotnet run --project (Join-Path $repo "src/ConPhone.Windows") -c $Configuration -- --clipboard-selftest

Write-Host "== Render UI tabs ==" -ForegroundColor Cyan
Remove-Item -Recurse -Force $shots -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $shots | Out-Null
dotnet run --project (Join-Path $repo "src/ConPhone.Windows") -c $Configuration -- --snapshot $shots
$pngs = @(Get-ChildItem $shots -Filter "*.png" -File)
if ($pngs.Count -ne 4) { throw "Expected 4 UI snapshots, got $($pngs.Count)." }

Write-Host "== Publish single EXE ==" -ForegroundColor Cyan
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish | Out-Null
$publishArgs = @(
    "publish",
    (Join-Path $repo "src/ConPhone.Windows"),
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:PublishTrimmed=false",
    "-p:PublishReadyToRun=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $publish
)
& dotnet @publishArgs

$exe = Join-Path $publish "ConPhone.exe"
if (!(Test-Path $exe -PathType Leaf)) { throw "ConPhone.exe was not produced." }
$files = @(Get-ChildItem $publish -File)
$unexpected = @($files | Where-Object Name -ne "ConPhone.exe")
if ($unexpected.Count -gt 0) { throw "Single-file publish produced unexpected files: $($unexpected.Name -join ', ')" }

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$hashFile = "$exe.sha256"
Set-Content -Path $hashFile -Value "$hash  ConPhone.exe" -NoNewline

Write-Host ""
Write-Host "LOCAL VERIFY PASSED" -ForegroundColor Green
Write-Host "EXE: $exe"
Write-Host "SHA256: $hash"
Write-Host "UI snapshots: $shots"
