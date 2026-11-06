# Fail closed when EF migration scaffolding includes unrelated historical schema drift.
# This script NEVER connects to or changes a database.
$ErrorActionPreference = 'Stop'
$dir = 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations'
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
if ($created.Count -ne 2) {
    throw "Unexpected CreateTable count: $($created.Count). Old snapshot may be out of sync; manual migration review required."
}
# No ALTER/DROP of existing production objects is allowed in this narrow preview.
$forbidden = @('DropTable','DropColumn','AlterColumn','RenameTable','RenameColumn','DeleteData','UpdateData','Sql')
foreach ($action in $forbidden) {
    if ($up -match ('migrationBuilder\.' + $action + '\(')) {
        throw "Unsafe migration operation detected: $action."
    }
}
Write-Host 'PAYOS EF MIGRATION PREVIEW: two new tables present; no dangerous Up() operations detected.'
Write-Host 'This is a generated preview only, not an applied migration or PostgreSQL acceptance.'
