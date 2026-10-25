# Phase 2G.3A.7 — SAFE LOCAL SCAFFOLD + SQL PREVIEW, NOT A DEPLOYMENT.
# Only generates local source files and an offline SQL text file.
# Never connects to PostgreSQL or runs 'database update' or DbMigrator.
# Design-time DbContext factory accepts BM_INVOICE_MIGRATION_OFFLINE=1
# and uses a deliberately unreachable local connection, Port=1.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repoRoot 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/BuildingManagement.EntityFrameworkCore.csproj'
$startup = Join-Path $repoRoot 'src/NgocTrangHouseManagementSystem.DbMigrator/NgocTrangHouseManagementSystem.DbMigrator.csproj'
$migrationDir = Join-Path $repoRoot 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations'
$reviewFile = Join-Path $repoRoot 'etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql'
$from = '20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots'
$migrationName = 'Phase2G3A7_AddInvoiceDraftRevisionLedger'

if (-not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath $startup)) {
    throw 'Safety stop: project/startup project not found.'
}
$branch = (& git -C $repoRoot branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: expected only branch wip/phase-2g2.'
}
$dirty = @(& git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or
    @($dirty | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
    throw 'Safety stop: commit/stash changes before generating a migration.'
}
if (Test-Path -LiteralPath $reviewFile) {
    throw 'Safety stop: SQL review file already exists; review it, do not overwrite.'
}
$exists = @(Get-ChildItem -LiteralPath $migrationDir -Filter ('*_' + $migrationName + '*') -File)
if ($exists.Count -ne 0) {
    throw 'Safety stop: migration of this name already exists.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI is not available.'
}

Write-Host 'SAFE MODE: only scaffold local EF migration files and generate OFFLINE SQL.'
Write-Host 'Connection refused by design-time DbContext factory (localhost Port=1).'
Write-Host 'NO database update, SQL execution or production connection.'
Write-Host ''

$previous = [Environment]::GetEnvironmentVariable('BM_INVOICE_MIGRATION_OFFLINE', 'Process')
[Environment]::SetEnvironmentVariable('BM_INVOICE_MIGRATION_OFFLINE', '1', 'Process')
Push-Location $repoRoot
try {
    & dotnet ef migrations add $migrationName `
        --project $project `
        --startup-project $startup `
        --context BuildingManagementDbContext `
        --output-dir Migrations
    if ($LASTEXITCODE -ne 0) {
        throw 'EF migration scaffolding failed; inspect any generated local files. No DB was changed.'
    }

    $generated = @(Get-ChildItem -LiteralPath $migrationDir -Filter ('*_' + $migrationName + '.cs') -File)
    if ($generated.Count -ne 1) {
        throw ('Safety stop: expected exactly one migration .cs; found ' + $generated.Count)
    }
    $migrationId = [IO.Path]::GetFileNameWithoutExtension($generated[0].Name)
    if ($migrationId -notmatch '^\d{14}_Phase2G3A7_AddInvoiceDraftRevisionLedger$') {
        throw ('Safety stop: unexpected migration ID ' + $migrationId)
    }

    # An empty model diff or any unrelated schema change must be reviewed,
    # not trusted because 'dotnet ef migrations add' itself succeeded.
    $source = [IO.File]::ReadAllText($generated[0].FullName)
    # A normal EF migration has a destructive Down() method. Audit
    # ONLY Up() here; otherwise every valid scaffold with DropTable
    # in its rollback method would be falsely rejected.
    $upIndex = $source.IndexOf('protected override void Up(',
        [StringComparison]::Ordinal)
    $downIndex = $source.IndexOf('protected override void Down(',
        [StringComparison]::Ordinal)
    if ($upIndex -lt 0 -or $downIndex -le $upIndex) {
        throw 'Safety stop: cannot isolate generated migration Up() method.'
    }
    $up = $source.Substring($upIndex, $downIndex - $upIndex)
    $newTable = 'BuildingManagementInvoiceDraftRevisions'
    if ($up -notmatch '(?s)migrationBuilder\.CreateTable\(\s*name:\s*"BuildingManagementInvoiceDraftRevisions"') {
        throw 'Safety stop: no expected revision ledger table creation found.'
    }
    $tableAdds = [regex]::Matches($up, 'migrationBuilder\.CreateTable\(').Count
    if ($tableAdds -ne 1) {
        throw ('Safety stop: expected one new table; found ' + $tableAdds)
    }
    if ($up -match '(?s)migrationBuilder\.(?:AlterColumn|AddColumn|DropColumn|DropTable|DropIndex|Rename|DeleteData|UpdateData|InsertData)\(') {
        throw 'Safety stop: migration contains changes beyond the one additive ledger table.'
    }
    if (-not $up.Contains('UX_BM_InvoiceDraftRevision_Invoice_Number')) {
        throw 'Safety stop: unique (InvoiceId, ArchivedRevision) missing.'
    }
    if (-not $up.Contains('BuildingManagementInvoices')) {
        throw 'Safety stop: parent invoice FK missing.'
    }

    & dotnet ef migrations script $from $migrationId `
        --project $project `
        --startup-project $startup `
        --context BuildingManagementDbContext `
        --output $reviewFile
    if ($LASTEXITCODE -ne 0) {
        throw 'Offline SQL generation failed. No SQL was executed.'
    }
    if (-not (Test-Path -LiteralPath $reviewFile)) {
        throw 'Expected review SQL file was not created.'
    }

    $sql = [IO.File]::ReadAllText($reviewFile)
    if (-not $sql.Contains('CREATE TABLE "' + $newTable + '"')) {
        throw 'Safety stop: SQL preview missing revision ledger table.'
    }
    if ([regex]::Matches($sql, '(?i)\bCREATE\s+TABLE\b').Count -ne 1) {
        throw 'Safety stop: preview contains more or fewer than one CREATE TABLE.'
    }
    if (-not $sql.Contains('CREATE UNIQUE INDEX "UX_BM_InvoiceDraftRevision_Invoice_Number"')) {
        throw 'Safety stop: SQL preview missing revision number uniqueness.'
    }
    if ($sql -match '(?i)\b(?:DROP\s+(?:TABLE|INDEX|COLUMN|DATABASE)|TRUNCATE|DELETE\s+FROM|UPDATE\s+"|ALTER\s+TABLE|CREATE\s+DATABASE)\b') {
        throw 'Safety stop: unexpected destructive or existing-schema operation in preview.'
    }
    if (-not $sql.Contains($migrationId)) {
        throw 'Safety stop: preview does not contain the new migration identity.'
    }
    foreach ($forbidden in @(
        'UX_BM_InvoiceSource_Global',
        'UX_BM_Invoice_Room_Period',
        'UX_BM_Invoice_Contract_Period')) {
        if ($sql.Contains('DROP INDEX "' + $forbidden + '"')) {
            throw ('Safety stop: cannot drop existing financial index: ' + $forbidden)
        }
    }

    Write-Host ''
    Write-Host 'OFFLINE MIGRATION SCAFFOLD + BASIC SQL REVIEW COMPLETED.'
    Write-Host ('Migration ID: ' + $migrationId)
    Write-Host ('Generated migration: ' + $generated[0].FullName)
    Write-Host ('Review SQL: ' + $reviewFile)
    Write-Host ''
    Write-Host 'NOT APPROVED TO APPLY: no PostgreSQL immutability trigger,'
    Write-Host 'revision concurrency transition, operator policy or rollback guard exists.'
    Write-Host 'Do NOT run dotnet ef database update, psql or DbMigrator.'
    Write-Host 'Inspect git diff and submit generated files for source review.'
}
finally {
    Pop-Location
    [Environment]::SetEnvironmentVariable('BM_INVOICE_MIGRATION_OFFLINE', $previous, 'Process')
}
