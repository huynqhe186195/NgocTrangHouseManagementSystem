param()

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
                    'BM_PAYOS_EF_TEST_PORT', 'BM_INVOICE_MIGRATION_OFFLINE')) {
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
        throw "$step FAILED (exit=$exitCode). Log: $log"
    }
    Write-Host "PASS: $step" -ForegroundColor Green
}

function Assert-Trx([string]$filename) {
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
    if ($total -lt 1 -or $passed -lt 1 -or $failed -ne 0 -or $skipped -ne 0) {
        throw "TRX failed/empty/skipped: total=$total passed=$passed failed=$failed notExecuted=$skipped ($path)"
    }
    Write-Host "TRX VERIFIED: $filename passed=$passed total=$total" -ForegroundColor Green
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
    foreach ($command in @('git','dotnet','docker')) {
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
    if ($remoteLine -notmatch '^([0-9a-fA-F]{40})\s+') {
        throw 'Cannot validate current origin HEAD. Do not run stale tests.'
    }
    $remoteSha = $Matches[1]
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
    $dockerServer = Invoke-Capture 'docker' @('info', '--format', '{{.ServerVersion}}')
    Write-Host "SOURCE COMMIT: $sha"
    Write-Host "DOCKER SERVER: $dockerServer"

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logRoot = Join-Path (Join-Path $HOME 'NgocTrang-Test-Logs') "payos-$stamp"
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    Set-Content -Path (Join-Path $logRoot 'source-commit.txt') -Value $sha

    $domain = 'modules/buildingmanagement/test/BuildingManagement.Domain.Tests/BuildingManagement.Domain.Tests.csproj'
    $application = 'modules/buildingmanagement/test/BuildingManagement.Application.Tests/BuildingManagement.Application.Tests.csproj'
    $efProject = 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/BuildingManagement.EntityFrameworkCore.csproj'
    $httpProject = 'src/NgocTrangHouseManagementSystem.HttpApi.Host/NgocTrangHouseManagementSystem.HttpApi.Host.csproj'
    $pgTests = 'test/PayOs.Staging.Persistence.Tests/PayOs.Staging.Persistence.Tests.csproj'

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

    $ready = $false
    for ($i = 0; $i -lt 50; $i++) {
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & docker exec $containerId pg_isready -U postgres -d payos_ef_migration_test *> $null
            $result = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previous }
        if ($result -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }

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

    $history = Invoke-Capture 'docker' @('exec', $containerId, 'psql',
        '-U','postgres','-d','payos_ef_migration_test', '-At',
        '-c', 'SELECT count(*) FROM public."__EFMigrationsHistory_PayOs"')
    if ($history.Trim() -ne '1') {
        throw "Expected exactly one payOS migration history row; got $history."
    }

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
