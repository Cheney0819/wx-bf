Describe "background task registration contract" {
    BeforeAll {
        $scriptPath = Join-Path $PSScriptRoot "..\register-background-tasks.ps1"
        $scriptText = Get-Content -Raw -LiteralPath $scriptPath
    }

    It "declares both fixed tasks and the recovery policy" {
        $scriptText | Should -Match "JunjieeDesktopPet-Recovery"
        $scriptText | Should -Match "JunjieeDesktopPet-DataSync"
        $scriptText | Should -Match "RunLevel Highest"
        $scriptText | Should -Match "RunLevel Limited"
        $scriptText | Should -Match "AtLogOn"
        $scriptText | Should -Match "RestartCount 3"
        $scriptText | Should -Match "RestartInterval \(New-TimeSpan -Minutes 1\)"
        $scriptText | Should -Match "Register-ScheduledTask"
    }

    It "does not expose injectable identity or command evaluation" {
        $scriptText | Should -Not -Match "param\([^)]*UserId"
        $scriptText | Should -Not -Match "Invoke-Expression"
        $scriptText | Should -Not -Match "Start-Process.*\$args"
    }
}

Describe "installer worker lifecycle contract" {
    BeforeAll {
        $setupText = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\DesktopPetSetup.iss")
    }

    It "registers and removes tasks and stops worker processes" {
        $setupText | Should -Match "register-background-tasks\.ps1"
        $setupText | Should -Match "\[UninstallRun\]"
        $setupText | Should -Match "DesktopPet\.Recovery\.Worker\.exe"
        $setupText | Should -Match "DesktopPet\.DataSync\.Worker\.exe"
        $setupText | Should -Match "wx_parser\.exe"
        $setupText | Should -Match "StopProcessTree"
    }

    It "deletes legacy plaintext credentials and decrypt executable during upgrades" {
        $setupText | Should -Match "(?m)^\[InstallDelete\]\s*$"
        foreach ($legacyFile in @(
            '{app}\monitor_config.json',
            '{app}\wechat_data\monitor_config.json',
            '{app}\Background\DataSync\monitor_config.json',
            '{app}\wx_decrypt.exe'
        )) {
            $entry = 'Type: files; Name: "' + $legacyFile + '"'
            $setupText | Should -Match ([regex]::Escape($entry))
        }
    }
}

Describe "production credential source contract" {
    BeforeAll {
        $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
        $legacyMonitorPath = Join-Path $repositoryRoot "windows\WeChatMonitor.cs"
        $legacyMonitorText = Get-Content -Raw -LiteralPath $legacyMonitorPath
    }

    It "does not retain the leaked deployment token in production sources" {
        $productionRoots = @(
            (Join-Path $repositoryRoot "windows"),
            (Join-Path $repositoryRoot "windows-pet-wpf"),
            (Join-Path $repositoryRoot "windows-background\src"),
            (Join-Path $repositoryRoot "windows-parser")
        )
        $sourceFiles = Get-ChildItem -LiteralPath $productionRoots -File -Recurse |
            Where-Object {
                $_.FullName -notmatch '[\\/](?:bin|obj|tests)[\\/]' -and
                $_.Extension -in @(
                    ".cs", ".json", ".ps1", ".psm1", ".iss", ".py",
                    ".xml", ".props", ".targets", ".yml", ".yaml", ".txt")
            }
        $retiredDeploymentToken = "wx_" + "monitor_" + "2026"
        $matches = @($sourceFiles | Select-String -SimpleMatch $retiredDeploymentToken)

        $matches.Count | Should -Be 0
    }

    It "requires legacy monitor credentials from environment or config" {
        $legacyMonitorText | Should -Match "WECHAT_MONITOR_SERVER_TOKEN"
        $legacyMonitorText | Should -Match "monitor_config\.json"
        $legacyMonitorText | Should -Match "throw new InvalidOperationException"
    }
}

Describe "silent worker executable contract" {
    BeforeAll {
        $backgroundRoot = Join-Path $PSScriptRoot "..\..\windows-background\src"
        $recoveryProject = Get-Content -Raw -LiteralPath (
            Join-Path $backgroundRoot "DesktopPet.Recovery.Worker\DesktopPet.Recovery.Worker.csproj")
        $dataSyncProject = Get-Content -Raw -LiteralPath (
            Join-Path $backgroundRoot "DesktopPet.DataSync.Worker\DesktopPet.DataSync.Worker.csproj")
    }

    It "uses the Windows GUI subsystem for both production workers" {
        $recoveryProject | Should -Match "<OutputType>WinExe</OutputType>"
        $dataSyncProject | Should -Match "<OutputType>WinExe</OutputType>"
    }
}
