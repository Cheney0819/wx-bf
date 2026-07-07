param(
    [string]$InstallDir = ""
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function New-Shortcut($ShortcutPath, $TargetPath, $WorkingDirectory) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $TargetPath
    $shortcut.Save()
}

$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $sourceDir "DesktopPet.Wpf.exe"

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "JunjieeDesktopPet"
}

if (-not (Test-Path $exePath)) {
    throw "没有找到 DesktopPet.Wpf.exe。请保持整个发布目录完整后再运行安装。"
}

$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
$startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$programsDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$desktopShortcut = Join-Path $desktopDir "桌宠.lnk"
$startupShortcut = Join-Path $startupDir "桌宠.lnk"
$programShortcut = Join-Path $programsDir "桌宠.lnk"
$installedExe = Join-Path $InstallDir "DesktopPet.Wpf.exe"

Write-Step "复制安装文件"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
robocopy $sourceDir $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
$copyExitCode = $LASTEXITCODE
if ($copyExitCode -gt 7) {
    throw "Copy failed. robocopy exit code: $copyExitCode"
}

Write-Step "创建桌面快捷方式"
New-Shortcut -ShortcutPath $desktopShortcut -TargetPath $installedExe -WorkingDirectory $InstallDir

Write-Step "创建开机自启快捷方式"
New-Shortcut -ShortcutPath $startupShortcut -TargetPath $installedExe -WorkingDirectory $InstallDir

Write-Step "创建开始菜单快捷方式"
New-Shortcut -ShortcutPath $programShortcut -TargetPath $installedExe -WorkingDirectory $InstallDir

Write-Step "启动桌宠"
Start-Process -FilePath $installedExe -WorkingDirectory $InstallDir

Write-Step "安装完成"
Write-Host "安装目录：$InstallDir" -ForegroundColor Green
Write-Host "桌面快捷方式：$desktopShortcut" -ForegroundColor Green
Write-Host "已启用开机自启。" -ForegroundColor Green
