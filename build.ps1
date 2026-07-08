param(
    [string]$Runtime = "win-x64",
    [switch]$NoSelfContained
)

$ErrorActionPreference = "Stop"

function Step($Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Require-Command($Name, $InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-Host "缺少命令：$Name" -ForegroundColor Red
        Write-Host $InstallHint -ForegroundColor Yellow
        exit 1
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$WindowsDir = Join-Path $Root "windows"
$PetDir = Join-Path $Root "windows-pet-wpf"
$DecryptExe = Join-Path $WindowsDir "dist\wx_decrypt.exe"
$PetDecryptExe = Join-Path $PetDir "wx_decrypt.exe"
$PetFfmpegExe = Join-Path $PetDir "ffmpeg.exe"
$PublishDir = Join-Path $PetDir "bin\Release\net8.0-windows\$Runtime\publish"

function Resolve-FfmpegPath {
    $candidates = @()
    if ($env:ChocolateyInstall) {
        $candidates += @(
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg\tools\ffmpeg\bin\ffmpeg.exe"),
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg-full\tools\ffmpeg\bin\ffmpeg.exe"),
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg-shared\tools\ffmpeg\bin\ffmpeg.exe"),
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg\tools\bin\ffmpeg.exe")
        )

        $discovered = Get-ChildItem -Path (Join-Path $env:ChocolateyInstall "lib") -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object {
                $_.FullName -match "\\tools\\.*\\bin\\ffmpeg\.exe$" -and
                $_.FullName -notmatch "\\chocolatey\\bin\\"
            } |
            Select-Object -ExpandProperty FullName
        $candidates += $discovered
    }

    $command = Get-Command "ffmpeg" -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path $command.Source) -and $command.Source -notmatch "\\chocolatey\\bin\\") {
        $candidates += $command.Source
    }

    $candidates += @(
        "C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        "C:\ffmpeg\bin\ffmpeg.exe"
    )
    $candidates = $candidates |
        Where-Object { $_ -and (Test-Path $_) } |
        Select-Object -Unique

    if ($candidates.Count -gt 0) {
        return $candidates[0]
    }

    return $null
}

Step "检查环境"
Require-Command "python" "请先安装 Python 3，并勾选 Add python.exe to PATH。"
Require-Command "pip" "请确认 Python 的 pip 已安装。"
Require-Command "dotnet" "请先安装 .NET SDK 8：https://dotnet.microsoft.com/download"
$ffmpegPath = Resolve-FfmpegPath
if (-not $ffmpegPath) {
    Write-Host "缺少命令：ffmpeg" -ForegroundColor Red
    Write-Host "请先安装 ffmpeg，或在 GitHub Actions 里先执行 choco install ffmpeg -y --no-progress。" -ForegroundColor Yellow
    exit 1
}

Step "安装 Python 依赖"
Push-Location $WindowsDir
python -m pip install -U pip
pip install -r requirements.txt
Pop-Location

Step "清理历史二进制残留"
Get-ChildItem $PetDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @(".exe", ".dll") } |
    Remove-Item -Force

$legacyResourcesDir = Join-Path $PetDir "resources"
if (Test-Path $legacyResourcesDir) {
    Remove-Item $legacyResourcesDir -Recurse -Force
}

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

Step "打包 wx_decrypt.exe（解密工具）"
Push-Location $WindowsDir
pyinstaller `
  --onefile `
  --name wx_decrypt `
  wx_decrypt.py

if (-not (Test-Path $DecryptExe)) {
    throw "没有找到打包产物：$DecryptExe"
}

Copy-Item $DecryptExe $PetDecryptExe -Force
Copy-Item $ffmpegPath $PetFfmpegExe -Force
Pop-Location

Step "发布桌宠"
Push-Location $PetDir
dotnet restore

$selfContained = if ($NoSelfContained) { "false" } else { "true" }
dotnet publish -c Release -r $Runtime --self-contained $selfContained
Pop-Location

Step "检查发布产物"
$required = @(
    "DesktopPet.Wpf.exe",
    "wx_decrypt.exe",
    "ffmpeg.exe",
    "Assets\sprite-sheet.png",
    "System.Data.SQLite.dll"
)

$missing = @()
foreach ($item in $required) {
    $path = Join-Path $PublishDir $item
    if (-not (Test-Path $path)) {
        $missing += $item
    }
}

if ($missing.Count -gt 0) {
    Write-Host "发布目录缺少文件：" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "打包未完成，请先处理缺失文件。"
}

Write-Host ""
Write-Host "打包完成。" -ForegroundColor Green
Write-Host "最终目录：" -ForegroundColor Green
Write-Host $PublishDir -ForegroundColor Green
Write-Host ""
Write-Host "把整个 publish 文件夹放到登录微信的 Windows 电脑上，双击 DesktopPet.Wpf.exe。" -ForegroundColor Yellow
Write-Host "当前产物已内置 wx_decrypt.exe 和 ffmpeg.exe，语音解码不再依赖用户手动安装 ffmpeg。" -ForegroundColor Green
