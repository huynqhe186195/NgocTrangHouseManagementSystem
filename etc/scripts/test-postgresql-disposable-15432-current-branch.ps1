[CmdletBinding()]
param(
    [ValidateSet('All', 'Migration', 'Revision', 'Membership')]
    [string]$Scenario = 'All'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This runner is intentionally separate from the historical 5432/phase-2g2
# launchers. It only enables test-local opt-ins in this PowerShell process.
$containerName = 'bm-p21-pg-15432'
$allowedHost = '127.0.0.1'
$allowedPort = 15432
$allowedDatabase = 'ngoctrang_integration_test'
$databaseNamePattern = '^[A-Za-z0-9_]+$'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $root 'modules/buildingmanagement/test/BuildingManagement.PostgreSql.Tests/BuildingManagement.PostgreSql.Tests.csproj'
$upSql = Join-Path $root 'etc/phase-audits/Phase2G3A7_RevisionLedger_Patched_Review.sql'
$downSql = Join-Path $root 'etc/phase-audits/Phase2G3A7_RevisionLedger_Down_Review.sql'
$envFile = Join-Path $root '.env'

function Restore-ProcessEnvironment {
    param([hashtable]$Saved)

    foreach ($key in $Saved.Keys) {
        [Environment]::SetEnvironmentVariable($key, $Saved[$key], 'Process')
    }
}

function Read-LocalPostgresSettings {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Safety stop: project-root .env is missing; no database was touched.'
    }

    $settings = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\s*(POSTGRES_USER|POSTGRES_PASSWORD)\s*=\s*(.*?)\s*$') {
            $value = $Matches[2].Trim()
            if ($value.Length -ge 2 -and
                (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                 ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            $settings[$Matches[1]] = $value
        }
    }

    if (-not $settings.ContainsKey('POSTGRES_USER') -or
        -not $settings.ContainsKey('POSTGRES_PASSWORD') -or
        [string]::IsNullOrWhiteSpace($settings['POSTGRES_USER']) -or
        [string]::IsNullOrWhiteSpace($settings['POSTGRES_PASSWORD']) -or
        $settings['POSTGRES_PASSWORD'] -eq 'your_password_here') {
        throw 'Safety stop: .env lacks usable disposable PostgreSQL credentials.'
    }

    return $settings
}

function Quote-ConnectionValue {
    param([string]$Value)

    return '"' + $Value.Replace('"', '""') + '"'
}

function Assert-DisposableContainer {
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $docker) {
        throw 'Safety stop: Docker CLI is not available.'
    }

    $ports = (& docker ps --filter "name=^/$containerName$" --format '{{.Ports}}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $ports -notmatch [regex]::Escape('127.0.0.1:15432->5432/tcp')) {
        throw "Safety stop: $containerName must publish 127.0.0.1:15432->5432/tcp; no database command was run."
    }
}

function Assert-DisposableDatabase {
    param([string]$Username)

    if ($allowedDatabase -notmatch $databaseNamePattern) {
        throw 'Safety stop: internal database allowlist is invalid.'
    }

    # This is a read-only probe through the already-running disposable
    # container. It does not create, drop, migrate, or alter any database.
    $probeSql = "SELECT current_database(), (SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = '$Username');"
    $probe = ($probeSql | & docker exec -i $containerName psql -U postgres -d $allowedDatabase -tA 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Safety stop: disposable database probe failed.'
    }

    $parts = $probe -split '\|'
    if ($parts.Count -lt 2 -or $parts[0] -ne $allowedDatabase) {
        throw 'Safety stop: database probe did not resolve the allowlisted disposable database.'
    }
}

function Assert-ConnectionString {
    param([string]$ConnectionString)

    $hostMatch = [regex]::Match($ConnectionString, '(?i)(?:^|;)Host=("[^"]*"|[^;]*)')
    $portMatch = [regex]::Match($ConnectionString, '(?i)(?:^|;)Port=(\d+)')
    $databaseMatch = [regex]::Match($ConnectionString, '(?i)(?:^|;)Database=("[^"]*"|[^;]*)')

    if (-not $hostMatch.Success -or -not $portMatch.Success -or
        -not $databaseMatch.Success) {
        throw 'Safety stop: generated PostgreSQL connection string is incomplete.'
    }

    $hostValue = $hostMatch.Groups[1].Value.Trim('"')
    $port = [int]$portMatch.Groups[1].Value
    $database = $databaseMatch.Groups[1].Value.Trim('"')
    if ($hostValue -ne $allowedHost -or $port -ne $allowedPort -or
        $database -ne $allowedDatabase) {
        throw 'Safety stop: runner connection is outside the disposable 15432 allowlist.'
    }
}

function Invoke-TestGroup {
    param(
        [string]$Name,
        [string]$Filter
    )

    Write-Host "===== $Name ====="
    Write-Host "Target: $allowedHost`:$allowedPort / $allowedDatabase"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # PowerShell 5.1 surfaces native stderr as ErrorRecord objects. Keep
        # those test diagnostics visible without aborting before we can record
        # dotnet's exit code and continue with the remaining guarded groups.
        $ErrorActionPreference = 'Continue'
        & dotnet test $project --no-restore --filter $Filter --logger 'console;verbosity=minimal' 2>&1 |
            ForEach-Object { Write-Host ([string]$_) }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($code -eq 0) {
        Write-Host "GROUP_RESULT=PASSED ($Name)"
        return [pscustomobject]@{ Name = $Name; ExitCode = 0 }
    }

    Write-Warning "GROUP_RESULT=FAILED ($Name), exit code $code"
    return [pscustomobject]@{ Name = $Name; ExitCode = $code }
}

$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    throw 'Safety stop: repository is detached or the current branch cannot be determined.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Safety stop: dotnet CLI is not available.'
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw 'Safety stop: PostgreSQL test project is missing.'
}

$settings = Read-LocalPostgresSettings -Path $envFile
Assert-DisposableContainer
Assert-DisposableDatabase -Username $settings['POSTGRES_USER']

$quotedUser = Quote-ConnectionValue -Value $settings['POSTGRES_USER']
$quotedPassword = Quote-ConnectionValue -Value $settings['POSTGRES_PASSWORD']
$connection = "Host=$allowedHost;Port=$allowedPort;Database=$allowedDatabase;Username=$quotedUser;Password=$quotedPassword;Timeout=15"
Assert-ConnectionString -ConnectionString $connection

$allKeys = @(
    'BM_PG_TEST_CONNECTION',
    'BM_ALLOW_FULL_MIGRATION_REHEARSAL',
    'BM_PG_SLICE_A_FULL_CHAIN_OPT_IN',
    'BM_ALLOW_REVISION_FULL_CHAIN_REHEARSAL',
    'BM_PHASE2G3A7_UP_SQL',
    'BM_PHASE2G3A7_DOWN_SQL',
    'BM_PG_MEMBERSHIP_CONCURRENCY_OPT_IN'
)
$saved = @{}
foreach ($key in $allKeys) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

$results = @()
try {
    [Environment]::SetEnvironmentVariable('BM_PG_TEST_CONNECTION', $connection, 'Process')

    if ($Scenario -in @('All', 'Migration')) {
        [Environment]::SetEnvironmentVariable(
            'BM_ALLOW_FULL_MIGRATION_REHEARSAL',
            'CREATE_AND_DROP_FRESH_LOCAL_TEST_DATABASE', 'Process')
        [Environment]::SetEnvironmentVariable(
            'BM_PG_SLICE_A_FULL_CHAIN_OPT_IN',
            'CREATE_AND_DROP_RANDOM_LOCAL_SLICE_A_DATABASE', 'Process')

        $results += Invoke-TestGroup -Name 'Full invoice migration chain' `
            -Filter 'FullyQualifiedName~FullInvoiceMigrationChainPostgreSql_Tests'
        $results += Invoke-TestGroup -Name 'Slice A and Slice B migration rehearsals' `
            -Filter 'FullyQualifiedName~SliceAFullChainUpgradePostgreSql_Tests|FullyQualifiedName~SliceBFullFinancialMigrationChainPostgreSql_Tests'
    }

    if ($Scenario -in @('All', 'Revision')) {
        foreach ($path in @($upSql, $downSql)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Safety stop: reviewed revision SQL is missing: $path"
            }
        }

        [Environment]::SetEnvironmentVariable(
            'BM_PHASE2G3A7_UP_SQL', $upSql, 'Process')
        [Environment]::SetEnvironmentVariable(
            'BM_PHASE2G3A7_DOWN_SQL', $downSql, 'Process')
        [Environment]::SetEnvironmentVariable(
            'BM_ALLOW_REVISION_FULL_CHAIN_REHEARSAL',
            'CREATE_AND_DROP_RANDOM_LOCAL_REVISION_TEST_DATABASE', 'Process')

        $results += Invoke-TestGroup -Name 'Revision ledger reviewed SQL rehearsal' `
            -Filter 'FullyQualifiedName~InvoiceRevisionLedgerSqlPostgreSql_Tests'
        $results += Invoke-TestGroup -Name 'Revision full-chain and populated rollback' `
            -Filter 'FullyQualifiedName~InvoiceRevisionFullChainPostgreSql_Tests|FullyQualifiedName~InvoiceRevisionPopulatedRollbackPostgreSql_Tests'
    }

    if ($Scenario -in @('All', 'Membership')) {
        [Environment]::SetEnvironmentVariable(
            'BM_PG_MEMBERSHIP_CONCURRENCY_OPT_IN',
            'I_ACCEPT_EPHEMERAL_SCHEMA', 'Process')

        $results += Invoke-TestGroup -Name 'Membership candidate and concurrency' `
            -Filter 'FullyQualifiedName~BuildingOperatorGrantCandidatePostgreSql_Tests|FullyQualifiedName~BuildingOperatorGrantConcurrencyPostgreSql_Tests'
    }
}
finally {
    Restore-ProcessEnvironment -Saved $saved
    Remove-Variable settings,quotedUser,quotedPassword,connection -ErrorAction SilentlyContinue
}

Write-Host '===== GUARDED RUN SUMMARY ====='
Write-Host "BRANCH=$branch"
Write-Host "TARGET=$allowedHost`:$allowedPort/$allowedDatabase"
foreach ($result in $results) {
    $status = 'FAILED'
    if ($result.ExitCode -eq 0) {
        $status = 'PASSED'
    }
    Write-Host "$($result.Name): $status"
}

if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) {
    exit 1
}
Write-Host 'All selected guarded PostgreSQL groups passed.'
