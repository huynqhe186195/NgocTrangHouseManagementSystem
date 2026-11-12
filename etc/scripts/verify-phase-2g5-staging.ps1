param()

# One-shot, local-only Phase 2G.5 evidence bundle.
# Never runs application DbMigrator, deploys production or edits user SQL.
# The migration/HTTP tests themselves create and remove ONLY disposable PG DBs.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
try {
    # P3.1 merged the spec-kit-h4-security lane (PR #5, which contained all
    # of wip/phase-2g2) into the trunk branch; accept the merged trunk too.
    $allowedBranches = @('wip/phase-2g2', 'feat/local-safe-startup-20261109')
    $branch = (& git branch --show-current | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cnotin $allowedBranches) {
        throw "Safety stop: expected a reviewed 2g/2h test branch ($($allowedBranches -join ' or ')), got '$branch'."
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK is required.'
    }
    if ([string]::IsNullOrWhiteSpace($env:BM_PG_TEST_CONNECTION)) {
        throw 'Set BM_PG_TEST_CONNECTION for the isolated local test database first.'
    }

    # The canonical safety guard is the NpgsqlConnectionStringBuilder in
    # BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString().
    # Every invoked PostgreSQL test requires that guard BEFORE connecting:
    # Database=ngoctrang_integration_test, localhost/127.0.0.1, port 5432
    # (CI) or 15432 (local disposable container; port allowlist widened in
    # the same P3.2 guard-adapt commit).
    # Do not reimplement the Npgsql parser with generic PowerShell string
    # parsing: it can misclassify otherwise valid PostgreSQL connection strings.
    # The migration and Phase 2G.5 cases use that canonical C# guard.
    # The authenticated HTTP fixture independently applies the same
    # Npgsql host/port/database allowlist before creating any scratch DB.

    $sha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        throw 'Cannot verify the current Git commit.'
    }
    $logs = Join-Path $env:USERPROFILE 'NgocTrang-Test-Logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $pg = '.\modules\buildingmanagement\test\BuildingManagement.PostgreSql.Tests\BuildingManagement.PostgreSql.Tests.csproj'
    $http = '.\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj'

    Write-Host "=== PHASE 2G.5 / COMMIT $sha / LOCAL DISPOSABLE PG ONLY ==="
    Write-Host '== 1/4: Real PostgreSQL 2G.4 cross-phase + 2G.5 room archive =='
    & dotnet test $pg --filter 'FullyQualifiedName~Phase2G4|FullyQualifiedName~Phase2G5RoomDispositionArchiveIntegrityPostgreSql_Tests' --logger 'console;verbosity=normal' *>&1 |
        Tee-Object -FilePath (Join-Path $logs '11-2g5-archive-pg.log')
    if ($LASTEXITCODE -ne 0) { throw 'Phase 2G.5 PostgreSQL archive tests FAILED.' }
    if (-not (Select-String -Path (Join-Path $logs '11-2g5-archive-pg.log') -Pattern 'Total tests:\s*58' -Quiet) -or
        -not (Select-String -Path (Join-Path $logs '11-2g5-archive-pg.log') -Pattern 'Passed:\s*58' -Quiet)) {
        throw 'PostgreSQL 2G.4 + 2G.5 test count did not match 58/58.'
    }

    Write-Host '== 2/4: Fresh disposable PostgreSQL full migration chain =='
    $env:BM_ALLOW_FULL_MIGRATION_REHEARSAL = 'CREATE_AND_DROP_FRESH_LOCAL_TEST_DATABASE'
    & dotnet test $pg --filter 'FullyQualifiedName~FullInvoiceMigrationChainPostgreSql_Tests' --logger 'console;verbosity=normal' *>&1 |
        Tee-Object -FilePath (Join-Path $logs '12-2g5-full-migration.log')
    if ($LASTEXITCODE -ne 0) { throw 'Full PostgreSQL migration chain FAILED.' }
    Remove-Item Env:\BM_ALLOW_FULL_MIGRATION_REHEARSAL -ErrorAction SilentlyContinue

    Write-Host '== 3/4: Authenticated isolated HTTP + PostgreSQL =='
    $env:BM_PG_SLICE_A_HTTP_OPT_IN = 'I_ACCEPT_DISPOSABLE_PG_STAGING_HTTP'
    & dotnet test $http --filter 'FullyQualifiedName~SliceAStagingPostgreSqlHttp_Tests' --logger 'console;verbosity=normal' *>&1 |
        Tee-Object -FilePath (Join-Path $logs '13-2g5-http-pg.log')
    if ($LASTEXITCODE -ne 0) { throw 'Authenticated HTTP/PostgreSQL test FAILED.' }
    Remove-Item Env:\BM_PG_SLICE_A_HTTP_OPT_IN -ErrorAction SilentlyContinue

    Write-Host '== 4/4: Full isolated .NET regression =='
    & .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full *>&1 |
        Tee-Object -FilePath (Join-Path $logs '14-2g5-full-regression.log')
    if (-not (Select-String -Path (Join-Path $logs '14-2g5-full-regression.log') -SimpleMatch -Quiet -Pattern '=== QUALITY GATE PASS: ALL REQUESTED TEST RUNS SUCCEEDED ===')) {
        throw 'Full regression did not produce the expected PASS evidence.'
    }

    Write-Host '=== PHASE 2G.5 LOCAL VERIFICATION: ALL FOUR GROUPS PASSED ==='
    Write-Host "COMMIT=$sha"
    Write-Host 'Production and live application DB: NOT TOUCHED by this script.'
}
finally {
    Remove-Item Env:\BM_ALLOW_FULL_MIGRATION_REHEARSAL -ErrorAction SilentlyContinue
    Remove-Item Env:\BM_PG_SLICE_A_HTTP_OPT_IN -ErrorAction SilentlyContinue
    Pop-Location
}
