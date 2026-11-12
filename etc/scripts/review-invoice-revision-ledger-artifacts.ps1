# Phase 2G.3A.7 — READ-ONLY exact-file migration artifact review.
# Never stages/commits files or connects to a database.
# Each hash corresponds to a previously uploaded, human-reviewed artifact.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$expectedBranch = 'wip/phase-2g2'
$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne $expectedBranch) {
    throw "Safety stop: expected branch $expectedBranch, got $branch."
}

$prefix = 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations'
$artifacts = @(
    @{
        Path = "$prefix/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs"
        Sha256 = '296e303ae2ff2770ba2df558c206ae5c102722fd765684d00518c9693c3ff977'
    },
    @{
        Path = "$prefix/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.Designer.cs"
        Sha256 = 'fcfe926913089b8cfd9a99c2a806ebc307a6e00ef15dd68f4e5e0529e442bd73'
    },
    @{
        Path = "$prefix/BuildingManagementDbContextModelSnapshot.cs"
        Sha256 = 'a21e8c7d9005d877ac9a76f37828e91fd9ff453bf53a06c060e65d4a23984d1d'
    },
    @{
        Path = 'etc/phase-audits/Phase2G3A7_RevisionLedger_Patched_Review.sql'
        Sha256 = 'b5aabd722050d3db3f7cff2703d8a7b44f0380fd9711f40470da35bf1d9dcb24'
    },
    @{
        Path = 'etc/phase-audits/Phase2G3A7_RevisionLedger_Down_Review.sql'
        Sha256 = '38a06bfbbf76b0cbd973a6fa70b2e779e73f1a45a9f61f12bbd7dda02bc4a416'
    }
)

foreach ($entry in $artifacts) {
    $full = Join-Path $root $entry.Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Safety stop: file missing: $($entry.Path)"
    }
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.Sha256) {
        throw "Safety stop: reviewed SHA-256 mismatch: $($entry.Path). Do NOT replace the file blindly; submit it for another review."
    }
    Write-Host "[MATCH] $($entry.Path)"
}

# There must be no preexisting staged unrelated changes; this step
# does not stage any files itself.
$staged = @(& git -C $root diff --cached --name-only)
if ($LASTEXITCODE -ne 0) {
    throw 'Safety stop: git staged status could not be read.'
}
$staged = @($staged | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($staged.Count -ne 0) {
    throw ('Safety stop: there are already staged files. Review first: ' +
           ($staged -join ', '))
}

# Only a visibility check. Never mutate/clean the user worktree.
Write-Host ''
Write-Host 'REVIEW PASS: 5/5 exact local files match approved SHA-256.'
Write-Host 'No files were staged or committed. No database was accessed.'
Write-Host 'Other local generated SQL files are deliberately left untouched.'
Write-Host ''
Write-Host 'Next action after inspecting the exact file list:'
Write-Host "  git add -- $($artifacts[0].Path) $($artifacts[1].Path) $($artifacts[2].Path)"
Write-Host "  git add -- $($artifacts[3].Path) $($artifacts[4].Path)"
Write-Host '  git diff --cached --check'
Write-Host '  git diff --cached --stat'
Write-Host 'Review those commands before committing; no auto-commit occurs.'
