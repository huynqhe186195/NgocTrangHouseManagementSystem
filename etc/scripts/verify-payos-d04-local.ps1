param([switch]$PreflightOnly, [switch]$BuildOnly, [switch]$WriterOnly)

# D04/payOS Windows acceptance bundle. Does NOT use existing local PostgreSQL.
# It launches ONLY a new disposable postgres:16 Docker container, bound to
# 127.0.0.1:15432, and removes that exact container in finally.
# No real payOS credentials, no production database, no ledger posting.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$expectedBranch = 'feat/phase2h-payos-signature-foundation-20261106'
$containerId = ''
$logRoot = ''
$oldEnv = @{}
foreach ($name in @('BM_PAYOS_EF_TEST_DB', 'BM_PAYOS_EF_TEST_PASSWORD',
                    'BM_PAYOS_EF_TEST_PORT', 'BM_INVOICE_MIGRATION_OFFLINE',
                    'BM_PAYOS_D04_REAL_WRITER_TEST_DB')) {
    $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-Capture([string]$command, [string[]]$arguments) {
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = (& $command @arguments 2>&1 | Out-String).Trim()
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    if ($exitCode -ne 0) {
        throw "$command $($arguments -join ' ') failed (exit=$exitCode): $output"
    }
    return $output
}

function Invoke-Logged([string]$step, [string]$command, [string[]]$arguments) {
    $log = Join-Path $logRoot "$step.log"
    Write-Host "=== $step ===" -ForegroundColor Cyan
    $previous = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 represents native stderr as a PowerShell
        # error. Let the process finish and preserve its actual exit code.
        $ErrorActionPreference = 'Continue'
        & $command @arguments *>&1 |
            ForEach-Object { $_.ToString() } |
            Tee-Object -FilePath $log |
            Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    if ($exitCode -ne 0) {
        # Capture the true xUnit failure MESSAGE + stack context first.
        # Generic "Failed to load prune package data" SDK warnings are not
        # a test failure; never let them consume the diagnostic budget.
        Write-Host "=== $step original error and stack ===" -ForegroundColor Red
        if (Test-Path -LiteralPath $log) {
            $logLines = @([System.IO.File]::ReadAllLines($log))
            $start = -1
            for ($i = 0; $i -lt $logLines.Length; $i++) {
                if ($logLines[$i] -match '^\s*Failed\s+.*\[\d+\s*(ms|s)\]' -or
                    $logLines[$i] -match '^\s*(Error Message:|\[xUnit.net.*\[FAIL\])') {
                    $start = $i
                    break
                }
            }
            if ($start -ge 0) {
                $end = [Math]::Min($start + 55, $logLines.Length - 1)
                for ($i = $start; $i -le $end; $i++) {
                    Write-Host $logLines[$i] -ForegroundColor Red
                }
            }
            else {
                $diagnostics = @($logLines | Where-Object {
                    $_ -match '(?i)(error (CS|NU|MSB)[0-9]+|ERROR:|FATAL:|Exception:|Npgsql\.PostgresException|\[FAIL\])'
                } | Select-Object -First 20)
                if ($diagnostics.Count -gt 0) {
                    $diagnostics | ForEach-Object { Write-Host $_ -ForegroundColor Red }
                }
                else {
                    $logLines | Select-Object -Last 22 |
                        ForEach-Object { Write-Host $_ -ForegroundColor Red }
                }
            }
        }
        throw "$step FAILED (exit=$exitCode). Log: $log"
    }
    Write-Host "PASS: $step" -ForegroundColor Green
}

function Assert-Trx([string]$filename, [int]$expectedCount = 0) {
    $path = Join-Path $logRoot $filename
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing TRX evidence: $path"
    }
    [xml]$trx = Get-Content -LiteralPath $path -Raw
    $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if ($null -eq $counters) {
        throw "Missing TRX counters: $path"
    }
    $total = [int]$counters.GetAttribute('total')
    $passed = [int]$counters.GetAttribute('passed')
    $failed = [int]$counters.GetAttribute('failed')
    $skipped = [int]$counters.GetAttribute('notExecuted')
    if ($total -lt 1 -or $passed -lt 1 -or $failed -ne 0 -or $skipped -ne 0 -or
        ($expectedCount -gt 0 -and ($total -ne $expectedCount -or $passed -ne $expectedCount))) {
        throw "TRX failed/empty/skipped/wrong test count: total=$total passed=$passed failed=$failed notExecuted=$skipped expected=$expectedCount ($path)"
    }
    Write-Host "TRX VERIFIED: $filename passed=$passed total=$total" -ForegroundColor Green
}

# Prevent a green general payOS suite from silently excluding the newly
# required authenticated ASP.NET D04-B controller-boundary test.
function Assert-TrxContainsPassed([string]$filename, [string]$testName) {
    $path = Join-Path $logRoot $filename
    [xml]$trx = Get-Content -LiteralPath $path -Raw
    $nodes = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $matched = @($nodes | Where-Object {
        $_.GetAttribute('testName').Contains($testName)
    })
    if ($matched.Count -ne 1 -or
        $matched[0].GetAttribute('outcome') -ne 'Passed') {
        throw "Required D04 HTTP test not passed exactly once: $testName ($path)"
    }
    Write-Host "TRX VERIFIED: D04 authenticated ASP.NET HTTP boundary test PASS: $testName" -ForegroundColor Green
}

function Invoke-D04RealWriterAcceptance {
    # Two separate disposable DBs for REAL BuildingManagement and an ABP
    # Identity-shaped permission/role fixture. Neither is the user's 5432 DB.
    Invoke-Logged '19-create-d04-legacy-test-db' 'docker' @(
        'exec', $containerId, 'createdb', '-U', 'postgres', 'payos_d04_legacy_test')
    Invoke-Logged '19a0-create-d04-identity-test-db' 'docker' @(
        'exec', $containerId, 'createdb', '-U', 'postgres', 'payos_d04_identity_test')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_DB', '1', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PASSWORD', 'disposable-only', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PORT', '15432', 'Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_D04_REAL_WRITER_TEST_DB',
        'I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB', 'Process')
    Invoke-Logged '19a-real-legacy-writer-acceptance' 'dotnet' @(
        'test', $pgTests, '-c', 'Release',
        '--no-build',
        '--filter', 'FullyQualifiedName~D04RealLegacyWriterAcceptance_Tests',
        '--logger', 'trx;LogFileName=payos-d04-real-writer.trx',
        '--results-directory', $logRoot, '--verbosity', 'normal')
    Assert-Trx 'payos-d04-real-writer.trx' 1
    [Environment]::SetEnvironmentVariable('BM_PAYOS_D04_REAL_WRITER_TEST_DB',
        $null, 'Process')
}

function Assert-EfTableCount([string]$database, [int]$expected) {
    $sql = 'SELECT count(*) FROM pg_tables WHERE schemaname = ' +
           "'public'" + ' AND tablename IN (' +
           "'BuildingManagementPayOsPaymentIntents','BuildingManagementPayOsWebhookInbox');"
    $count = Invoke-Capture 'docker' @('exec', $containerId, 'psql', '-U',
        'postgres', '-d', $database, '-At', '-v', 'ON_ERROR_STOP=1', '-c', $sql)
    if ($count.Trim() -ne [string]$expected) {
        throw "Expected $expected payOS tables, got '$count' in $database."
    }
}

Push-Location $root
try {
    # ReadAllLines strips Windows CRLF before comparison.
    # No multiline regex or PowerShell string interpolation is involved.
    $sqlOpenCount = 0
    $sqlCloseCount = 0
    foreach ($sqlLine in [System.IO.File]::ReadAllLines($PSCommandPath)) {
        if ($sqlLine -ceq 'DO $d04$') { $sqlOpenCount++ }
        if ($sqlLine -ceq 'END $d04$;') { $sqlCloseCount++ }
    }
    if ($sqlOpenCount -ne 3 -or $sqlCloseCount -ne 3) {
        throw "D04 SQL preflight failed: opens=$sqlOpenCount closes=$sqlCloseCount; expected 3 each."
    }
    Write-Host 'D04 SQL templates: 3 dollar-quoted blocks verified.' -ForegroundColor Green
    if (($PreflightOnly -and ($BuildOnly -or $WriterOnly)) -or
        ($BuildOnly -and $WriterOnly)) {
        throw 'Choose only one of -PreflightOnly, -BuildOnly or -WriterOnly.'
    }
    if ($PreflightOnly) {
        Write-Host '=== D04 PREFLIGHT ONLY PASS: no builds, database or containers used ===' -ForegroundColor Green
        return
    }

    foreach ($command in @('git','dotnet')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "Missing required tool: $command"
        }
    }

    $branch = Invoke-Capture 'git' @('branch', '--show-current')
    if ($branch -ne $expectedBranch) {
        throw "Safety stop: checkout branch $expectedBranch first (current='$branch')."
    }
    $sha = Invoke-Capture 'git' @('rev-parse', 'HEAD')
    $remoteLine = Invoke-Capture 'git' @('ls-remote', '--exit-code',
        'origin', "refs/heads/$expectedBranch")
    if ($remoteLine -match '^([0-9a-fA-F]{40})\s+') {
        $remoteSha = $Matches[1]
    }
    else {
        throw 'Cannot validate current origin HEAD. Do not run stale tests.'
    }
    if ($sha -ne $remoteSha) {
        throw "Local SHA $sha differs from origin $remoteSha. Use git pull --ff-only; keep local edits safe."
    }
    $tracked = Invoke-Capture 'git' @('status', '--porcelain', '--untracked-files=no')
    if (-not [string]::IsNullOrWhiteSpace($tracked)) {
        throw "Tracked working tree has edits. Review 'git status' before running."
    }



    $sdkList = Invoke-Capture 'dotnet' @('--list-sdks')
    if ($sdkList -notmatch '(?m)^10\.') {
        throw '.NET SDK 10 is required (net10.0 test projects).'
    }
    Write-Host "SOURCE COMMIT: $sha"
    # Build-only mode has no Docker or PostgreSQL dependency. Validate
    # Docker only when a full test run is actually requested.
    if (-not $BuildOnly) {
        $dockerServer = Invoke-Capture 'docker' @('info', '--format', '{{.ServerVersion}}')
        Write-Host "DOCKER SERVER: $dockerServer"
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logRoot = Join-Path (Join-Path $HOME 'NgocTrang-Test-Logs') "payos-$stamp"
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    Set-Content -Path (Join-Path $logRoot 'source-commit.txt') -Value $sha

    $domain = 'modules/buildingmanagement/test/BuildingManagement.Domain.Tests/BuildingManagement.Domain.Tests.csproj'
    $application = 'modules/buildingmanagement/test/BuildingManagement.Application.Tests/BuildingManagement.Application.Tests.csproj'
    $efProject = 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/BuildingManagement.EntityFrameworkCore.csproj'
    $httpProject = 'src/NgocTrangHouseManagementSystem.HttpApi.Host/NgocTrangHouseManagementSystem.HttpApi.Host.csproj'
    $pgTests = 'test/PayOs.Staging.Persistence.Tests/PayOs.Staging.Persistence.Tests.csproj'

    # The narrow build gate performs NO tests, EF migration or database work.
    # Compile each D04-involved project before requiring the full suite.
    if ($BuildOnly) {
        Invoke-Logged '01-domain-build' 'dotnet' @('build', $domain, '-c', 'Release')
        Invoke-Logged '02-application-build' 'dotnet' @('build', $application, '-c', 'Release')
        Invoke-Logged '03-ef-build' 'dotnet' @('build', $efProject, '-c', 'Release')
        Invoke-Logged '04-http-build' 'dotnet' @('build', $httpProject, '-c', 'Release')
        Invoke-Logged '04a-staging-test-compile' 'dotnet' @('build', $pgTests, '-c', 'Release')
        Write-Host '=== PAYOS D04 BUILD ONLY PASS: no tests, database, Docker or EF migration used ===' -ForegroundColor Green
        Write-Host "SOURCE_COMMIT=$sha"
        Write-Host "LOG_DIRECTORY=$logRoot"
        return
    }

    if ($WriterOnly) {
        # Compile tests BEFORE starting any disposable DB container.
        Invoke-Logged '04a-staging-test-compile' 'dotnet' @(
            'build', $pgTests, '-c', 'Release')
    }
    else {
    Invoke-Logged '01-domain' 'dotnet' @('test', $domain,
        '-c', 'Release', '--filter', 'FullyQualifiedName~PayOs',
        '--logger', 'trx;LogFileName=payos-domain.trx',
        '--results-directory', $logRoot, '--verbosity', 'normal')
    Assert-Trx 'payos-domain.trx'

    Invoke-Logged '02-application' 'dotnet' @('test', $application,
        '-c', 'Release', '--filter', 'FullyQualifiedName~PayOs',
        '--logger', 'trx;LogFileName=payos-application.trx',
        '--results-directory', $logRoot, '--verbosity', 'normal')
    Assert-Trx 'payos-application.trx'

    Invoke-Logged '03-ef-build' 'dotnet' @('build', $efProject, '-c', 'Release')
    Invoke-Logged '04-http-build' 'dotnet' @('build', $httpProject, '-c', 'Release')
    # Compile the PostgreSQL staging test assembly BEFORE creating a Docker
    # container. This catches newly-added D04 test source/EF dependencies.
    Invoke-Logged '04a-staging-test-compile' 'dotnet' @(
        'build', $pgTests, '-c', 'Release')
    # Catch HTTP controller discovery / auth middleware regressions BEFORE
    # the disposable PostgreSQL container is created. The test itself uses
    # an in-memory ASP.NET TestServer and requires no database or payOS API.
    Invoke-Logged '04b-d04-authenticated-http-preflight' 'dotnet' @(
        'test', $pgTests, '-c', 'Release', '--no-build',
        '--filter', 'FullyQualifiedName~D04B_HTTP01_Distinct_authenticated_sessions_reach_only_their_allowed_routes',
        '--logger', 'trx;LogFileName=payos-d04-http-preflight.trx',
        '--results-directory', $logRoot, '--verbosity', 'normal')
    Assert-Trx 'payos-d04-http-preflight.trx' 1
    Assert-TrxContainsPassed 'payos-d04-http-preflight.trx' 'D04B_HTTP01_Distinct_authenticated_sessions_reach_only_their_allowed_routes'
    $toolDir = Join-Path $logRoot 'dotnet-ef'
    Invoke-Logged '05-install-isolated-ef-tool' 'dotnet' @(
        'tool', 'install', 'dotnet-ef', '--version', '10.0.9',
        '--tool-path', $toolDir)
    $ef = Join-Path $toolDir 'dotnet-ef.exe'
    if (-not (Test-Path -LiteralPath $ef)) {
        throw "No isolated dotnet-ef executable: $ef"
    }

    [Environment]::SetEnvironmentVariable('BM_INVOICE_MIGRATION_OFFLINE','1','Process')
    Invoke-Logged '06-ef-model-drift' $ef @(
        'migrations', 'has-pending-model-changes',
        '--project', $efProject, '--startup-project', $efProject,
        '--context', 'PayOsPaymentMigrationDbContext')
    Invoke-Logged '07-ef-safety-review' 'powershell' @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        '.\etc\scripts\review-payos-ef-migration.ps1')
    [Environment]::SetEnvironmentVariable('BM_INVOICE_MIGRATION_OFFLINE',$null,'Process')
    }

    # A new isolated database container, not the existing NgocTrang postgres.
    # Loopback-only binding and different local port prevent touching 5432.
    Write-Host '=== Launching disposable PostgreSQL 16 on 127.0.0.1:15432 ==='
    $containerName = "bm-payos-d04-local-$PID"
    $containerId = Invoke-Capture 'docker' @(
        'run', '--detach', '--rm',
        '--name', $containerName,
        '--label', 'bm.scope=temporary-payos-d04-test',
        '--publish', '127.0.0.1:15432:5432',
        '--env', 'POSTGRES_PASSWORD=disposable-only',
        '--env', 'POSTGRES_DB=payos_ef_migration_test',
        'postgres:16')
    if ($containerId -notmatch '^[0-9a-f]{64}$') {
        throw "Docker did not return a valid disposable container ID: $containerId"
    }

    # postgres:16 Docker entrypoint temporarily starts a UNIX-socket-only
    # server to initialize POSTGRES_DB, then stops it and starts the FINAL
    # TCP-listening server. The old pg_isready (no -h) could pass during the
    # temporary server, racing the restart at 08-createdb. Probe TCP only.
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & docker exec $containerId pg_isready -h 127.0.0.1 -p 5432 -U postgres -d payos_ef_migration_test *> $null
            $result = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previous }
        if ($result -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        throw 'Disposable PostgreSQL FINAL TCP server did not become ready. Review docker-postgres-tail.log.'
    }
    Write-Host 'PASS: final disposable PostgreSQL TCP server is accepting connections.' -ForegroundColor Green

    if ($WriterOnly) {
        Invoke-D04RealWriterAcceptance
        Write-Host '=== PAYOS D04 WRITER-ONLY ACCEPTANCE PASS (synthetic authority, not business accepted) ===' -ForegroundColor Green
        Write-Host "SOURCE_COMMIT=$sha"
        Write-Host "LOG_DIRECTORY=$logRoot"
        return
    }

    Invoke-Logged '08-create-rehearsal-db' 'docker' @(
        'exec', $containerId, 'createdb', '-U', 'postgres', 'payos_rehearsal_test')

    $rehearsalSql = Resolve-Path '.\etc\phase-audits\payos-postgresql16-schema-rehearsal.sql'
    Invoke-Logged '09-copy-rehearsal-sql' 'docker' @(
        'cp', [string]$rehearsalSql.Path, "$($containerId):/tmp/rehearsal.sql")
    Invoke-Logged '10-sql-constraints-rollback' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres', '-d',
        'payos_rehearsal_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/rehearsal.sql')

    # Two independent concurrent writers must result in exactly one commit.
    # Both background tasks target this specific fresh disposable container.
    $first = "BEGIN; INSERT INTO payos_rehearsal.payment_intents(id,building_id,payer_id,order_code,amount_vnd,payment_id,expires_at_utc,status) VALUES ('00000000-0000-0000-0000-000000000611','00000000-0000-0000-0000-000000000201','00000000-0000-0000-0000-000000000301',910000,150000,'00000000-0000-0000-0000-000000000711',now()+interval '1 day',0); SELECT pg_sleep(4); COMMIT;"
    $second = "BEGIN; INSERT INTO payos_rehearsal.payment_intents(id,building_id,payer_id,order_code,amount_vnd,payment_id,expires_at_utc,status) VALUES ('00000000-0000-0000-0000-000000000612','00000000-0000-0000-0000-000000000201','00000000-0000-0000-0000-000000000301',910000,150000,'00000000-0000-0000-0000-000000000712',now()+interval '1 day',0); SELECT pg_sleep(4); COMMIT;"
    $writers = @()
    try {
        foreach ($sql in @($first,$second)) {
            $writers += Start-Job -ArgumentList $containerId,$sql -ScriptBlock {
                param($cid,$statement)
                $ErrorActionPreference = 'Continue'
                $result = (& docker exec $cid psql -U postgres -d payos_rehearsal_test -v ON_ERROR_STOP=1 -c $statement 2>&1 | Out-String)
                [pscustomobject]@{ ExitCode = [int]$LASTEXITCODE; Output = $result }
            }
        }
        Wait-Job -Job $writers -Timeout 40 | Out-Null
        if (@($writers | Where-Object { $_.State -ne 'Completed' }).Count -ne 0) {
            throw 'Concurrent SQL writers timed out.'
        }
        $outcomes = @($writers | ForEach-Object { Receive-Job -Job $_ })
        ($outcomes | Format-List | Out-String) |
            Set-Content -Path (Join-Path $logRoot '11-concurrent-duplicate.log')
        $successes = @($outcomes | Where-Object { $_.ExitCode -eq 0 }).Count
        $duplicates = @($outcomes | Where-Object {
            $_.ExitCode -ne 0 -and $_.Output -match 'duplicate key value violates unique constraint'
        }).Count
        $count = Invoke-Capture 'docker' @(
            'exec', $containerId, 'psql', '-U', 'postgres', '-d',
            'payos_rehearsal_test', '-At', '-c',
            'SELECT count(*) FROM payos_rehearsal.payment_intents WHERE order_code=910000')
        if ($successes -ne 1 -or $duplicates -ne 1 -or $count.Trim() -ne '1') {
            throw "Concurrent unique constraint FAILED; success=$successes duplicate=$duplicates count=$count"
        }
        Write-Host 'PASS: one and only one concurrent order committed.' -ForegroundColor Green
    }
    finally { foreach ($job in $writers) { Remove-Job -Job $job -Force -ErrorAction SilentlyContinue } }

    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_DB','1','Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PASSWORD','disposable-only','Process')
    [Environment]::SetEnvironmentVariable('BM_PAYOS_EF_TEST_PORT','15432','Process')

    Invoke-Logged '12-ef-migration-apply' $ef @(
        'database', 'update', 'StagePayOsPaymentIntentsAndWebhookInbox',
        '--context', 'PayOsPaymentMigrationDbContext',
        '--project', $efProject, '--startup-project', $efProject)

    $efSql = Resolve-Path '.\etc\phase-audits\payos-generated-ef-migration-acceptance.sql'
    Invoke-Logged '13-copy-ef-acceptance-sql' 'docker' @(
        'cp', [string]$efSql.Path, "$($containerId):/tmp/ef-acceptance.sql")
    Invoke-Logged '14-ef-constraints' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres', '-d',
        'payos_ef_migration_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/ef-acceptance.sql')
    Assert-EfTableCount 'payos_ef_migration_test' 2

    Invoke-Logged '15-pg-checkout-webhook-http-controller-tests' 'dotnet' @(
        'test', $pgTests, '-c', 'Release',
        '--filter', 'FullyQualifiedName~PayOsStaging',
        '--logger', 'trx;LogFileName=payos-pg.trx',
        '--results-directory', $logRoot, '--verbosity', 'normal')
    Assert-Trx 'payos-pg.trx'
    Assert-TrxContainsPassed 'payos-pg.trx' 'D04B_HTTP01_Distinct_authenticated_sessions_reach_only_their_allowed_routes'

    Invoke-Logged '16-ef-migration-rollback' $ef @(
        'database', 'update', '0',
        '--context', 'PayOsPaymentMigrationDbContext',
        '--project', $efProject, '--startup-project', $efProject)
    Assert-EfTableCount 'payos_ef_migration_test' 0

    Invoke-Logged '17-ef-migration-reapply' $ef @(
        'database', 'update', 'StagePayOsPaymentIntentsAndWebhookInbox',
        '--context', 'PayOsPaymentMigrationDbContext',
        '--project', $efProject, '--startup-project', $efProject)
    Assert-EfTableCount 'payos_ef_migration_test' 2

    # Windows PowerShell 5.1 + docker.exe can remove embedded double
    # quotes from a native -c argument. PostgreSQL then lowercases the
    # mixed-case EF history identifier and incorrectly reports no relation.
    # Send this exact identifier in a SQL file instead of native argv.
    $historySqlFile = Join-Path $logRoot '18-ef-history.sql'
    Set-Content -LiteralPath $historySqlFile -Encoding Ascii -Value @'
SELECT count(*) FROM public."__EFMigrationsHistory_PayOs";
'@
    Invoke-Logged '18-copy-ef-history-sql' 'docker' @(
        'cp', $historySqlFile, "$($containerId):/tmp/ef-history.sql")
    $history = Invoke-Capture 'docker' @('exec', $containerId, 'psql',
        '-U', 'postgres', '-d', 'payos_ef_migration_test', '-At',
        '-v', 'ON_ERROR_STOP=1', '-f', '/tmp/ef-history.sql')
    if ($history.Trim() -ne '1') {
        throw "Expected exactly one payOS migration history row; got $history."
    }
    Write-Host 'PASS: exactly one case-sensitive payOS EF migration history row.' -ForegroundColor Green

    # Two actual databases in one disposable PG16 container. These are
    # protocol rehearsal schemas and synthetic receipts, never the live writer.
    Invoke-D04RealWriterAcceptance
    $d04Sql = Resolve-Path '.\etc\phase-audits\payos-d04-two-db-posting-recovery-rehearsal.sql'
    Invoke-Logged '20-copy-d04-two-db-sql' 'docker' @(
        'cp', [string]$d04Sql.Path, "$($containerId):/tmp/d04-two-db.sql")
    Invoke-Logged '21-d04-two-db-commit-rollback-review' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres',
        '-d', 'payos_ef_migration_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/d04-two-db.sql')

    # This is a mandatory read-back from the *separate* synthetic legacy DB,
    # NOT the payOS DB; never infer a posted receipt from provider PAID alone.
    $proofSqlFile = Join-Path $logRoot '22-d04-legacy-proof.sql'
    Set-Content -LiteralPath $proofSqlFile -Encoding Ascii -Value @'
DO $d04$
BEGIN
 IF (
   SELECT count(*)
   FROM d04_acceptance.receipts r
   JOIN d04_acceptance.payments p ON p.id = r.payment_id
   JOIN d04_acceptance.deposit_settlements s
     ON s.receipt_id = r.id AND s.payment_id = p.id
   JOIN d04_acceptance.outbox o
     ON o.receipt_id = r.id AND o.source_key = r.source_key
   WHERE r.payos_intent_id = '00000000-0000-0000-0000-00000000d401'
     AND r.payment_id = '00000000-0000-0000-0000-00000000d404'
     AND p.building_id = '00000000-0000-0000-0000-00000000d402'
     AND p.status = 1
     AND p.bank_reference = 'D04-SYNTHETIC-REF'
     AND p.amount_vnd = r.amount_vnd AND r.amount_vnd = s.amount_vnd
     AND r.amount_vnd = 150000
     AND r.bank_reference = 'D04-SYNTHETIC-REF'
     AND r.source_key = 'PAYOS:D04:0000000000000000000000000000d401'
     AND r.approved_by = '00000000-0000-0000-0000-00000000d409'
     AND r.evidence_sha256 = repeat('b',64)
     AND o.event_type = 'D04PayOsReceiptCommitted'
 ) <> 1 THEN RAISE EXCEPTION 'D04 source Payment+receipt+settlement+outbox proof missing/conflicting'; END IF;
 RAISE NOTICE 'D04 LEGACY: verified exact journal and outbox from independent database';
END $d04$;
'@
    Invoke-Logged '22-copy-d04-legacy-proof' 'docker' @(
        'cp', $proofSqlFile, "$($containerId):/tmp/d04-legacy-proof.sql")
    Invoke-Logged '23-proof-legacy-commit-before-ack' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres',
        '-d', 'payos_d04_legacy_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/d04-legacy-proof.sql')

    # Two workers race on the same legacy Payment/source key. The loser must
    # roll back its entire receipt+settlement+outbox; it may not add money.
    $raceJobs = @()
    try {
        foreach ($suffix in @('0','1')) {
            $receiptId = if ($suffix -eq '0') {
                '00000000-0000-0000-0000-00000000d430'
            } else { '00000000-0000-0000-0000-00000000d431' }
            $settlementId = if ($suffix -eq '0') {
                '00000000-0000-0000-0000-00000000d432'
            } else { '00000000-0000-0000-0000-00000000d433' }
            $outboxId = if ($suffix -eq '0') {
                '00000000-0000-0000-0000-00000000d434'
            } else { '00000000-0000-0000-0000-00000000d435' }
            $raceSql = @"
BEGIN;
SELECT id FROM d04_acceptance.payments WHERE id='00000000-0000-0000-0000-00000000d412' FOR UPDATE;
UPDATE d04_acceptance.payments SET status=1,bank_reference='D04-CONCURRENT-REF',completed_at_utc=now()
 WHERE id='00000000-0000-0000-0000-00000000d412';
INSERT INTO d04_acceptance.receipts
(id,payment_id,payos_intent_id,source_key,bank_reference,amount_vnd,approved_by,evidence_sha256,created_at_utc)
VALUES ('$receiptId','00000000-0000-0000-0000-00000000d412',
'00000000-0000-0000-0000-00000000d420',
'PAYOS:D04:0000000000000000000000000000d420','D04-CONCURRENT-REF',150000,
'00000000-0000-0000-0000-00000000d421',repeat('c',64),now());
INSERT INTO d04_acceptance.deposit_settlements(id,payment_id,receipt_id,amount_vnd)
VALUES('$settlementId','00000000-0000-0000-0000-00000000d412','$receiptId',150000);
INSERT INTO d04_acceptance.outbox(id,receipt_id,source_key,event_type,created_at_utc)
VALUES('$outboxId','$receiptId','PAYOS:D04:0000000000000000000000000000d420',
'D04PayOsReceiptCommitted',now());
SELECT pg_sleep(2);
COMMIT;
"@
            $raceJobs += Start-Job -ArgumentList $containerId,$raceSql -ScriptBlock {
                param($cid,$statement)
                $ErrorActionPreference = 'Continue'
                $result = (& docker exec $cid psql -U postgres -d payos_d04_legacy_test -v ON_ERROR_STOP=1 -c $statement 2>&1 | Out-String)
                [pscustomobject]@{ ExitCode = [int]$LASTEXITCODE; Output = $result }
            }
        }
        Wait-Job -Job $raceJobs -Timeout 40 | Out-Null
        if (@($raceJobs | Where-Object { $_.State -ne 'Completed' }).Count -ne 0) {
            throw 'D04 double-writer rehearsal timed out.'
        }
        $raceResults = @($raceJobs | ForEach-Object { Receive-Job -Job $_ })
        ($raceResults | Format-List | Out-String) |
            Set-Content -Path (Join-Path $logRoot '23a-d04-concurrent-writers.log')
        $won = @($raceResults | Where-Object { $_.ExitCode -eq 0 }).Count
        $lost = @($raceResults | Where-Object {
            $_.ExitCode -ne 0 -and
            $_.Output -match 'duplicate key value violates unique constraint'
        }).Count
        if ($won -ne 1 -or $lost -ne 1) {
            throw "D04 concurrency requires 1 commit + 1 unique violation, got $won/$lost. Check 23a-d04-concurrent-writers.log"
        }
    }
    finally {
        foreach ($job in $raceJobs) {
            Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        }
    }
    $raceProofFile = Join-Path $logRoot '23b-d04-concurrent-proof.sql'
    Set-Content -LiteralPath $raceProofFile -Encoding Ascii -Value @'
DO $d04$
BEGIN
  IF (SELECT count(*) FROM d04_acceptance.receipts
      WHERE payos_intent_id='00000000-0000-0000-0000-00000000d420')<>1 OR
     (SELECT count(*) FROM d04_acceptance.deposit_settlements
      WHERE payment_id='00000000-0000-0000-0000-00000000d412')<>1 OR
     (SELECT count(*) FROM d04_acceptance.outbox
      WHERE source_key='PAYOS:D04:0000000000000000000000000000d420')<>1 OR
     (SELECT status FROM d04_acceptance.payments
      WHERE id='00000000-0000-0000-0000-00000000d412')<>1 THEN
    RAISE EXCEPTION 'Two concurrent D04 workers created partial or duplicate money';
  END IF;
  RAISE NOTICE 'D04 LEGACY: 1 of 2 concurrent workers committed exactly one financial bundle';
END $d04$;
'@
    Invoke-Logged '23a-copy-concurrent-proof' 'docker' @(
        'cp', $raceProofFile, "$($containerId):/tmp/d04-concurrent-proof.sql")
    Invoke-Logged '23b-verify-no-double-receipt-settlement-outbox' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres',
        '-d', 'payos_d04_legacy_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/d04-concurrent-proof.sql')
    Write-Host 'D04 concurrency PASS: only one of two legacy writers committed.' -ForegroundColor Green

    # ACK only after two independently successful checks: legacy financial
    # journal above, and staging immutable approval/inbox below. This is a
    # test-only ack, not a production cross-DB service or crash-safe worker.
    $ackSqlFile = Join-Path $logRoot '24-d04-staging-ack.sql'
    Set-Content -LiteralPath $ackSqlFile -Encoding Ascii -Value @'
DO $d04$
DECLARE current_status integer;
BEGIN
 SELECT "Status" INTO current_status
 FROM public."BuildingManagementPayOsPaymentIntents"
 WHERE "Id"='00000000-0000-0000-0000-00000000d401'
 FOR UPDATE;
 IF current_status NOT IN (2,3) OR current_status IS NULL THEN
   RAISE EXCEPTION 'D04 unexpected payOS intent status %', current_status;
 END IF;
 IF (SELECT count(*)
     FROM d04_acceptance.independent_reviews r
     JOIN d04_acceptance.owner_approvals a
       ON a.payment_intent_id=r.payment_intent_id
       AND a.reviewer_id=r.reviewer_id
     JOIN public."BuildingManagementPayOsWebhookInbox" w
       ON w."PaymentIntentId"=r.payment_intent_id
       AND w."BuildingId"=r.building_id
       AND w."OrderCode"=r.order_code
       AND w."ProviderReference"=r.bank_reference
     WHERE r.payment_intent_id='00000000-0000-0000-0000-00000000d401'
       AND r.payment_id='00000000-0000-0000-0000-00000000d404'
       AND r.amount_vnd=150000
       AND r.bank_reference='D04-SYNTHETIC-REF'
       AND r.document_sha256=repeat('b',64)
       AND a.reviewed_evidence_sha256=r.document_sha256
       AND a.owner_id<>r.reviewer_id
       AND w."Status"=1) <> 1 THEN
   RAISE EXCEPTION 'D04 immutable independent staging evidence not valid';
 END IF;
 IF current_status=2 THEN
   UPDATE public."BuildingManagementPayOsPaymentIntents"
     SET "Status"=3, "ConcurrencyStamp"='d04-ack'
     WHERE "Id"='00000000-0000-0000-0000-00000000d401'
       AND "Status"=2;
 END IF;
 IF (SELECT "Status" FROM public."BuildingManagementPayOsPaymentIntents"
     WHERE "Id"='00000000-0000-0000-0000-00000000d401')<>3 THEN
     RAISE EXCEPTION 'D04 ack not durable';
 END IF;
 RAISE NOTICE 'D04 STAGING ACK: after legacy proof, replay is read-only PASS';
END $d04$;
'@
    Invoke-Logged '24-copy-d04-staging-ack' 'docker' @(
        'cp', $ackSqlFile, "$($containerId):/tmp/d04-staging-ack.sql")
    Invoke-Logged '25-ack-after-legacy-proof' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres',
        '-d', 'payos_ef_migration_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/d04-staging-ack.sql')
    Invoke-Logged '26-repeated-ack-idempotent' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres',
        '-d', 'payos_ef_migration_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/d04-staging-ack.sql')
    Invoke-Logged '27-legacy-proof-after-repeated-ack' 'docker' @(
        'exec', $containerId, 'psql', '-U', 'postgres',
        '-d', 'payos_d04_legacy_test', '-v', 'ON_ERROR_STOP=1',
        '-f', '/tmp/d04-legacy-proof.sql')
    Write-Host 'D04 TWO-DB PROTOCOL REHEARSAL PASS (not financial writer acceptance).' -ForegroundColor Green

    Write-Host '=== PAYOS D04 LOCAL ISOLATED TEST BUNDLE PASS ===' -ForegroundColor Green
    Write-Host "SOURCE_COMMIT=$sha"
    Write-Host "LOG_DIRECTORY=$logRoot"
    Write-Host 'No production DB, no live payOS payment, no official D04 posting.'
    Write-Host 'D04 BUSINESS ACCEPTANCE remains HOLD pending real HTTP/financial writer evidence.'
}
catch {
    Write-Host "=== PAYOS D04 LOCAL TEST FAILED: $($_.Exception.Message) ===" -ForegroundColor Red
    if ($logRoot) {
        Write-Host "LOG_DIRECTORY=$logRoot" -ForegroundColor Yellow
        # Retain context from ONLY the disposable container, even when
        # the finally block removes it. Do not read other Docker containers.
        if ($containerId) {
            $diagnosticPath = Join-Path $logRoot 'docker-postgres-tail.log'
            $oldPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & docker logs --tail 150 $containerId *>&1 |
                    ForEach-Object { $_.ToString() } |
                    Set-Content -LiteralPath $diagnosticPath
                Write-Host "DISPOSABLE_POSTGRES_LOG=$diagnosticPath" -ForegroundColor Yellow
            }
            catch {
                Write-Host 'Could not preserve disposable PostgreSQL container logs.' -ForegroundColor Yellow
            }
            finally { $ErrorActionPreference = $oldPreference }
        }
    }
    throw
}
finally {
    foreach ($key in $oldEnv.Keys) {
        [Environment]::SetEnvironmentVariable($key, $oldEnv[$key], 'Process')
    }
    if ($containerId) {
        Write-Host 'Removing only the dedicated temporary payOS test container ...'
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & docker rm -f $containerId *> $null
        }
        finally { $ErrorActionPreference = $previous }
    }
    Pop-Location
}
