$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$expectedVersion = '1.5.0'
$publishDir = Join-Path $root 'dist\publish-tmp-v1.5-dev'
$exeName = 'Wx411Easy-v1.5-dev.exe'
$readmeName = 'README-v1.5-dev.txt'
$diagnosticName = 'DIAGNOSTIC-STEPS-v1.5-dev.txt'
$zipName = 'Wx411Easy-v1.5-dev.zip'
$distReadmeAlias = 'README.txt'
$distDiagnosticAlias = 'DIAGNOSTIC-STEPS.txt'

Push-Location $root
try {
    dotnet clean .\Wx411Easy.sln -c Release
    dotnet restore .\Wx411Easy.sln
    dotnet test .\Wx411Easy.sln `
        -c Release `
        --no-restore `
        -p:TreatWarningsAsErrors=true

    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }
    New-Item .\dist -ItemType Directory -Force | Out-Null
    $obsoleteVersionedArtifacts = @(
        'Wx411Easy-v1.4-dev.exe',
        'Wx411Easy-v1.4-dev.zip',
        'README-v1.4-dev.txt',
        'DIAGNOSTIC-STEPS-v1.4-dev.txt'
    )
    foreach ($obsoleteName in $obsoleteVersionedArtifacts) {
        $obsoletePath = Join-Path $root ("dist\" + $obsoleteName)
        if (Test-Path $obsoletePath) {
            Remove-Item $obsoletePath -Force
        }
    }

    dotnet publish .\src\Wx411.Easy\Wx411.Easy.csproj `
        -c Release `
        -p:PublishProfile=win-x64-single `
        -p:TreatWarningsAsErrors=true `
        -o $publishDir

    $publishedExe = Join-Path $publishDir 'Wx411Easy.exe'
    if (-not (Test-Path $publishedExe)) {
        throw '发布结果缺少 Wx411Easy.exe'
    }

    $exePath = Join-Path $root ("dist\" + $exeName)
    Copy-Item $publishedExe $exePath -Force

    $pe = [System.IO.File]::ReadAllBytes($exePath)
    $peOffset = [BitConverter]::ToInt32($pe, 0x3c)
    $machine = [BitConverter]::ToUInt16($pe, $peOffset + 4)
    $optionalHeader = $peOffset + 24
    $magic = [BitConverter]::ToUInt16($pe, $optionalHeader)
    $subsystem = [BitConverter]::ToUInt16($pe, $optionalHeader + 68)
    if ($machine -ne 0x8664 -or $magic -ne 0x20b -or $subsystem -ne 2) {
        throw ('PE 身份错误：machine=0x{0:x4}, magic=0x{1:x4}, subsystem={2}' -f $machine, $magic, $subsystem)
    }

    $vi = (Get-Item $exePath).VersionInfo
    $fileVersion = "$($vi.FileMajorPart).$($vi.FileMinorPart).$($vi.FileBuildPart).$($vi.FilePrivatePart)"
    $productVersion = "$($vi.ProductMajorPart).$($vi.ProductMinorPart).$($vi.ProductBuildPart)"
    if ($fileVersion -ne "$expectedVersion.0" -or $productVersion -ne $expectedVersion) {
        throw "版本错误：File=$fileVersion Product=$productVersion"
    }

    $readmePath = Join-Path $root ("dist\" + $readmeName)
    $diagnosticPath = Join-Path $root ("dist\" + $diagnosticName)
    Copy-Item .\使用说明.txt $readmePath -Force
    Copy-Item .\诊断测试步骤.txt $diagnosticPath -Force

    $readmeAliasPath = Join-Path $root ("dist\" + $distReadmeAlias)
    $diagnosticAliasPath = Join-Path $root ("dist\" + $distDiagnosticAlias)
    $aliasReadme = @'
发布目录说明
============

这个 README.txt 现在是发布目录入口索引。

当前实验构建：
- Wx411Easy-v1.5-dev.exe
- README-v1.5-dev.txt
- DIAGNOSTIC-STEPS-v1.5-dev.txt

旧的 Wx411Easy.exe / Wx411Easy.zip 属于 1.3.0 历史留档。

Windows 实测使用 -v1.5-dev 这组文件；完整哈希见 SHA256SUMS.txt。

1.5-dev RC8 只保留“定位 key 并解密”这一条恢复流程，“刷新列表”只更新进程和数据库列表。
精准捕获默认安装 4 个 INT3 观察点，在一次会话内验证全部发现 DB，累计命中并批量输出；key 不写日志。
候选须通过多 profile、多页、全页认证和 SQLite integrity_check。
抽样页命中进入 pending；解除附加后执行全页认证，主文件失败时严格校验 WAL header、双 salt、滚动 checksum 和最后 commit 后重试。
失败候选只以当前 Windows 用户 DPAPI 密文保存供下次续跑；逐库输出成功后删除。完整性检查的临时 SQLite sidecar 在 finally 中精确清理。
页 1 始终执行 HMAC；页 2+ 的整页零值按 SQLCipher 预分配未初始化页处理并保持零值，非零坏页仍会阻止输出。
证据 Gate A 在 RC8 为 N/A；总结果只由 Gate B 精准输出和 Gate C 取消/票据未复用决定。Gate D 仅观察源文件变化。
'@
    $aliasDiagnostic = @'
发布目录诊断入口
================

当前实测文档：DIAGNOSTIC-STEPS-v1.5-dev.txt
配套 EXE：Wx411Easy-v1.5-dev.exe

dist 里保留 1.3.0 历史文件；旧 1.4-dev 版本化产物已移除。

完全退出微信后，以管理员身份运行工具；目标进程保持“自动捕获全部 Weixin.exe”，选择 DB 和空输出目录。
先点“定位 key 并解密”，再启动微信；看到 Weixin.dll 已加载并设置 4 个观察点后登录并触发聊天、媒体和头像访问。

精准 profile 与默认 4 个观察点已完成 Windows 真机闭环。
一次捕获会验证全部发现 DB；抽样 pending 收齐后解除附加做全页认证，主文件失败时自动尝试 checksum 有效的 WAL 最后提交视图。
失败票据由 DPAPI 加密保留，成功输出后删除；临时 SQLite sidecar 自动清理。
只有生成 .readable.sqlite 且 integrity_check 通过才算成功。
Gate A 为 N/A；Gate B 与 Gate C 都为 PASS 时，总结果为 PASS。Gate D 不参与总结果。
'@
    [System.IO.File]::WriteAllText($readmeAliasPath, $aliasReadme.TrimStart(), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($diagnosticAliasPath, $aliasDiagnostic.TrimStart(), [System.Text.UTF8Encoding]::new($false))

    $zipPath = Join-Path $root ("dist\" + $zipName)
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    Compress-Archive `
        -Path @($exePath, $readmePath, $diagnosticPath) `
        -DestinationPath $zipPath `
        -CompressionLevel Optimal `
        -Force

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $actualEntries = @($zip.Entries | ForEach-Object FullName | Sort-Object)
        $expectedEntries = @($diagnosticName, $exeName, $readmeName)
        if (($actualEntries -join '|') -ne ($expectedEntries -join '|')) {
            throw "ZIP 内容错误：$($actualEntries -join ', ')"
        }
    }
    finally {
        $zip.Dispose()
    }

    $hashRecords = @()
    $legacyFiles = @(
        @{ Header = '# 1.3.0 — 2026-07-20'; Name = 'Wx411Easy.exe' },
        @{ Header = $null; Name = 'Wx411Easy.zip' }
    )
    $legacyHeaderWritten = $false
    foreach ($legacy in $legacyFiles) {
        $legacyPath = Join-Path $root ("dist\" + $legacy.Name)
        if (-not (Test-Path $legacyPath)) { continue }
        if ($legacy.Header -and -not $legacyHeaderWritten) {
            $hashRecords += $legacy.Header
            $legacyHeaderWritten = $true
        }
        $legacyHash = (Get-FileHash -Algorithm SHA256 $legacyPath).Hash.ToLowerInvariant()
        $hashRecords += "$legacyHash  $($legacy.Name)"
    }

    $hashRecords += '# 1.5-dev — 2026-07-24 WAL + DPAPI + zero-page preallocation'
    foreach ($file in @($exePath, $zipPath, $readmePath, $diagnosticPath)) {
        $hash = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
        $hashRecords += "$hash  $(Split-Path -Leaf $file)"
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $root 'dist\SHA256SUMS.txt'),
        $hashRecords,
        [System.Text.UTF8Encoding]::new($false))

    Write-Host ''
    Write-Host "PE identity:  x64 GUI $fileVersion" -ForegroundColor Green
    Write-Host "Build output: dist\\$exeName" -ForegroundColor Green
    Write-Host "Package:      dist\\$zipName" -ForegroundColor Green
    Get-Content .\dist\SHA256SUMS.txt
}
finally {
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }
    Pop-Location
}
