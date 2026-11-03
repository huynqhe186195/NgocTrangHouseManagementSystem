param()

# One-shot, local-only Phase 2G.5 evidence bundle.
# Never runs application DbMigrator, deploys production or edits user SQL.
# The migration/HTTP tests themselves create and remove ONLY disposable PG DBs.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
try {
    $branch = (& git branch --show-current | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
        throw "Safety stop: expected branch wip/phase-2g2, got '$branch'."
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK is required.'
    }
    if ([string]::IsNullOrWhiteSpace($env:BM_PG_TEST_CONNECTION)) {
        throw 'Set BM_PG_TEST_CONNECTION for the isolated local test database first.'
    }

    # Inspect only the local DB name/host/port; never print the password.
    $settings = New-Object System.Data.Common.DbConnectionStringBuilder
    $settings.ConnectionString = $env:BM_PG_TEST_CONNECTION
    if (-not $settings.ContainsKey('Database') -or
        -not $settings.ContainsKey('Host') -or
        -not $settings.ContainsKey('Port') -or
        [string]$settings['Database'] -cne 'ngoctrang_integration_test' -or
        [string]$settings['Host'] -notin @('127.0.0.1', 'localhost') -or
        [int]$settings['Port'] -ne 5432) {
        throw 'Safety stop: only 127.0.0.1/localhost:5432/ngoctrang_integration_test is allowed.'
    }

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
