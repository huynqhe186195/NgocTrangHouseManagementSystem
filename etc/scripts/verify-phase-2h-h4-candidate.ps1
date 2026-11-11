param()
# Merged H4 one-shot isolated tests on the active work branch. No production database.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
$oldMigration = [Environment]::GetEnvironmentVariable('BM_ALLOW_FULL_MIGRATION_REHEARSAL','Process')
$oldHttp = [Environment]::GetEnvironmentVariable('BM_PG_SLICE_A_HTTP_OPT_IN','Process')
try {
    $branch = (& git branch --show-current | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
        throw "Expected merged work branch wip/phase-2g2, got $branch"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK not found'
    }
    if ((& dotnet --list-sdks | Out-String) -notmatch '(?m)^10\.') {
        throw 'dotnet SDK 10 required'
    }
    $sha = (& git rev-parse HEAD | Out-String).Trim()
    $dirty = (& git status --porcelain --untracked-files=no | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $dirty) {
        throw 'Tracked local edits present; review and commit/stash explicitly'
    }
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $remote = (& git ls-remote --exit-code origin 'refs/heads/wip/phase-2g2' | Out-String).Trim()
        $remoteExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorAction }
    if ($remoteExit -ne 0 -or $remote -notmatch '^([0-9a-fA-F]{40})\s+refs/heads/wip/phase-2g2$') {
        throw 'Cannot verify exact merged work branch head'
    }
    $remoteSha = $Matches[1]
    if ($sha -ne $remoteSha) { throw "Local SHA $sha differs from origin $remoteSha" }
    $raw = [Environment]::GetEnvironmentVariable('BM_PG_TEST_CONNECTION','Process')
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'Missing local BM_PG_TEST_CONNECTION' }
    try {
        $conn = New-Object System.Data.Common.DbConnectionStringBuilder
        $conn.ConnectionString = $raw
        $pgHost = [string]$conn['Host']
        $pgPort = [string]$conn['Port']
        $pgDatabase = [string]$conn['Database']
    }
    catch { throw 'Invalid PostgreSQL connection string' }
    if ($pgHost -notin @('localhost','127.0.0.1') -or
        $pgPort -ne '5432' -or
        $pgDatabase -ne 'ngoctrang_integration_test') {
        throw 'Unsafe DB target: only local 5432/ngoctrang_integration_test is allowed'
    }
    $logs = Join-Path $env:USERPROFILE 'NgocTrang-Test-Logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    function Run-Group([string]$name,[string]$project,[string]$filter,[int]$expected,[int]$minimum) {
        $log = Join-Path $logs ($name+'.log')
        $trxName = $name+'.trx'
        $trx = Join-Path $logs $trxName
        Remove-Item -LiteralPath $trx -ErrorAction SilentlyContinue -Force
        Set-Content -LiteralPath $log -Value "H4_CANDIDATE_SHA=$sha" -Encoding UTF8
        $arguments = @('test',$project,'--logger','console;verbosity=normal',
            '--logger',"trx;LogFileName=$trxName",'--results-directory',$logs)
        if ($filter) { $arguments += @('--filter',$filter) }
        $previous = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & dotnet @arguments *>&1 | ForEach-Object { $_.ToString() } |
                Tee-Object -FilePath $log -Append | Out-Host
            $exit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previous }
        if ($exit -ne 0 -or -not (Test-Path -LiteralPath $trx)) {
            Get-Content -LiteralPath $log -Tail 120 | Out-Host
            throw "$name failed or missing TRX; see $log"
        }
        [xml]$xml = Get-Content -LiteralPath $trx -Raw
        $nodes = $xml.GetElementsByTagName('Counters')
        if ($nodes.Count -ne 1) { throw "Missing TRX counters: $name" }
        $c = $nodes[0]
        $total = [int]$c.GetAttribute('total')
        $passed = [int]$c.GetAttribute('passed')
        $failed = [int]$c.GetAttribute('failed')
        $skipped = [int]$c.GetAttribute('notExecuted')
        if ($total -lt $minimum -or ($expected -gt 0 -and $total -ne $expected) -or
            $failed -ne 0 -or $skipped -ne 0 -or $passed -ne $total) {
            throw "Failed TRX $name: total=$total passed=$passed failed=$failed skipped=$skipped; see $log"
        }
        if ($name -eq 'h4-06-authenticated-http') {
            $positive = @($xml.GetElementsByTagName('UnitTestResult') |
                Where-Object {
                    $_.GetAttribute('testName') -like '*SliceAStagingPostgreSqlHttp_Tests*' -and
                    $_.GetAttribute('outcome') -eq 'Passed'
                })
            if ($positive.Count -eq 0) {
                throw 'Authenticated two-PostgreSQL HTTP result missing or not passed'
            }
        }
        Write-Host "PASS $name $passed/$total (SHA $sha)" -ForegroundColor Green
    }

    $domain = '.\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj'
    $app = '.\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj'
    $ef = '.\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj'
    $pg = '.\modules\buildingmanagement\test\BuildingManagement.PostgreSql.Tests\BuildingManagement.PostgreSql.Tests.csproj'
    $http = '.\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj'
    $filter = 'FullyQualifiedName~RoomChangeRequestsPostgreSql_Tests|FullyQualifiedName~RoomChangeTenantRosterPostgreSql_Tests|FullyQualifiedName~RoomChangeSourceDispositionPostgreSql_Tests|FullyQualifiedName~RoomChangeHandoverDigestPostgreSql_Tests|FullyQualifiedName~RoomChangeDayPricingRulePostgreSql_Tests|FullyQualifiedName~RoomChangeEvidenceArchivePostgreSql_Tests|FullyQualifiedName~RoomChangeMidMonthFinancialPostingPostgreSql_Tests|FullyQualifiedName~RoomChangeMidMonthCompletionPostgreSql_Tests|FullyQualifiedName~RoomScopedServiceTariffPostgreSql_Tests|FullyQualifiedName~RoomServiceInvoiceSourceBindingsPostgreSql_Tests|FullyQualifiedName~RoomChangeDepositDispositionIntentPostgreSql_Tests'

    Write-Host "=== PHASE 2H H4 MERGED-BRANCH ACCEPTANCE SHA=$sha ==="
    Run-Group 'h4-01-domain-full' $domain '' 0 700
    Run-Group 'h4-02-application-full' $app '' 0 1
    Run-Group 'h4-03-ef-full' $ef '' 0 1
    Run-Group 'h4-04-pg-103' $pg $filter 103 103
    [Environment]::SetEnvironmentVariable('BM_ALLOW_FULL_MIGRATION_REHEARSAL',
        'CREATE_AND_DROP_FRESH_LOCAL_TEST_DATABASE','Process')
    Run-Group 'h4-05-migrations' $pg 'FullyQualifiedName~FullInvoiceMigrationChainPostgreSql_Tests' 1 1
    [Environment]::SetEnvironmentVariable('BM_ALLOW_FULL_MIGRATION_REHEARSAL',$oldMigration,'Process')
    [Environment]::SetEnvironmentVariable('BM_PG_SLICE_A_HTTP_OPT_IN',
        'I_ACCEPT_DISPOSABLE_PG_STAGING_HTTP','Process')
    Run-Group 'h4-06-authenticated-http' $http '' 0 75
    Write-Host "=== H4 MERGED-BRANCH EXISTING TESTS PASS AT SHA=$sha ===" -ForegroundColor Green
    Write-Host 'H4 staged transfer/rollback was accepted in isolated CI; actual bank refund and applying the future rent credit remain HOLD. No production.'
}
finally {
    [Environment]::SetEnvironmentVariable('BM_ALLOW_FULL_MIGRATION_REHEARSAL',$oldMigration,'Process')
    [Environment]::SetEnvironmentVariable('BM_PG_SLICE_A_HTTP_OPT_IN',$oldHttp,'Process')
    Pop-Location
}
