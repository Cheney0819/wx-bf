param(
    [string]$Runtime = "win-x64",
    [switch]$NoSelfContained
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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

function Publish-Project([string]$Project, [string]$Output, [string]$Rid) {
    & dotnet publish $Project `
        -c Release `
        -r $Rid `
        --self-contained false `
        -p:EnableWindowsTargeting=true `
        -o $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $Project"
    }
}

function Write-ReleaseManifest([string]$PublishRoot, [string]$Runtime, [string]$Version) {
    $files = @(
        Get-ChildItem -LiteralPath $PublishRoot -File -Recurse |
            Where-Object { $_.Extension -notin @(".pdb", ".xml") } |
            ForEach-Object {
                $relative = $_.FullName.Substring($PublishRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                [ordered]@{
                    path = $relative
                    bytes = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        runtime = $Runtime
        frameworkDependent = $true
        files = $files
    }
    $manifestPath = Join-Path $PublishRoot "release-manifest.json"
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    return $manifestPath
}

function Resolve-FfmpegPath {
    $candidates = @()
    if ($env:ChocolateyInstall) {
        $candidates += @(
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg\tools\ffmpeg\bin\ffmpeg.exe"),
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg-full\tools\ffmpeg\bin\ffmpeg.exe"),
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg-shared\tools\ffmpeg\bin\ffmpeg.exe"),
            (Join-Path $env:ChocolateyInstall "lib\ffmpeg\tools\bin\ffmpeg.exe")
        )
    }

    $command = Get-Command "ffmpeg" -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        $candidates += $command.Source
    }
    $candidates += @(
        "C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        "C:\ffmpeg\bin\ffmpeg.exe"
    )
    return @($candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)[0]
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$PetDir = Join-Path $Root "windows-pet-wpf"
$PublishDir = Join-Path $PetDir "bin\Release\net8.0-windows\$Runtime\publish"
$ArtifactsRoot = Join-Path $Root "artifacts\desktop-pet-build"
$ParserStage = Join-Path $ArtifactsRoot "Parser"
$BackgroundRoot = Join-Path $PublishDir "Background"
$ffmpegPath = $null

Step "检查环境"
Require-Command "python" "请先安装 Python 3，并勾选 Add python.exe to PATH。"
Require-Command "pip" "请确认 Python 的 pip 已安装。"
Require-Command "dotnet" "请先安装 .NET SDK 8：https://dotnet.microsoft.com/download"
$ffmpegPath = Resolve-FfmpegPath
if ([string]::IsNullOrWhiteSpace($ffmpegPath)) {
    throw "缺少 ffmpeg.exe。请先安装 ffmpeg，或在 GitHub Actions 中执行 choco install ffmpeg -y --no-progress。"
}

Step "安装 Parser 构建依赖"
Push-Location (Join-Path $Root "windows-parser")
python -m pip install -U pip
pip install -r requirements-build.txt
Pop-Location

Step "运行发布前测试"
python -m pytest (Join-Path $Root "windows-parser/tests") -q
if ($LASTEXITCODE -ne 0) {
    throw "Parser tests failed."
}
& dotnet test `
    (Join-Path $Root "windows-background/DesktopPet.Background.sln") `
    -c Release `
    -p:EnableWindowsTargeting=true
if ($LASTEXITCODE -ne 0) {
    throw "Background tests failed."
}
& dotnet test `
    (Join-Path $Root "tools/DesktopPet.Uninstaller.sln") `
    -c Release `
    -p:EnableWindowsTargeting=true
if ($LASTEXITCODE -ne 0) {
    throw "Uninstaller tests failed."
}

$parseTokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PetDir "register-background-tasks.ps1"),
    [ref]$parseTokens,
    [ref]$parseErrors) | Out-Null
if (@($parseErrors).Count -gt 0) {
    throw "Background task registration script has parse errors: $($parseErrors -join '; ')"
}

Step "清理旧发布目录"
if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
if (Test-Path -LiteralPath $ArtifactsRoot) {
    Remove-Item -LiteralPath $ArtifactsRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $BackgroundRoot | Out-Null

Step "发布 WPF 主程序"
Push-Location $PetDir
dotnet restore
Pop-Location
Publish-Project (Join-Path $PetDir "DesktopPet.Wpf.csproj") $PublishDir $Runtime

Step "发布 Recovery Worker"
Publish-Project `
    (Join-Path $Root "windows-background/src/DesktopPet.Recovery.Worker/DesktopPet.Recovery.Worker.csproj") `
    (Join-Path $BackgroundRoot "Recovery") `
    $Runtime

Step "发布 DataSync Worker"
Publish-Project `
    (Join-Path $Root "windows-background/src/DesktopPet.DataSync.Worker/DesktopPet.DataSync.Worker.csproj") `
    (Join-Path $BackgroundRoot "DataSync") `
    $Runtime

Step "构建 Parser"
& (Join-Path $Root "windows-parser/build-parser.ps1") -OutputRoot $ArtifactsRoot
if ($LASTEXITCODE -ne 0) {
    throw "Parser build failed."
}
Copy-Item -LiteralPath $ParserStage -Destination (Join-Path $BackgroundRoot "Parser") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PetDir "register-background-tasks.ps1") -Destination $BackgroundRoot -Force
Copy-Item -LiteralPath $ffmpegPath -Destination (Join-Path $BackgroundRoot "Parser\ffmpeg.exe") -Force

Remove-Item -LiteralPath (Join-Path $PublishDir "wx_decrypt.exe") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $PublishDir "ffmpeg.exe") -Force -ErrorAction SilentlyContinue

$projectXml = [xml](Get-Content -LiteralPath (Join-Path $PetDir "DesktopPet.Wpf.csproj"))
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "DesktopPet.Wpf.csproj does not define a version."
}
Write-ReleaseManifest $PublishDir $Runtime $version | Out-Null

Step "验证发布目录"
& (Join-Path $PetDir "tests/validate-release.ps1") -PublishRoot $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "Release validation failed."
}

Write-Host ""
Write-Host "打包完成。" -ForegroundColor Green
Write-Host "最终目录：$PublishDir" -ForegroundColor Green
Write-Host "使用 Inno Setup 编译 windows-pet-wpf/DesktopPetSetup.iss 生成安装包。" -ForegroundColor Yellow
