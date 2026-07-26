param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($PublishRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Publish root does not exist: $root"
}

$required = @(
    "DesktopPet.Wpf.exe",
    "Background\Recovery\DesktopPet.Recovery.Worker.exe",
    "Background\DataSync\DesktopPet.DataSync.Worker.exe",
    "Background\Parser\wx_parser.exe",
    "Background\Parser\parser-install.json",
    "Background\register-background-tasks.ps1",
    "release-manifest.json"
)

$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "Release is missing required files: $($missing -join ', ')"
}

$forbiddenNames = @(
    "wx_decrypt.exe",
    "WeChatMonitor",
    "--watchdog",
    "--monitor-only",
    "server"
)
$forbidden = Get-ChildItem -LiteralPath $root -File -Recurse |
    Where-Object {
        $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/')
        $forbiddenNames | Where-Object {
            $relative -like "*$_*"
        }
    }
if ($forbidden.Count -gt 0) {
    throw "Release contains forbidden files: $($forbidden.FullName -join ', ')"
}

$manifestPath = Join-Path $root "release-manifest.json"
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.runtime -ne "win-x64") {
    throw "Release manifest schema or runtime is invalid."
}

foreach ($entry in $manifest.files) {
    if ([System.IO.Path]::IsPathRooted($entry.path) -or $entry.path.Contains("..")) {
        throw "Release manifest contains an unsafe path: $($entry.path)"
    }

    $filePath = Join-Path $root $entry.path
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "Release manifest file is missing: $($entry.path)"
    }

    $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.sha256 -or (Get-Item -LiteralPath $filePath).Length -ne $entry.bytes) {
        throw "Release manifest hash or size mismatch: $($entry.path)"
    }
}

$publishBytes = (Get-ChildItem -LiteralPath $root -File -Recurse | Measure-Object -Property Length -Sum).Sum
if ($publishBytes -gt 220MB) {
    throw "Release publish directory exceeds 220 MiB: $publishBytes bytes."
}

Write-Output "Release validation passed: $root"
