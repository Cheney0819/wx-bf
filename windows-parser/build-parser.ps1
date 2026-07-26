param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ParserRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ParserRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "artifacts/datasync-windows"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$OutputPrefix = $OutputRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$PathRoot = [System.IO.Path]::GetPathRoot($OutputRoot)
if ([string]::Equals($OutputRoot, $PathRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($OutputRoot, $RepoRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($OutputRoot, $ParserRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $RepoRoot.StartsWith($OutputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be a dedicated build directory and must not contain the repository."
}
$WorkRoot = Join-Path $OutputRoot "work"
$StageRoot = Join-Path $OutputRoot "Parser"
$ZipPath = Join-Path $OutputRoot "Parser.zip"
$ParserDist = Join-Path $WorkRoot "parser-dist"
$ParserWork = Join-Path $WorkRoot "parser-work"

if (Test-Path $OutputRoot) {
    Remove-Item $OutputRoot -Recurse -Force
}
New-Item $WorkRoot -ItemType Directory -Force | Out-Null
New-Item $StageRoot -ItemType Directory -Force | Out-Null

& python -m PyInstaller --version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller is unavailable. Install windows-parser/requirements-build.txt first."
}

& python -m PyInstaller `
    --noconfirm `
    --clean `
    --onedir `
    --noupx `
    --name wx_parser `
    --paths $ParserRoot `
    --distpath $ParserDist `
    --workpath $ParserWork `
    --specpath $WorkRoot `
    (Join-Path $ParserRoot "wx_parser.py")
if ($LASTEXITCODE -ne 0) {
    throw "Parser build failed."
}

$ParserBuildRoot = Join-Path $ParserDist "wx_parser"
$ParserExe = Join-Path $ParserBuildRoot "wx_parser.exe"
if (-not (Test-Path $ParserExe)) {
    throw "Parser build did not produce wx_parser.exe."
}
$ParserSha256 = (Get-FileHash $ParserExe -Algorithm SHA256).Hash.ToLowerInvariant()
New-Item $StageRoot -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $ParserBuildRoot "*") $StageRoot -Recurse -Force
$ParserInstallJson = @{
    schemaVersion = 1
    executablePath = "wx_parser.exe"
    sha256 = $ParserSha256
} | ConvertTo-Json
[System.IO.File]::WriteAllText(
    (Join-Path $StageRoot "parser-install.json"),
    $ParserInstallJson,
    [System.Text.UTF8Encoding]::new($false))

$UncompressedBytes = (
    Get-ChildItem $StageRoot -File -Recurse |
        Measure-Object -Property Length -Sum
).Sum
Compress-Archive -Path (Join-Path $StageRoot "*") -DestinationPath $ZipPath -CompressionLevel Optimal
$CompressedBytes = (Get-Item $ZipPath).Length
$MaximumZipBytes = 85MB
if ($CompressedBytes -gt $MaximumZipBytes) {
    throw "Combined ZIP exceeds 85 MiB: $CompressedBytes bytes."
}

$Evidence = [ordered]@{
    schemaVersion = 1
    parserSha256 = $ParserSha256
    uncompressedBytes = $UncompressedBytes
    compressedBytes = $CompressedBytes
    maximumCompressedBytes = $MaximumZipBytes
    parserOnly = $true
    zipPath = $ZipPath
}
$EvidenceJson = $Evidence | ConvertTo-Json
[System.IO.File]::WriteAllText(
    (Join-Path $OutputRoot "package-evidence.json"),
    $EvidenceJson,
    [System.Text.UTF8Encoding]::new($false))
$Evidence | ConvertTo-Json -Compress
