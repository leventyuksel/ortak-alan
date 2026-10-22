$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetRoot = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
$dotnet = Join-Path $dotnetRoot "dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$env:DOTNET_ROOT = $dotnetRoot
if ($env:Path -notlike "*$dotnetRoot*") { $env:Path = "$dotnetRoot;$env:Path" }
[Environment]::SetEnvironmentVariable("DOTNET_ROOT", $dotnetRoot, "User")

Get-Process OrtakAlan -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
& $dotnet build (Join-Path $root "OrtakAlan.csproj") -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Get-ChildItem (Join-Path $root "bin\Debug") -Recurse -Filter OrtakAlan.exe | Select-Object -First 1
$satisDir = Join-Path $env:APPDATA "OrtakAlanDevSatis"
$shareDir = Join-Path $satisDir "Evrak"
New-Item -ItemType Directory -Force -Path $shareDir | Out-Null
$satisName = "Sat" + [char]0x0131 + [char]0x015F
$yesil = "Ye" + [char]0x015F + "il"
$hello = Join-Path $shareDir "Merhaba.txt"
if (-not (Test-Path $hello)) {
  $utf8 = New-Object System.Text.UTF8Encoding $false
  $helloText = "Merhaba, bu " + $satisName + " bilgisayar" + [char]0x0131 + "ndan payla" + [char]0x015F + [char]0x0131 + "lan bir dosyad" + [char]0x0131 + "r."
  [IO.File]::WriteAllText($hello, $helloText, $utf8)
}
$settingsPath = Join-Path $satisDir "settings.json"
if (-not (Test-Path $settingsPath)) {
  $settings = [ordered]@{
    computerId = "satis-dev-0001"
    computerName = $satisName
    theme = $yesil
    startWithWindows = $false
    approved = @()
    rejected = @()
    shares = @(
      [ordered]@{
        id = "satis-klasor-1"
        kind = "folder"
        name = "Evrak"
        path = $shareDir
        windowsShare = $null
        windowsError = $null
      }
    )
  }
  $utf8 = New-Object System.Text.UTF8Encoding $false
  [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 6), $utf8)
}

Start-Process $exe.FullName -ArgumentList @("--veri", $satisDir)
Start-Sleep -Milliseconds 700
Start-Process $exe.FullName -ArgumentList @("--goster")
