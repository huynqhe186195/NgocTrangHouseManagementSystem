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
$hostModule = Read-Source 'src/NgocTrangHouseManagementSystem.HttpApi.Host/NgocTrangHouseManagementSystemHttpApiHostModule.cs'
if ($hostModule -notmatch 'BuildingManagement:Bootstrap' -or
    $hostModule -notmatch 'BuildingOwnerBootstrapSecurityOptions') {
    throw 'Safety stop: configured root account allowlist missing.'
}
$identity = Read-Source 'src/NgocTrangHouseManagementSystem.HttpApi.Host/Security/AbpIdentityBuildingOperatorAccountProbe.cs'
$gate = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingBusinessScopeDesignGate.cs'
$delegation = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingOperatorChangeDesignGate.cs'
$queries = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Security/BuildingBusinessScopedCollectionQueries.cs'

# A real SQL-backed membership reader may be tested in isolation but MUST
# not be auto-registered. Until the production migration and ABP Identity
# integration pass review, DenyAllBuildingOperatorGrantReader stays active.
$candidateReader = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Security/EfBuildingOperatorGrantCandidateReader.cs'
if ($candidateReader -notmatch 'class EfBuildingOperatorGrantCandidateReader' -or
    $candidateReader -notmatch 'BuildingOperatorGrantCandidateQuery' -or
    $candidateReader -match ':\s*IBuildingOperatorGrantReader\s*,\s*ITransientDependency' -or
    $candidateReader -match '\[(ExposeServices|Dependency)\s*\(') {
    throw 'Safety stop: candidate membership reader changed or was auto-registered.'
}

# The candidate grant schema MUST remain exclusive to disposable test DbContexts
# until a reviewed migration, identity integration and complete CRUD guard exist.
foreach ($source in @($db, $model)) {
    if ($source -match 'BuildingOperatorAccessGrant|BuildingOperatorAccessGrantDesignConfiguration|BuildingManagementOperatorBuildingGrants') {
        throw 'Safety stop: design-only grant entity was mapped into production EF.'
    }
}

# Match actual declaration/attributes, not comments documenting the absence
# of such registration. Effective DI remains a separate runtime test.
if ($identity -notmatch 'IdentityUserManager manager' -or
    $identity -notmatch 'IsLockedOutAsync' -or
    $identity -match ':\s*IBuildingOperatorActiveAccountReader\s*,\s*ITransientDependency' -or
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

# Fail closed: the staging grant/revoke writer must remain unregistered.
$mutation = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingOperatorChangeCoordinator.cs'
$mutationEf = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Security/EfBuildingOperatorGrantAtomicMutationWriter.cs'
if ($mutation -notmatch 'class DenyAllBuildingOperatorGrantMutationWriter' -or
    $mutation -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $mutationEf -match ':\s*IBuildingOperatorGrantMutationWriter\s*,\s*ITransientDependency' -or
    $mutationEf -match '\[(ExposeServices|Dependency)\s*\(' -or
    $mutationEf -notmatch 'IsolationLevel.Serializable' -or
    $mutationEf -notmatch 'EnsureIssuerGrantAsync' -or
    $mutationEf -notmatch 'RevisionNumber') {
    throw 'Safety stop: staged operator delegation writer lost security guards.'
}

# The reviewed OFFLINE candidate must remain separate from runtime EF and
# never grant DB roles or run destructive SQL.
$grantUp = Read-Source 'etc/phase-audits/SliceA_OperatorBuildingGrant_Up_Candidate.sql'
$grantDown = Read-Source 'etc/phase-audits/SliceA_OperatorBuildingGrant_Down_Candidate.sql'
if ($grantUp -notmatch 'CREATE TABLE "BuildingManagementOperatorBuildingGrants"' -or
    $grantUp -notmatch 'UX_BM_OperatorBuilding_Unrevoked' -or
    $grantUp -notmatch 'FK_BM_OperatorBuildingGrants_Building' -or
    $grantUp -notmatch 'CK_BM_Grant_Revocation' -or
    $grantDown -notmatch 'ACCESS EXCLUSIVE MODE' -or
    $grantDown -notmatch 'IF EXISTS' -or
    $grantDown -notmatch 'RAISE EXCEPTION' -or
    $grantDown -notmatch 'DROP TABLE' -or
    $grantUp -match '(?im)^\s*(?:GRANT\b|CREATE\s+ROLE\b|ALTER\s+ROLE\b|DROP\s+DATABASE\b|START\s+TRANSACTION\b|COMMIT\b|BEGIN\s*(?:TRANSACTION\b|;))' -or
    $grantDown -match '(?im)^\s*(?:GRANT\b|CREATE\s+ROLE\b|ALTER\s+ROLE\b|DROP\s+DATABASE\b|START\s+TRANSACTION\b|COMMIT\b|BEGIN\s*(?:TRANSACTION\b|;))') {
    throw 'Safety stop: unreviewed grant SQL candidate or unsafe SQL operation.'
}
if ($grantDown.IndexOf('LOCK TABLE') -ge $grantDown.IndexOf('IF EXISTS') -or
    $grantDown.IndexOf('IF EXISTS') -ge $grantDown.IndexOf('DROP TABLE')) {
    throw 'Safety stop: grant rollback lock/check/drop ordering changed.'
}

# A candidate scoped read service must remain internal and must not be
# prematurely used as a positive authorization token by old CRUD endpoints.
$scopedRead = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingScopedReadDesignGate.cs'
if ($scopedRead -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $scopedRead -notmatch 'EligibleForDesignOnly') {
    throw 'Safety stop: internal scoped list gate is no longer remote-disabled/design-only.'
}
foreach ($crudPath in @(
    'modules/buildingmanagement/src/BuildingManagement.Application/Buildings/BuildingAppService.cs',
    'modules/buildingmanagement/src/BuildingManagement.Application/Floors/FloorAppService.cs',
    'modules/buildingmanagement/src/BuildingManagement.Application/Rooms/RoomAppService.cs',
    'modules/buildingmanagement/src/BuildingManagement.Application/Contracts/ContractAppService.cs'
)) {
    $crud = Read-Source $crudPath
    if ($crud -match 'BuildingScopedReadDesignGate|BuildingScopedReadDesignStatus|IBuildingScopedCollectionStore') {
        throw "Safety stop: design-only scoped reader was wired into unreviewed CRUD: $crudPath"
    }
}

# Slice A now enforces a separate REAL CRUD policy, not the
# EligibleForDesignOnly preview. Ensure every Building/Floor/Room service
# still calls its guard before business operations. This is only a source
# coverage check; integration/HTTP/DB tests remain mandatory.
$crudPolicy = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingCrudAccessPolicy.cs'
if ($crudPolicy -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $crudPolicy -notmatch 'IBuildingOperatorActiveAccountReader' -or
    $crudPolicy -notmatch 'IBuildingOperatorGrantReader' -or
    $crudPolicy -notmatch 'AbpAuthorizationException') {
    throw 'Safety stop: enforced CRUD policy lost remote-disable or security dependencies.'
}
$buildingCrud = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Buildings/BuildingAppService.cs'
$floorCrud = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Floors/FloorAppService.cs'
$roomCrud = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Rooms/RoomAppService.cs'
foreach ($check in @(
    @{Name='Building'; Text=$buildingCrud; Auth='EnsureBuildingAsync'; Minimum=3},
    @{Name='Floor'; Text=$floorCrud; Auth='EnsureResourceAsync'; Minimum=3},
    @{Name='Room'; Text=$roomCrud; Auth='EnsureResourceAsync'; Minimum=4}
)) {
    if ($check.Text -notmatch 'IBuildingCrudAccessPolicy' -or
        ([regex]::Matches($check.Text, $check.Auth)).Count -lt $check.Minimum -or
        $check.Text -notmatch 'GetAuthorizedBuildingIdsAsync') {
        throw "Safety stop: $($check.Name) CRUD endpoint coverage is incomplete."
    }
}
# Building creation now goes through explicit first-owner bootstrap,
# not the old direct entity insert or design-only Create denial helper.
$bootstrapCoordinator = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Security/BuildingOwnerBootstrapCoordinator.cs'
$atomicWriter = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Security/EfBuildingOwnerBootstrapAtomicWriter.cs'
if ($buildingCrud -notmatch 'CreateWithCurrentOwnerAsync' -or
    $bootstrapCoordinator -notmatch 'RootUserIds\?\.Contains' -or
    $bootstrapCoordinator -notmatch 'BuildingOwnerBootstrapSecurityOptions' -or
    $bootstrapCoordinator -notmatch 'BootstrapOwner' -or
    $bootstrapCoordinator -notmatch 'DenyAllBuildingOwnerBootstrapWriter' -or
    $bootstrapCoordinator -notmatch 'IBuildingOperatorActiveAccountReader' -or
    $bootstrapCoordinator -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $bootstrapCoordinator -notmatch 'IBuildingOwnerBootstrapPrincipalReader' -or
    $atomicWriter -match ':\s*IBuildingOwnerBootstrapWriter\s*,\s*ITransientDependency' -or
    $atomicWriter -match '\[(ExposeServices|Dependency)\s*\(' -or
    $atomicWriter -notmatch 'BeginTransactionAsync' -or
    $atomicWriter -notmatch 'RollbackAsync') {
    throw 'Safety stop: building owner creation lost privilege, transaction or default-deny safeguards.'
}
if ($floorCrud -notmatch 'EnsureBuildingAsync' -or
    $floorCrud -notmatch 'AsyncExecuter.ToListAsync' -or
    $roomCrud -notmatch 'AsyncExecuter.ToListAsync') {
    throw 'Safety stop: first-owner creation / child parent scope / SQL list enforcement missing.'
}
Write-Host 'PASS: all 15 Building/Floor/Room CRUD entry points retain fail-closed scope checks.'
Write-Host 'PASS: building creation routes through default-deny owner bootstrap with unregistered transaction writer.'

Write-Host 'BUILDING SCOPE DESIGN-ONLY GATE PASS.'
Write-Host 'PASS: experimental grant table is not mapped into the runtime DbContext.'
Write-Host 'PASS: ABP Identity adapter is not auto-registered by class markers.'
Write-Host 'PASS: current membership/account defaults are DENY ALL.'
Write-Host 'READ-ONLY SOURCE CHECK: this does NOT prove effective DI, API authorization or database roles.'
