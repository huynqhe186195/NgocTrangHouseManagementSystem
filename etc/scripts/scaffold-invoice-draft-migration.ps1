# Generates EF Core migration source files only (NO database connection,
# NO database update, NO DbMigrator execution, NO SQL execution).
# Use from the repository's wip/phase-2g2 branch after a clean pull.
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$moduleDir = Join-Path $repoRoot 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore'
$migrationsDir = Join-Path $moduleDir 'Migrations'
$moduleProject = Join-Path $moduleDir 'BuildingManagement.EntityFrameworkCore.csproj'
$startupProject = Join-Path $repoRoot 'src/NgocTrangHouseManagementSystem.DbMigrator/NgocTrangHouseManagementSystem.DbMigrator.csproj'
$migrationName = 'Phase2G3A4_AddInvoiceDraftSnapshots'

$branch = (& git -C $repoRoot branch --show-current)
if ($LASTEXITCODE -ne 0 -or ($branch | Out-String).Trim() -ne 'wip/phase-2g2') {
    throw 'Aborted: migration scaffolding is allowed only on wip/phase-2g2.'
}

$gitStatus = @(& git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw 'Aborted: cannot inspect git status.'
}
if (@($gitStatus | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw 'Aborted: commit/stash unrelated changes first. Migration scaffolding requires a clean worktree.'
}

if (@(Get-ChildItem -LiteralPath $migrationsDir -Filter "*_$migrationName*").Count -gt 0) {
    throw 'Aborted: this migration already exists. Review the existing migration instead.'
}

$efVersionOutput = @(& dotnet ef --version)
if ($LASTEXITCODE -ne 0) {
    throw 'The dotnet-ef tool is unavailable. Install/restore the compatible EF Core 10 CLI before retrying.'
}
$efVersionText = ($efVersionOutput -join ' ')
if ($efVersionText -notmatch '(?:^|\s)10\.\d+(?:\.\d+)?(?:\s|$)') {
    throw "Expected dotnet-ef version 10.x, got: $efVersionText"
}
Write-Host $efVersionText

# The design-time DbContext factory sees only a non-routable dummy
# PostgreSQL endpoint. The EF migrations add command does not apply schema.
$priorOffline = [Environment]::GetEnvironmentVariable(
    'BM_INVOICE_MIGRATION_OFFLINE', 'Process')
[Environment]::SetEnvironmentVariable(
    'BM_INVOICE_MIGRATION_OFFLINE', '1', 'Process')
try {
    & dotnet ef migrations add $migrationName `
        --project $moduleProject `
        --startup-project $startupProject `
        --context BuildingManagementDbContext `
        --output-dir Migrations
    if ($LASTEXITCODE -ne 0) {
        throw 'EF Core migration scaffolding failed. Database was NOT updated.'
    }

    & dotnet ef migrations has-pending-model-changes `
        --project $moduleProject `
        --startup-project $startupProject `
        --context BuildingManagementDbContext
    if ($LASTEXITCODE -ne 0) {
        throw 'Generated snapshot does not match the current model. Inspect the migration before committing.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        'BM_INVOICE_MIGRATION_OFFLINE', $priorOffline, 'Process')
}

$files = @(Get-ChildItem -LiteralPath $migrationsDir -Filter "*_$migrationName.cs")
if ($files.Count -ne 1) {
    throw 'Expected exactly one generated migration source file.'
}
$file = $files[0]
$designer = $file.FullName -replace '\.cs$', '.Designer.cs'
$snapshot = Join-Path $migrationsDir 'BuildingManagementDbContextModelSnapshot.cs'
if (-not (Test-Path -LiteralPath $designer) -or
    -not (Test-Path -LiteralPath $snapshot)) {
    throw 'Missing Designer or model snapshot; inspect generated files.'
}

$source = [IO.File]::ReadAllText($file.FullName)
$up = ([regex]::Split($source, 'protected\s+override\s+void\s+Down'))[0]
$tables = @(
    'BuildingManagementInvoices'
    'BuildingManagementInvoiceItems'
    'BuildingManagementInvoiceItemMeterSources'
)
foreach ($table in $tables) {
    if (-not $up.Contains('"' + $table + '"')) {
        throw "Expected new table $table is not in the generated Up migration."
    }
}
$createCount = [regex]::Matches($up, 'migrationBuilder\.CreateTable\(').Count
if ($createCount -ne 3) {
    throw "Expected only 3 CreateTable operations, found $createCount. Review for model drift."
}
if ($up -match 'migrationBuilder\.(?:DropTable|DropColumn|AlterColumn|Sql)\(') {
    throw 'Unexpected destructive or hand-written Up migration operation. Review before committing.'
}
foreach ($index in @(
    'UX_BM_Invoice_Contract_Period'
    'UX_BM_Invoice_Room_Period'
    'UX_BM_InvoiceItem_Invoice_Utility'
    'UX_BM_InvoiceSource_Global'
)) {
    if (-not $up.Contains('"' + $index + '"')) {
        throw "Expected uniqueness constraint $index is not in the migration."
    }
}

$generatedFiles = @($file.FullName, $designer, $snapshot)
Write-Host ''
Write-Host 'Generated 3 EF Core migration artifacts (NO database updates):'
$generatedFiles | ForEach-Object { Write-Host " - $_" }
Write-Host ''
Write-Host 'Review git diff (including the complete new migration) before committing.'
Write-Host 'Do NOT run dotnet ef database update or DbMigrator against production.'
