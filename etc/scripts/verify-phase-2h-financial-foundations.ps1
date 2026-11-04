param()
# Phase 2H H2/H3/H4/H6: isolated, one-command local acceptance bundle.
# No production, no user-database migration, no real money movement.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
try {
    $branch = (& git branch --show-current | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
        throw "Safety stop: expected branch wip/phase-2g2; actual '$branch'."
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK is required.'
    }
    if ([string]::IsNullOrWhiteSpace($env:BM_PG_TEST_CONNECTION)) {
        throw 'BM_PG_TEST_CONNECTION must target the dedicated local test database.'
    }
    # The invoked PG test assemblies independently enforce the Npgsql
    # allowlist: localhost:5432, database ngoctrang_integration_test.
    # The authenticated HTTP fixture enforces the same rules before connection.
    $sha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        throw 'Could not obtain HEAD commit.'
    }

    # Stale working trees reproduce already-fixed authorization failures
    # after the expensive migration and integration tests. Stop before work.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $remoteLine = (& git ls-remote --exit-code origin 'refs/heads/wip/phase-2g2' | Out-String).Trim()
        $remoteExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($remoteExit -ne 0) {
        throw 'Safety stop: cannot contact origin/wip/phase-2g2. Check origin/network and retry; no tests were started.'
    }
    if ($remoteLine -match '^([0-9a-fA-F]{40})\s+refs/heads/wip/phase-2g2$') {
        $remoteSha = $Matches[1]
    }
    else {
        throw 'Safety stop: cannot parse origin/wip/phase-2g2 commit. No tests were started.'
    }
    if ($sha -ne $remoteSha) {
        throw "Safety stop: local HEAD=$sha differs from origin/wip/phase-2g2=$remoteSha. Review 'git status --short', then use 'git pull --ff-only origin wip/phase-2g2' when safe. No tests were started."
    }
    $trackedChanges = (& git status --porcelain --untracked-files=no | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace($trackedChanges)) {
        throw 'Safety stop: tracked local edits would make this acceptance non-reproducible. Review git status; do not discard work. No tests were started.'
    }
    $logs = Join-Path $env:USERPROFILE 'NgocTrang-Test-Logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    $domain = '.\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj'
    $pg = '.\modules\buildingmanagement\test\BuildingManagement.PostgreSql.Tests\BuildingManagement.PostgreSql.Tests.csproj'
    $http = '.\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj'

    # Show the actual failing compiler/SQL/test output, not only the
    # wrapper failure at the final throw statement.
    function Throw-WithTestLog([string]$reason, [string]$relativeLogName) {
        $logPath = Join-Path $logs $relativeLogName
        Write-Host "=== FAILED GROUP: $reason ===" -ForegroundColor Red
        if (Test-Path -LiteralPath $logPath) {
            Write-Host "=== Last 120 lines: $logPath ===" -ForegroundColor Yellow
            Get-Content -LiteralPath $logPath -Tail 120 | ForEach-Object { Write-Host $_ }
        }
        throw $reason
    }

    # Windows PowerShell 5.1 promotes native stderr from redirected pipelines
    # to NativeCommandError. With ErrorActionPreference=Stop this aborted the
    # runner at the first xUnit "[FAIL]" line and hid the actual exception.
    # Allow the native process to finish; preserve its exit code and full log.
    # VSTest console output varies by Windows SDK / logger. Use TRX counters
    # as machine-readable acceptance evidence instead of matching prose.
    function Assert-LoggedTestCount([string]$logName, [int]$expectedCount) {
        $trxName = [System.IO.Path]::ChangeExtension($logName, '.trx')
        $trxPath = Join-Path $logs $trxName
        try {
            $verified = & (Join-Path $PSScriptRoot 'Assert-Phase2HTrx.ps1') -Path $trxPath -ExpectedTests $expectedCount
            Write-Host $verified -ForegroundColor Green
        }
        catch {
            Throw-WithTestLog "TRX result validation FAILED for $logName (expected $expectedCount passed): $($_.Exception.Message)" $logName
        }
    }

    function Invoke-DotNetTestLogged(
        [string]$project, [string]$testFilter,
        [string]$verbosity, [string]$logName) {
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            # Mark all logs with the exact source revision to avoid stale-fail confusion.
            $testLogPath = Join-Path $logs $logName
            $trxName = [System.IO.Path]::ChangeExtension($logName, '.trx')
            $trxPath = Join-Path $logs $trxName
            # Never accept a TRX artifact from any earlier test invocation.
            Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue
            Set-Content -LiteralPath $testLogPath -Value "PHASE2H_COMMIT=$sha" -Encoding UTF8
            & dotnet test $project --filter $testFilter --logger $verbosity --logger "trx;LogFileName=$trxName" --results-directory $logs *>&1 |
                ForEach-Object { $_.ToString() } |
                Tee-Object -FilePath $testLogPath -Append |
                Out-Host
            return [int]$LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previousPreference }
    }

    Write-Host "=== PHASE 2H FINANCIAL FOUNDATIONS / COMMIT $sha ==="
    Write-Host '== 1/5: H2-H6 D01-D04/D06 160 dossier and archival source cases =='
    $domainExit = Invoke-DotNetTestLogged $domain 'FullyQualifiedName~RoomChangeMidMonthRentReconciliation_Tests|FullyQualifiedName~RoomChangeDepositDifferencePreview_Tests|FullyQualifiedName~TRF_HAND|FullyQualifiedName~RoomChangeMidMonthFinancialReconciliationPacket_Tests|FullyQualifiedName~RoomChangeDayPricingRuleCandidate_Tests|FullyQualifiedName~RoomChangeOfficialSourceRentEvidenceBridge_Tests|FullyQualifiedName~RoomChangeFinalIndependentSignoffs_Tests|FullyQualifiedName~RoomChangeApprovedH2H3ExecutionPolicy_Tests|FullyQualifiedName~RoomChangeDeferredDepositDisposition_Tests|FullyQualifiedName~RoomScopedServiceTariffPolicy_Tests|FullyQualifiedName~RoomScopedServiceInvoiceSourceBinding_Tests|FullyQualifiedName~RoomChangeDepositDispositionIntent_Tests' 'console;verbosity=normal' '15-2h-financial-domain.log'
    if ($domainExit -ne 0) { Throw-WithTestLog '2H rent/deposit domain tests FAILED.' '15-2h-financial-domain.log' }
    Assert-LoggedTestCount '15-2h-financial-domain.log' 160

    Write-Host '== 2/5: Phase 2H PostgreSQL concurrency and atomic transfer =='
    $pgExit = Invoke-DotNetTestLogged $pg 'FullyQualifiedName~RoomChangeRequestsPostgreSql_Tests|FullyQualifiedName~RoomChangeTenantRosterPostgreSql_Tests|FullyQualifiedName~RoomChangeSourceDispositionPostgreSql_Tests|FullyQualifiedName~RoomChangeHandoverDigestPostgreSql_Tests|FullyQualifiedName~RoomChangeDayPricingRulePostgreSql_Tests|FullyQualifiedName~RoomChangeEvidenceArchivePostgreSql_Tests|FullyQualifiedName~RoomChangeMidMonthFinancialPostingPostgreSql_Tests|FullyQualifiedName~RoomChangeMidMonthCompletionPostgreSql_Tests|FullyQualifiedName~RoomScopedServiceTariffPostgreSql_Tests|FullyQualifiedName~RoomServiceInvoiceSourceBindingsPostgreSql_Tests|FullyQualifiedName~RoomChangeDepositDispositionIntentPostgreSql_Tests' 'console;verbosity=normal' '16-2h-postgresql.log'
    if ($pgExit -ne 0) { Throw-WithTestLog '2H PostgreSQL suite FAILED.' '16-2h-postgresql.log' }
    Assert-LoggedTestCount '16-2h-postgresql.log' 96

    Write-Host '== 3/5: Fresh disposable PostgreSQL migration chain =='
    $env:BM_ALLOW_FULL_MIGRATION_REHEARSAL = 'CREATE_AND_DROP_FRESH_LOCAL_TEST_DATABASE'
    $migrationExit = Invoke-DotNetTestLogged $pg 'FullyQualifiedName~FullInvoiceMigrationChainPostgreSql_Tests' 'console;verbosity=normal' '17-2h-full-migration.log'
    if ($migrationExit -ne 0) { Throw-WithTestLog 'PostgreSQL migration chain FAILED.' '17-2h-full-migration.log' }
    Remove-Item Env:\BM_ALLOW_FULL_MIGRATION_REHEARSAL -ErrorAction SilentlyContinue

    Write-Host '== 4/5: Authenticated two-PG HTTP / Owner DOCX, per-room tariffs, finance and revocation =='
    $env:BM_PG_SLICE_A_HTTP_OPT_IN = 'I_ACCEPT_DISPOSABLE_PG_STAGING_HTTP'
    $httpExit = Invoke-DotNetTestLogged $http 'FullyQualifiedName~SliceAStagingPostgreSqlHttp_Tests' 'console;verbosity=minimal' '18-2h-auth-http.log'
    if ($httpExit -ne 0) { Throw-WithTestLog 'Authenticated PostgreSQL HTTP test FAILED.' '18-2h-auth-http.log' }
    Remove-Item Env:\BM_PG_SLICE_A_HTTP_OPT_IN -ErrorAction SilentlyContinue

    Write-Host '== 5/5: Full isolated Domain / Application / EF / HTTP regression =='
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full *>&1 |
            ForEach-Object { $_.ToString() } |
            Tee-Object -FilePath (Join-Path $logs '19-2h-full-regression.log') |
            Out-Host
    }
    finally { $ErrorActionPreference = $previousPreference }
    if (-not (Select-String -Path (Join-Path $logs '19-2h-full-regression.log') -Pattern '=== QUALITY GATE PASS: ALL REQUESTED TEST RUNS SUCCEEDED ===' -SimpleMatch -Quiet)) {
        Throw-WithTestLog 'Full regression quality gate FAILED.' '19-2h-full-regression.log'
    }

    Write-Host '=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ==='
    Write-Host "COMMIT=$sha"
    Write-Host 'Restricted same-rent/same-deposit mid-month lane requires positive authenticated execution acceptance; H4 top-up/refund and production remain HOLD.'
}
finally {
    Remove-Item Env:\BM_ALLOW_FULL_MIGRATION_REHEARSAL -ErrorAction SilentlyContinue
    Remove-Item Env:\BM_PG_SLICE_A_HTTP_OPT_IN -ErrorAction SilentlyContinue
    Pop-Location
}
