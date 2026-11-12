# Phase 2G.3A.4 — OFFLINE PostgreSQL SQL preview only.
# Does NOT connect, migrate, create databases, run DbMigrator or apply SQL.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repoRoot 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/BuildingManagement.EntityFrameworkCore.csproj'
$startup = Join-Path $repoRoot 'src/NgocTrangHouseManagementSystem.DbMigrator/NgocTrangHouseManagementSystem.DbMigrator.csproj'
$output = Join-Path $repoRoot 'etc/phase-audits/Phase2G3A4_InvoiceDraftSchema_Review.sql'

$from = '20261023103354_Phase2G2B1_AddMeterReplacements'
$to = '20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots'

$branch = (& git -C $repoRoot branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: run this only on branch wip/phase-2g2.'
}
$dirty = @(& git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or
    @($dirty | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
    throw 'Safety stop: commit or stash unrelated changes before SQL preview.'
}
if (Test-Path -LiteralPath $output) {
    throw 'SQL preview already exists. Review it before generating another copy.'
}

$previous = [Environment]::GetEnvironmentVariable('BM_INVOICE_MIGRATION_OFFLINE', 'Process')
[Environment]::SetEnvironmentVariable('BM_INVOICE_MIGRATION_OFFLINE', '1', 'Process')
try {
    & dotnet ef migrations script $from $to `
        --project $project `
        --startup-project $startup `
        --context BuildingManagementDbContext `
        --output $output
    if ($LASTEXITCODE -ne 0) {
        throw 'SQL preview generation failed. No database changes were made.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        'BM_INVOICE_MIGRATION_OFFLINE', $previous, 'Process')
}

if (-not (Test-Path -LiteralPath $output)) {
    throw 'Missing generated SQL preview.'
}
$sql = [IO.File]::ReadAllText($output)
$expectedTables = @(
    'BuildingManagementInvoices',
    'BuildingManagementInvoiceItems',
    'BuildingManagementInvoiceItemMeterSources'
)
foreach ($table in $expectedTables) {
    if (-not $sql.Contains('CREATE TABLE "' + $table + '"')) {
        throw "Safety stop: expected new table $table missing from SQL preview."
    }
}
$createTableCount = [regex]::Matches($sql, '(?i)\bCREATE\s+TABLE\b').Count
if ($createTableCount -ne 3) {
    throw "Safety stop: expected 3 CREATE TABLE statements, got $createTableCount."
}
foreach ($index in @(
    'UX_BM_Invoice_Contract_Period',
    'UX_BM_Invoice_Room_Period',
    'UX_BM_InvoiceItem_Invoice_Utility',
    'UX_BM_InvoiceSource_Global',
    'UX_BM_InvoiceSource_Item_Type_Source'
)) {
    if (-not $sql.Contains('CREATE UNIQUE INDEX "' + $index + '"')) {
        throw "Safety stop: expected unique index $index missing."
    }
}
if ($sql -match '(?im)^\s*(?:DROP|DELETE|UPDATE|TRUNCATE)\s+' -or
    $sql -match '(?i)\bCREATE\s+DATABASE\b') {
    throw 'Safety stop: potentially destructive or database creation statement found.'
}
if (-not $sql.Contains($to)) {
    throw 'Safety stop: generated SQL does not refer to the expected migration.'
}

Write-Host ''
Write-Host 'Offline invoice migration SQL preview generated and basic checks passed.'
Write-Host "File: $output"
Write-Host 'Expected tables: 3. Expected unique indexes: 5.'
Write-Host 'NO database connection and NO SQL execution were performed.'
Write-Host 'Review and share this SQL file; do NOT run database update or DbMigrator.'
