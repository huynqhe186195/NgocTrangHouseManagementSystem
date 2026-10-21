PHASE 2E.2 - PostgreSQL DateTime UTC alignment

This bundle modifies 4 EF mapping files (6 time-of-event columns) and adjusts ONLY
ngoctrang_integration_test PostgreSQL columns. It does not edit the 247 SQLite tests.
The original PostgreSQL test module from the prior turn MUST keep:
    Configure<AbpClockOptions>(options => options.Kind = DateTimeKind.Utc);

IMPORTANT: The GitHub repository snapshot may differ from your uncommitted local code.
First use git apply --check and review git diff. If --check fails, do not use --reject or --3way:
inspect the actual file content before any manual edit.

1) Copy phase2e2-datetime-utc.patch to the solution root (or refer to its path).
   PowerShell (run from solution root):
   git apply --check .\phase2e2-datetime-utc.patch
   git apply .\phase2e2-datetime-utc.patch
   git diff -- modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/EntityConfigurations

2) Update ONLY the dedicated test database. $pgUser is the POSTGRES_USER
   environment value. Run from root in the PowerShell session with Docker:
   $pgUser = docker exec ngoctrang-postgres printenv POSTGRES_USER
   if ([string]::IsNullOrWhiteSpace($pgUser)) { $pgUser = 'postgres' }
   Get-Content -Raw -Encoding UTF8 .\update-test-db-timestamps.sql |
       docker exec -i ngoctrang-postgres psql -X -v ON_ERROR_STOP=1 -U $pgUser -d ngoctrang_integration_test
   if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL schema update failed' }

3) Run:
   dotnet test .\modules\buildingmanagement\test\BuildingManagement.PostgreSql.Tests\BuildingManagement.PostgreSql.Tests.csproj
   dotnet test

The SQL changes the test database only and is guarded by current_database().
It does not update production schema or create a migration. BEFORE any commit
or production release, also review migration and UTC clock configuration in
application hosts; otherwise model/database could diverge in production.

WARNING: The conversion assumes old timestamp-without-time-zone values represent
UTC instants. Do NOT apply this SQL on the real application database. Actual
app data may require a different timezone interpretation and an EF migration.
