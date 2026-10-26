# Phase 2G.3A.7 — explicit opt-in full EF migration chain rehearsal.
# Creates AND drops one random database on localhost:5432 only, after
# name/role/SQL checks. Never migrates ngoctrang_integration_test itself.
# Requires the reviewed V2 migration from user's uncommitted local files.
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
$migration = Join-Path $root 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs'
$designer = Join-Path $root 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.Designer.cs'

foreach ($path in @($up, $down, $migration, $designer)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Safety stop: required reviewed/local file does not exist: $path"
    }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Safety stop: dotnet CLI not available.'
}

# Do NOT demand a clean Git worktree. The four generated files in this
# phase are intentionally still under manual review and uncommitted.
$envFile = Join-Path $root '.env'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    throw 'Safety stop: project root .env not available.'
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
    throw 'Safety stop: local test PostgreSQL credentials are missing.'
}

function QuoteConnectionValue([string]$value) {
    return '"' + $value.Replace('"', '""') + '"'
}

$keys = @(
    'BM_PG_TEST_CONNECTION',
    'BM_PHASE2G3A7_UP_SQL',
    'BM_PHASE2G3A7_DOWN_SQL',
    'BM_ALLOW_REVISION_FULL_CHAIN_REHEARSAL'
)
$previous = @{}
foreach ($key in $keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

$project = Join-Path $root 'modules/buildingmanagement/test/BuildingManagement.PostgreSql.Tests/BuildingManagement.PostgreSql.Tests.csproj'

try {
    $user = QuoteConnectionValue $vars['POSTGRES_USER']
    $password = QuoteConnectionValue $vars['POSTGRES_PASSWORD']

    [Environment]::SetEnvironmentVariable(
        'BM_PG_TEST_CONNECTION',
        "Host=127.0.0.1;Port=5432;Database=ngoctrang_integration_test;Username=$user;Password=$password",
        'Process')
    [Environment]::SetEnvironmentVariable(
        'BM_PHASE2G3A7_UP_SQL', $up, 'Process')
    [Environment]::SetEnvironmentVariable(
        'BM_PHASE2G3A7_DOWN_SQL', $down, 'Process')
    [Environment]::SetEnvironmentVariable(
        'BM_ALLOW_REVISION_FULL_CHAIN_REHEARSAL',
        'CREATE_AND_DROP_RANDOM_LOCAL_REVISION_TEST_DATABASE',
        'Process')

    Write-Host 'PGREV03: full EF migration chain Up -> empty Down -> Up.'
    Write-Host 'ONLY guarded localhost test credentials are used.'
    Write-Host 'PGREV03 creates one random temporary PostgreSQL database,'
    Write-Host 'runs the chain, then DROPS only the just-created database.'
    Write-Host 'No SQL is applied to ngoctrang_integration_test or app databases.'
    Write-Host 'Local migration, Designer, Snapshot and SQL files are preserved.'
    Write-Host 'Do not run this against shared PostgreSQL server instances.'

    & dotnet test $project --filter 'FullyQualifiedName~InvoiceRevisionFullChainPostgreSql_Tests' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) {
        throw 'PGREV03 failed. Read failure stage and cleanup status; do not migrate app databases.'
    }
}
finally {
    foreach ($key in $keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
    Remove-Variable vars,user,password -ErrorAction SilentlyContinue
}

Write-Host 'PGREV03 PASSED; random scratch database created and deleted.';
