# Fail closed when EF migration scaffolding includes unrelated historical schema drift.
# This script NEVER connects to or changes a database.
$ErrorActionPreference = 'Stop'
$dir = 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/PayOs'
$migrations = @(Get-ChildItem "$dir/*StagePayOsPaymentIntentsAndWebhookInbox.cs" -ErrorAction Stop | Where-Object { $_.Name -notlike '*.Designer.cs' })
if ($migrations.Count -ne 1) { throw "Expected exactly one generated migration file, found $($migrations.Count)." }
$source = Get-Content -Raw $migrations[0].FullName
$upMatch = [regex]::Match($source, '(?s)protected override void Up\(MigrationBuilder migrationBuilder\)(.*?)protected override void Down\(MigrationBuilder migrationBuilder\)')
if (-not $upMatch.Success) { throw 'Migration Up/Down methods not located.' }
$up = $upMatch.Groups[1].Value
$expected = @('BuildingManagementPayOsPaymentIntents','BuildingManagementPayOsWebhookInbox')
foreach ($table in $expected) {
    if ($up -notmatch [regex]::Escape('name: "' + $table + '"')) {
        throw "Expected table $table is missing from migration Up()."
    }
}
$created = @([regex]::Matches($up, 'migrationBuilder\.CreateTable\('))
$tableNames = @([regex]::Matches($up, 'migrationBuilder\.CreateTable\(\s*name:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$unexpectedTables = @($tableNames | Where-Object { $_ -notin $expected } | Sort-Object -Unique)
Write-Host "PAYOS EF SCAFFOLD DIAGNOSTIC: CreateTable total=$($created.Count); named tables=$($tableNames.Count); unrelated tables=$($unexpectedTables.Count)"
foreach ($table in $unexpectedTables) { Write-Host "UNRELATED_CREATE_TABLE: $table" }
$operations = [regex]::Matches($up, 'migrationBuilder\.([A-Za-z]+)\(') | ForEach-Object { $_.Groups[1].Value } | Group-Object | Sort-Object Name
foreach ($operation in $operations) { Write-Host "EF_UP_OPERATION: $($operation.Name)=$($operation.Count)" }
if ($created.Count -ne 2 -or $unexpectedTables.Count -ne 0) {
    throw "Blocking migration snapshot drift: expected two payOS tables, found $($created.Count) CreateTable calls and $($unexpectedTables.Count) unrelated tables. Review the emitted list and artifact before any EF migration."
}
# No ALTER/DROP of existing production objects is allowed in this narrow preview.
$forbidden = @('DropTable','DropColumn','AlterColumn','RenameTable','RenameColumn','DeleteData','UpdateData','Sql')
foreach ($action in $forbidden) {
    if ($up -match ('migrationBuilder\.' + $action + '\(')) {
        throw "Unsafe migration operation detected: $action."
    }
}
if ($up -notmatch 'ForeignKey\(' -or $up -notmatch 'BuildingId' -or $up -notmatch 'OrderCode') { throw 'Composite building/order scope foreign key missing from Up().' }
if ($up -notmatch 'CreateIndex\(' -or $up -notmatch 'unique: true') { throw 'PayOS unique provider/order indexes missing from Up().' }
Write-Host 'PAYOS EF MIGRATION PREVIEW: isolated payOS migration contains two tables, composite FK and unique indexes; no unrelated operations.'
Write-Host 'This is a generated preview only, not an applied migration or PostgreSQL acceptance.'
