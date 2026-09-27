# EMoneyMod 构建 + 部署（单文件版：Mods 里只需要 EMoneyMod.dll）
#
#   .\deploy.ps1              构建并部署（游戏在跑就排队等它退出）
#   .\deploy.ps1 -SkipBuild   直接部署现有 bin\Release\EMoneyMod.dll
#   .\deploy.ps1 -Force       不管游戏在不在跑，立刻部署

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Force,
    [string]$GameDir = $env:SDEZ_PACKAGE_DIR
)

$ErrorActionPreference = 'Stop'

$proj   = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = 'H:\SDEZ1.70\Package'
}
$mods   = Join-Path $GameDir 'Mods'
$src    = Join-Path $proj 'bin\Release\EMoneyMod.dll'
$dst    = Join-Path $mods 'EMoneyMod.dll'
$hid    = Join-Path $mods 'HidSharp.dll'
$bakDir = Join-Path $mods '_backup'

if (-not $SkipBuild) {
    Write-Host '== 构建 ==' -ForegroundColor Cyan
    & dotnet build (Join-Path $proj 'EMoneyMod.csproj') -c Release -v:minimal "-p:GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "构建失败 (exit $LASTEXITCODE)" }
}

if (-not (Test-Path $src)) { throw "找不到 $src" }

Write-Host ''
Write-Host '== 部署 ==' -ForegroundColor Cyan
Write-Host "  源  : $src  ($((Get-Item $src).Length) B)"
Write-Host "  目标: $dst"

# 游戏在跑就排队
if ((Get-Process -Name Sinmai -ErrorAction SilentlyContinue) -and -not $Force) {
    Write-Host '  Sinmai 正在运行 -> 起一个独立的后台守护进程, 等它退出后自动部署' -ForegroundColor Yellow
    # 注意: 不能用 Start-Job —— 脚本所在的控制台一关, Job 就跟着死了。
    # 这里用 Start-Process 起一个独立的 powershell, 它不依赖当前窗口。
    $watcher = @"
`$ErrorActionPreference = 'SilentlyContinue'
while (Get-Process -Name Sinmai) { Start-Sleep -Seconds 1 }
Start-Sleep -Seconds 1
New-Item -ItemType Directory -Path '$bakDir' -Force | Out-Null
if (Test-Path '$dst') { Copy-Item '$dst' (Join-Path '$bakDir' ("EMoneyMod.{0}.dll" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))) -Force }
Copy-Item -LiteralPath '$src' -Destination '$dst' -Force
if (Test-Path '$hid') { Move-Item -LiteralPath '$hid' -Destination (Join-Path '$bakDir' 'HidSharp.dll') -Force }
"@
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $watcher) | Out-Null
    return
}

New-Item -ItemType Directory -Path $bakDir -Force | Out-Null
if (Test-Path $dst) {
    Copy-Item $dst (Join-Path $bakDir ("EMoneyMod.{0}.dll" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))) -Force
}
Copy-Item -LiteralPath $src -Destination $dst -Force
if (Test-Path $hid) {
    Move-Item -LiteralPath $hid -Destination (Join-Path $bakDir 'HidSharp.dll') -Force
    Write-Host '  HidSharp.dll 已移出 Mods（已嵌入 EMoneyMod.dll）' -ForegroundColor Yellow
}

$legacyCredits = @(
    (Join-Path $mods 'EMoneyMod.credit.txt'),
    (Join-Path $GameDir 'appdata\SDEZ\EMoneyMod.credit.txt')
)
foreach ($legacyCredit in $legacyCredits) {
    if (Test-Path -LiteralPath $legacyCredit) {
        Remove-Item -LiteralPath $legacyCredit -Force
        Write-Host "  已清理旧点数文件: $legacyCredit" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '== 完成 ==' -ForegroundColor Green
Get-ChildItem $mods -Filter '*.dll' | Select-Object Length, LastWriteTime, Name | Format-Table -AutoSize
Write-Host ("sha256: " + (Get-FileHash $dst -Algorithm SHA256).Hash)
