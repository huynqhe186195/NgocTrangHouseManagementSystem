# Uses the project's EXISTING local Docker PostgreSQL .env credentials
# for the already guarded PGMIG01 disposable migration rehearsal.
# Never prints, commits or stores passwords. No credential reset, new
# users, psql requirement or production database migration.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$envFile = Join-Path $root '.env'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    throw 'Cannot find project-root .env. Local Docker credentials could not be recovered; no database was touched.'
}

$localVariables = @{}
foreach ($line in [System.IO.File]::ReadAllLines($envFile)) {
    if ($line -match '^\s*(POSTGRES_USER|POSTGRES_PASSWORD)\s*=\s*(.*?)\s*$') {
        $value = $Matches[2]
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $localVariables[$Matches[1]] = $value
    }
}
if (-not $localVariables.ContainsKey('POSTGRES_USER') -or
    -not $localVariables.ContainsKey('POSTGRES_PASSWORD') -or
    [string]::IsNullOrWhiteSpace($localVariables['POSTGRES_USER']) -or
    [string]::IsNullOrEmpty($localVariables['POSTGRES_PASSWORD']) -or
    $localVariables['POSTGRES_PASSWORD'] -eq 'your_password_here') {
    throw 'Project .env lacks configured POSTGRES_USER/POSTGRES_PASSWORD. No database was touched.'
}

# ADO.NET/Npgsql connection-string quoting: preserve semicolons and
# embedded double quotes without ever echoing the credential values.
function Quote-ConnectionValue([string]$value) {
    return '"' + $value.Replace('"', '""') + '"'
}

$quotedUser = Quote-ConnectionValue $localVariables['POSTGRES_USER']
$quotedPassword = Quote-ConnectionValue $localVariables['POSTGRES_PASSWORD']
$connectionVariable = 'BM_PG_TEST_CONNECTION'
$original = [Environment]::GetEnvironmentVariable($connectionVariable, 'Process')

try {
    # Only the dedicated local integration DB is selected. The PGMIG01
    # test itself creates and deletes a different, random scratch DB.
    [Environment]::SetEnvironmentVariable(
        $connectionVariable,
        "Host=127.0.0.1;Port=5432;Database=ngoctrang_integration_test;Username=$quotedUser;Password=$quotedPassword",
        'Process')

    Write-Host 'Loaded local .env credentials (secret values hidden).'
    Write-Host 'Starting PGMIG01 with existing PostgreSQL role and guarded test database.'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (
        Join-Path $PSScriptRoot 'test-full-invoice-migration-chain.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'PGMIG01 failed. Review the PostgreSQL/test error above; no credential value was printed.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable($connectionVariable, $original, 'Process')
    Remove-Variable localVariables,quotedUser,quotedPassword -ErrorAction SilentlyContinue
}
