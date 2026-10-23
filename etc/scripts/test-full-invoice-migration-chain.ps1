# Phase 2G.3A.4 — explicit, opt-in test-only PostgreSQL migration rehearsal.
# Creates and deletes ONE freshly named disposable database on localhost:5432
# using the already guarded BM_PG_TEST_CONNECTION. Never runs DbMigrator
# or migration commands against the integration or production databases.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $root 'modules/buildingmanagement/test/BuildingManagement.PostgreSql.Tests/BuildingManagement.PostgreSql.Tests.csproj'

$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: run on wip/phase-2g2 only.'
}

$gitStatus = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0 -or
    @($gitStatus | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
    throw 'Safety stop: worktree must be clean. Commit/stash changes first.'
}

if ([string]::IsNullOrWhiteSpace(
        [Environment]::GetEnvironmentVariable('BM_PG_TEST_CONNECTION', 'Process'))) {
    throw 'Safety stop: BM_PG_TEST_CONNECTION is not set. Do not use a production connection.'
}

$variable = 'BM_ALLOW_FULL_MIGRATION_REHEARSAL'
$previous = [Environment]::GetEnvironmentVariable($variable, 'Process')
[Environment]::SetEnvironmentVariable(
    $variable, 'CREATE_AND_DROP_FRESH_LOCAL_TEST_DATABASE', 'Process')
try {
    Write-Host 'Running isolated full EF migration chain test on LOCAL PostgreSQL.'
    Write-Host 'This temporarily creates a NEW randomly named test database and drops it.'
    Write-Host 'The existing integration test and application databases are not migrated.'
    & dotnet test $project --filter 'FullyQualifiedName~FullInvoiceMigrationChainPostgreSql_Tests'
    if ($LASTEXITCODE -ne 0) {
        throw 'Full migration rehearsal failed. Check the test log for safe cleanup status.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable($variable, $previous, 'Process')
}

Write-Host 'Full migration chain test passed; temporary database was deleted.'
