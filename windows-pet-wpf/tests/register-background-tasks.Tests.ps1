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
