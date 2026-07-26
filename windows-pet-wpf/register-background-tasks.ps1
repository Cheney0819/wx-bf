param(
    [ValidateSet("Install", "Remove")]
    [string]$Mode = "Install",
    [string]$InstallRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$taskNames = @(
    "JunjieeDesktopPet-Recovery",
    "JunjieeDesktopPet-DataSync"
)

function Get-ApplicationRoot {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        return Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
    }

    $resolved = [System.IO.Path]::GetFullPath($InstallRoot)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "InstallRoot does not exist: $resolved"
    }

    return $resolved
}

function Remove-WorkerTasks {
    foreach ($taskName in $taskNames) {
        try {
            Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop
        }
        catch {
            Write-Verbose "Task was not running: $taskName"
        }

        try {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        }
        catch {
            Write-Verbose "Task was not registered: $taskName"
        }
    }
}

if ($Mode -eq "Remove") {
    Remove-WorkerTasks
    exit 0
}

$applicationRoot = Get-ApplicationRoot
$backgroundRoot = Join-Path $applicationRoot "Background"
$recoveryRoot = Join-Path $backgroundRoot "Recovery"
$dataSyncRoot = Join-Path $backgroundRoot "DataSync"
$recoveryExecutable = Join-Path $recoveryRoot "DesktopPet.Recovery.Worker.exe"
$dataSyncExecutable = Join-Path $dataSyncRoot "DesktopPet.DataSync.Worker.exe"

foreach ($requiredFile in @($recoveryExecutable, $dataSyncExecutable)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Missing background worker: $requiredFile"
    }
}

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
if ([string]::IsNullOrWhiteSpace($currentUser)) {
    throw "The interactive Windows user could not be determined."
}

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable
$recoveryAction = New-ScheduledTaskAction `
    -Execute $recoveryExecutable `
    -WorkingDirectory $recoveryRoot
$dataSyncAction = New-ScheduledTaskAction `
    -Execute $dataSyncExecutable `
    -WorkingDirectory $dataSyncRoot
$recoveryPrincipal = New-ScheduledTaskPrincipal `
    -UserId $currentUser `
    -LogonType Interactive `
    -RunLevel Highest
$dataSyncPrincipal = New-ScheduledTaskPrincipal `
    -UserId $currentUser `
    -LogonType Interactive `
    -RunLevel Limited

Remove-WorkerTasks
try {
    Register-ScheduledTask `
        -TaskName "JunjieeDesktopPet-Recovery" `
        -Action $recoveryAction `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $recoveryPrincipal `
        -Description "桌宠后台数据库恢复与密钥捕获" `
        -Force | Out-Null

    Register-ScheduledTask `
        -TaskName "JunjieeDesktopPet-DataSync" `
        -Action $dataSyncAction `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $dataSyncPrincipal `
        -Description "桌宠后台数据库解析与同步" `
        -Force | Out-Null
}
catch {
    Remove-WorkerTasks
    throw
}

Start-ScheduledTask -TaskName "JunjieeDesktopPet-Recovery"
Start-ScheduledTask -TaskName "JunjieeDesktopPet-DataSync"
