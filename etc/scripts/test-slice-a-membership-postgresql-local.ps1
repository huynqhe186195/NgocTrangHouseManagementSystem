# Slice A membership PostgreSQL probes. Explicit local opt-in only.
# Never runs EF migrations, DbMigrator, SQL against the public schema, or
# any remote database. Concurrency mode commits a RANDOM scratch schema
# and removes that exact schema in finally; abrupt process termination may
# leave a scratch schema requiring MANUAL investigation.
param([switch]$IncludeConcurrency)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: expected wip/phase-2g2 branch.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Safety stop: .NET SDK not installed.'
}
$envFile = Join-Path $root '.env'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    throw 'Safety stop: local .env is missing; no DB connection attempted.'
}
$settings = @{}
foreach ($line in [IO.File]::ReadAllLines($envFile)) {
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
    throw 'Safety stop: local PostgreSQL test credentials missing.'
}

function QuoteConnectionValue([string]$value) {
    return '"' + $value.Replace('"', '""') + '"'
}
$previousConnection = [Environment]::GetEnvironmentVariable('BM_PG_TEST_CONNECTION','Process')
$previousOptIn = [Environment]::GetEnvironmentVariable(
    'BM_PG_MEMBERSHIP_CONCURRENCY_OPT_IN','Process')
try {
    $dbUser = QuoteConnectionValue $settings['POSTGRES_USER']
    $dbPassword = QuoteConnectionValue $settings['POSTGRES_PASSWORD']
    $env:BM_PG_TEST_CONNECTION = "Host=127.0.0.1;Port=5432;Database=ngoctrang_integration_test;Username=$dbUser;Password=$dbPassword"
    $env:BM_PG_MEMBERSHIP_CONCURRENCY_OPT_IN = $null

    $filter = 'FullyQualifiedName~BuildingOperatorGrantCandidatePostgreSql_Tests'
    if ($IncludeConcurrency) {
        $filter += '|FullyQualifiedName~BuildingOperatorGrantConcurrencyPostgreSql_Tests'
        $env:BM_PG_MEMBERSHIP_CONCURRENCY_OPT_IN = 'I_ACCEPT_EPHEMERAL_SCHEMA'
        Write-Host 'EXPLICIT CONCURRENCY OPT-IN: a random scratch schema will be COMMITTED temporarily.'
        Write-Host 'Only that schema is dropped in finally. A crash can leave an orphan scratch schema.'
    } else {
        Write-Host 'Rollback-only mode: all DDL and rows remain in rolled-back transactions.'
    }

    Write-Host 'Dedicated target: 127.0.0.1:5432 / ngoctrang_integration_test.'
    Write-Host 'No EF Migrate, DbMigrator, production schema, role changes or financial commands.'
    $project = Join-Path $root 'modules/buildingmanagement/test/BuildingManagement.PostgreSql.Tests/BuildingManagement.PostgreSql.Tests.csproj'
    & dotnet test $project --filter $filter --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) {
        throw 'Slice A membership PostgreSQL probes FAILED. Do not deploy or apply migrations.'
    }
    Write-Host 'PASS: requested Slice A PostgreSQL membership probes.'
}
finally {
    [Environment]::SetEnvironmentVariable(
        'BM_PG_TEST_CONNECTION', $previousConnection, 'Process')
    [Environment]::SetEnvironmentVariable(
        'BM_PG_MEMBERSHIP_CONCURRENCY_OPT_IN', $previousOptIn, 'Process')
    Remove-Variable settings,dbUser,dbPassword -ErrorAction SilentlyContinue
}
