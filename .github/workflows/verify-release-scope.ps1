param(
    [Parameter(Mandatory = $true)]
    [string]$BaseSha,
    [Parameter(Mandatory = $true)]
    [string]$HeadSha = "HEAD"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (git rev-parse --show-toplevel).Trim()
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "The current directory is not a Git worktree."
}

$diffEntries = @(git -C $repoRoot diff --name-status $BaseSha $HeadSha)
$backendChanges = @()
foreach ($entry in $diffEntries) {
    $columns = $entry -split "`t"
    $status = $columns[0]
    $paths = @($columns | Select-Object -Skip 1)
    foreach ($path in $paths) {
        if ($path -match '(^|[\\/])server([\\/]|$)' -and $status -notmatch '^D') {
            $backendChanges += "$status $path"
        }
    }
}
$workingTreePaths = @(
    git -C $repoRoot status --short --untracked-files=all |
        ForEach-Object {
            if ($_.Length -gt 3) { $_.Substring(3) }
        }
)
$backendChanges += @($workingTreePaths | Where-Object { $_ -match '(^|[\\/])server([\\/]|$)' })
if ($backendChanges.Count -gt 0) {
    throw "Release scope contains new or modified server paths: $($backendChanges -join ', ')"
}

$treePaths = @(git -C $repoRoot ls-tree -r --name-only $HeadSha -- server)
if ($treePaths.Count -gt 0) {
    throw "Release tree contains server paths: $($treePaths -join ', ')"
}

Write-Output "Release scope passed: no server paths in diff, worktree, or HEAD tree."
