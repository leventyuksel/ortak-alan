$ErrorActionPreference = "Stop"
$dotnet = "C:\Users\Lvnt\AppData\Local\Microsoft\dotnet\dotnet.exe"
$proj = Join-Path $PSScriptRoot "OrtakAlan.Mac.csproj"
$plist = Join-Path $PSScriptRoot "Info.plist"
$root = Join-Path (Split-Path $PSScriptRoot -Parent) "dist\macos"
New-Item -ItemType Directory -Force -Path $root | Out-Null

foreach ($rid in @("osx-arm64", "osx-x64")) {
    $out = Join-Path $root $rid
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dotnet publish $proj -c Release -r $rid --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $appName = "OrtakAlan-$rid.app"
    $app = Join-Path $root $appName
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    $macOs = Join-Path $app "Contents\MacOS"
    New-Item -ItemType Directory -Force -Path $macOs | Out-Null
    Copy-Item -Path (Join-Path $out "*") -Destination $macOs -Recurse -Force
    Copy-Item $plist (Join-Path $app "Contents\Info.plist") -Force

    $stage = Join-Path $env:TEMP "oa-mac-pack"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item $app (Join-Path $stage $appName) -Recurse
    $archive = Join-Path $root "$appName.tar.gz"
    $tempArchive = Join-Path $stage "$appName.tar.gz"
    Push-Location $stage
    tar -czf $tempArchive $appName
    if ($LASTEXITCODE -ne 0) { Pop-Location; exit $LASTEXITCODE }
    Pop-Location
    Move-Item $tempArchive $archive -Force
    Write-Host "Hazır: $archive"
}
