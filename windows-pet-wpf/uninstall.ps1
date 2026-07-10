param(
    [string]$InstallDir = ""
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Remove-ShortcutIfExists($Path) {
    if (Test-Path $Path) {
        Remove-Item $Path -Force -ErrorAction SilentlyContinue
    }
}

function Stop-ProcessTreeByName($Name) {
    try {
        taskkill /IM $Name /F /T | Out-Null
    } catch {
    }
    Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($Name)) -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "JunjieeDesktopPet"
}

$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
$startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$programsDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$desktopShortcut = Join-Path $desktopDir "桌宠.lnk"
$startupShortcut = Join-Path $startupDir "桌宠.lnk"
$programShortcut = Join-Path $programsDir "桌宠.lnk"
$legacyDesktopShortcut = Join-Path $desktopDir "Desktop Pet.lnk"
$legacyStartupShortcut = Join-Path $startupDir "Desktop Pet.lnk"
$legacyProgramShortcut = Join-Path $programsDir "Desktop Pet.lnk"

Write-Step "关闭桌宠后台进程"
Stop-ProcessTreeByName "DesktopPet.Wpf.exe"
Stop-ProcessTreeByName "wx_decrypt.exe"
Stop-ProcessTreeByName "ffmpeg.exe"
Start-Sleep -Milliseconds 800

Write-Step "删除快捷方式和开机自启"
Remove-ShortcutIfExists $desktopShortcut
Remove-ShortcutIfExists $startupShortcut
Remove-ShortcutIfExists $programShortcut
Remove-ShortcutIfExists $legacyDesktopShortcut
Remove-ShortcutIfExists $legacyStartupShortcut
Remove-ShortcutIfExists $legacyProgramShortcut

Write-Step "删除安装目录"
if (Test-Path $InstallDir) {
    try {
        Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "安装目录将交给系统卸载器继续清理：$InstallDir" -ForegroundColor Yellow
    }
}

Write-Step "卸载完成"
Write-Host "桌宠后台进程、快捷方式、开机自启和安装目录已清理。" -ForegroundColor Green
