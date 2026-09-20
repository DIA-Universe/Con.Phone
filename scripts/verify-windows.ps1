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

function Assert-NativeSuccess([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit $LASTEXITCODE)." }
}

# Only clean the two known output directories, never an arbitrary computed root.
function Clear-OutputDirectory([string]$Path) {
    $root = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/')
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -notin @((Join-Path $root 'ui'), (Join-Path $root 'win-x64'))) {
        throw "Refusing to clean unexpected output path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    New-Item -ItemType Directory -Force -Path $resolved | Out-Null
}

Write-Host "== .NET SDK ==" -ForegroundColor Cyan
dotnet --info
Assert-NativeSuccess "SDK inspection"

Write-Host "== Core behavior tests ==" -ForegroundColor Cyan
dotnet run --project (Join-Path $repo "tests/ConPhone.Tests") -c $Configuration
Assert-NativeSuccess "Core tests"

Write-Host "== Windows build ==" -ForegroundColor Cyan
dotnet build (Join-Path $repo "src/ConPhone.Windows") -c $Configuration
Assert-NativeSuccess "Windows build"

Write-Host "== Release behavior checks ==" -ForegroundColor Cyan
dotnet run --project (Join-Path $repo "src/ConPhone.Windows") -c $Configuration -- --release-selftest
Assert-NativeSuccess "Release self-test"

Write-Host "== Real Windows clipboard roundtrip ==" -ForegroundColor Cyan
dotnet run --project (Join-Path $repo "src/ConPhone.Windows") -c $Configuration -- --clipboard-selftest
Assert-NativeSuccess "Clipboard self-test"

Write-Host "== Render UI tabs ==" -ForegroundColor Cyan
Clear-OutputDirectory $shots
dotnet run --project (Join-Path $repo "src/ConPhone.Windows") -c $Configuration -- --snapshot $shots
Assert-NativeSuccess "UI snapshots"
$pngs = @(Get-ChildItem $shots -Filter "*.png" -File)
if ($pngs.Count -ne 4) { throw "Expected 4 UI snapshots, got $($pngs.Count)." }

Write-Host "== Publish single EXE ==" -ForegroundColor Cyan
Clear-OutputDirectory $publish
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
Assert-NativeSuccess "Single-file publish"

$exe = Join-Path $publish "LINKO.exe"
if (!(Test-Path $exe -PathType Leaf)) { throw "LINKO.exe was not produced." }
if ((Get-Item -LiteralPath $exe).Length -eq 0) { throw "LINKO.exe is empty." }
$files = @(Get-ChildItem $publish -File)
$unexpected = @($files | Where-Object Name -ne "LINKO.exe")
if ($unexpected.Count -gt 0) { throw "Single-file publish produced unexpected files: $($unexpected.Name -join ', ')" }

Write-Host "== Published EXE format and execution ==" -ForegroundColor Cyan
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exe))
try {
    if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid DOS executable header." }
    $reader.BaseStream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
        throw "Expected an x64 PE executable."
    }
} finally { $reader.Dispose() }
$process = Start-Process -FilePath $exe -ArgumentList '--clipboard-selftest' -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id
        throw "Published EXE self-test timed out."
    }
    if ($process.ExitCode -ne 0) { throw "Published EXE self-test failed (exit $($process.ExitCode))." }
} finally { $process.Dispose() }

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$hashFile = "$exe.sha256"
Set-Content -Path $hashFile -Value "$hash  LINKO.exe" -NoNewline

Write-Host ""
Write-Host "LOCAL VERIFY PASSED" -ForegroundColor Green
Write-Host "EXE: $exe"
Write-Host "SHA256: $hash"
Write-Host "UI snapshots: $shots"
