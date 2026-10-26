# Phase 2G.3A.13 — source-only guard against premature activation of
# reviewed-but-not-deployed operator/building membership and SQL scope queries.
# NEVER accesses any database, alters permissions, or edits files.
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: expected wip/phase-2g2 branch.'
}

function Read-Source([string]$path) {
    $file = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Safety stop: missing $path"
    }
    return [IO.File]::ReadAllText($file)
}

$db = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/EntityFrameworkCore/BuildingManagementDbContext.cs'
$model = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/EntityFrameworkCore/BuildingManagementDbContextModelCreatingExtensions.cs'
$identity = Read-Source 'src/NgocTrangHouseManagementSystem.HttpApi.Host/Security/AbpIdentityBuildingOperatorAccountProbe.cs'
$gate = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingBusinessScopeDesignGate.cs'
$delegation = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingOperatorChangeDesignGate.cs'
$queries = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Security/BuildingBusinessScopedCollectionQueries.cs'

# The candidate grant schema MUST remain exclusive to disposable test DbContexts
# until a reviewed migration, identity integration and complete CRUD guard exist.
foreach ($source in @($db, $model)) {
    if ($source -match 'BuildingOperatorAccessGrant|BuildingOperatorAccessGrantDesignConfiguration|BuildingManagementOperatorBuildingGrants') {
        throw 'Safety stop: design-only grant entity was mapped into production EF.'
    }
}

# Match actual declaration/attributes, not comments documenting the absence
# of such registration. Effective DI remains a separate runtime test.
if ($identity -match ':\s*IBuildingOperatorActiveAccountReader\s*,\s*ITransientDependency' -or
    $identity -match '\[(ExposeServices|Dependency)\s*\(') {
    throw 'Safety stop: experimental identity adapter appears auto-registered.'
}
if ($gate -notmatch 'class DenyAllBuildingOperatorGrantReader' -or
    $gate -notmatch 'Array.Empty<Guid>\(\)' -or
    $gate -notmatch 'EligibleForDesignOnly') {
    throw 'Safety stop: deny-all scope evaluation contract changed.'
}
if ($delegation -notmatch 'class DenyAllBuildingOperatorActiveAccountReader' -or
    $delegation -notmatch 'Task.FromResult\(false\)' -or
    $delegation -notmatch 'EligibleForDesignOnly') {
    throw 'Safety stop: delegation default account deny contract changed.'
}
foreach ($name in @('IQueryable<Building>', 'IQueryable<Floor>',
                   'IQueryable<Room>', 'IQueryable<Contract>')) {
    if (-not $queries.Contains($name)) {
        throw "Safety stop: scoped SQL query type is missing: $name"
    }
}

Write-Host 'BUILDING SCOPE DESIGN-ONLY GATE PASS.'
Write-Host 'PASS: experimental grant table is not mapped into the runtime DbContext.'
Write-Host 'PASS: ABP Identity adapter is not auto-registered by class markers.'
Write-Host 'PASS: current membership/account defaults are DENY ALL.'
Write-Host 'READ-ONLY SOURCE CHECK: this does NOT prove effective DI, API authorization or database roles.'
