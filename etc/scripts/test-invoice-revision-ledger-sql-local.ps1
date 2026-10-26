# Phase 2G.3A.7 — explicit opt-in, SQL text replay in a transaction-local schema.
# Never calls database update, DbMigrator, CREATE/DROP DATABASE or EF Migrate().
# Both human-reviewed SQL exports are SHA-256 pinned by the xUnit test.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: expected branch wip/phase-2g2.'
}

$up = Join-Path $root 'etc/phase-audits/Phase2G3A7_RevisionLedger_Patched_Review.sql'
$down = Join-Path $root 'etc/phase-audits/Phase2G3A7_RevisionLedger_Down_Review.sql'
foreach ($path in @($up, $down)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Safety stop: missing human-reviewed local SQL: $path"
    }
}

# Do not require a clean worktree: the user's generated EF migration,
# Designer, model snapshot and SQL files are deliberately uncommitted.
$envFile = Join-Path $root '.env'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    throw 'Safety stop: local .env missing; no DB was touched.'
}
$vars = @{}
foreach ($line in [IO.File]::ReadAllLines($envFile)) {
    if ($line -match '^\s*(POSTGRES_USER|POSTGRES_PASSWORD)\s*=\s*(.*?)\s*$') {
        $value = $Matches[2]
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $vars[$Matches[1]] = $value
    }
}
if (-not $vars.ContainsKey('POSTGRES_USER') -or
    -not $vars.ContainsKey('POSTGRES_PASSWORD') -or
    [string]::IsNullOrWhiteSpace($vars['POSTGRES_USER']) -or
    [string]::IsNullOrWhiteSpace($vars['POSTGRES_PASSWORD']) -or
    $vars['POSTGRES_PASSWORD'] -eq 'your_password_here') {
    throw 'Safety stop: local PostgreSQL test credentials missing.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Safety stop: dotnet CLI missing.'
}

function QuoteConnectionValue([string]$text) {
    return '"' + $text.Replace('"', '""') + '"'
}

$project = Join-Path $root 'modules/buildingmanagement/test/BuildingManagement.PostgreSql.Tests/BuildingManagement.PostgreSql.Tests.csproj'
$names = @('BM_PG_TEST_CONNECTION','BM_PHASE2G3A7_UP_SQL','BM_PHASE2G3A7_DOWN_SQL')
$previous = @{}
foreach ($key in $names) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

try {
    $user = QuoteConnectionValue $vars['POSTGRES_USER']
    $password = QuoteConnectionValue $vars['POSTGRES_PASSWORD']

    [Environment]::SetEnvironmentVariable(
        'BM_PG_TEST_CONNECTION',
        "Host=127.0.0.1;Port=5432;Database=ngoctrang_integration_test;Username=$user;Password=$password",
        'Process')
    [Environment]::SetEnvironmentVariable('BM_PHASE2G3A7_UP_SQL', $up, 'Process')
    [Environment]::SetEnvironmentVariable('BM_PHASE2G3A7_DOWN_SQL', $down, 'Process')

    Write-Host 'Running PGREV01/02 with checksum-pinned reviewed Up and Down SQL.'
    Write-Host 'Target: localhost:5432 / ngoctrang_integration_test only.'
    Write-Host 'Random schema created inside each transaction and always rolled back.'
    Write-Host 'No permanent public schema change, EF migration or billing mutation.'
    Write-Host 'Uncommitted generated migration files remain untouched.'

    & dotnet test $project --filter 'FullyQualifiedName~InvoiceRevisionLedgerSqlPostgreSql_Tests' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) {
        throw 'PGREV SQL rehearsal failed. Do not apply any migration; share the full log.'
    }
}
finally {
    foreach ($key in $names) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
    Remove-Variable vars,user,password -ErrorAction SilentlyContinue
}

Write-Host 'PGREV01/02 PASSED. Test-only PostgreSQL transactions were rolled back.';
