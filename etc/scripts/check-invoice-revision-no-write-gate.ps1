# Phase 2G.3A.7 — READ-ONLY source-boundary regression gate.
# Does NOT prove authorization, database role separation or production safety.
# Does NOT connect to PostgreSQL, execute SQL or edit any files.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: expected branch wip/phase-2g2.'
}

function Read-SafeSource([string]$path) {
    $full = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Safety stop: missing source $path"
    }
    return [IO.File]::ReadAllText($full)
}

$service = Read-SafeSource 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/InvoiceDraftStorageAppService.cs'
$contract = Read-SafeSource 'modules/buildingmanagement/src/BuildingManagement.Application.Contracts/Invoices/IInvoiceDraftStorageAppService.cs'
$invoice = Read-SafeSource 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/Invoice.cs'
$permissions = Read-SafeSource 'modules/buildingmanagement/src/BuildingManagement.Application.Contracts/Permissions/BuildingManagementPermissions.cs'
$provider = Read-SafeSource 'modules/buildingmanagement/src/BuildingManagement.Application.Contracts/Permissions/BuildingManagementPermissionDefinitionProvider.cs'
$migration = Read-SafeSource 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs'

if ($service -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]') {
    throw 'Safety stop: draft service remote-disable attribute missing.'
}
if ($service -notmatch '\bPreviewRebuildAsync\s*\(' -or
    $contract -notmatch '\bPreviewRebuildAsync\s*\(') {
    throw 'Safety stop: expected read-only preflight surface is missing.'
}

# This is deliberately a NO-WRITE gate. Once implementation of a
# mutation is planned, change the security design/tests FIRST rather
# than treating this script's failure as something to bypass.
foreach ($text in @($service, $contract)) {
    if ($text -match '\b(RebuildDraftAsync|IssueAsync|PostDebtAsync)\s*\(') {
        throw 'Safety stop: a mutating revision/issuance API was introduced before security review.'
    }
}

if ($invoice -match '\bCurrentRevision\b') {
    throw 'Safety stop: CurrentRevision appeared. Review new EF migration and concurrent version gate.'
}
# Permission names may now be DEFINED, but they are not GRANTED
# or used by a mutating service. This is a source presence gate, not
# an ABP authorization integration or role assignment check.
foreach ($permissionName in @(
    'RebuildDraft',
    'ReadRevisionHistory'
)) {
    if (-not $permissions.Contains($permissionName) -or
        -not $provider.Contains('BuildingManagementPermissions.Invoices.' + $permissionName)) {
        throw "Safety stop: reviewed invoice permission definition missing: $permissionName"
    }
}

$policy = Read-SafeSource 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/InvoiceDraftRevisionAccessPolicy.cs'
if (-not $policy.Contains('PermissionGranted') -or
    -not $policy.Contains('AuthorizedBuildingIds') -or
    -not $policy.Contains('EligibleForDesignOnly')) {
    throw 'Safety stop: server-resolved scope policy was removed or changed.'
}

foreach ($guard in @(
    'CK_BM_InvoiceDraftRevision_HexSha256',
    'TR_BM_InvoiceDraftRevision_ProtectRow',
    'TR_BM_InvoiceDraftRevision_ProtectTruncate',
    'IN ACCESS EXCLUSIVE MODE',
    'Cannot roll back draft revision ledger: archived history exists'
)) {
    if (-not $migration.Contains($guard)) {
        throw "Safety stop: migration safety guard missing: $guard"
    }
}

Write-Host 'SOURCE NO-WRITE GATE PASS: remote draft service remains disabled.'
Write-Host 'PASS: no mutating draft-rebuild/issue/debt command exposed in draft service/contract.'
Write-Host 'PASS: CurrentRevision still absent; two permission definitions exist but grant no access.'
Write-Host 'PASS: pure building-scope policy remains design-only; no authorization service is wired.'
Write-Host 'PASS: required V2 trigger, checksum and rollback-source markers are present.'
Write-Host 'READ-ONLY CHECK ONLY. This is NOT a database-role or authorization test.'
