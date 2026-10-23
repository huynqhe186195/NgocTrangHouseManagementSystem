# Phase 2G.3A.4 — Reviewed SQL and rollback-only PostgreSQL smoke test

## Verified locally by user (23/10/2026)

- `109/109` selected EF Core / Application tests **PASS**; .NET 10 build
  succeeded with four existing Tenant Mapperly RMG012 warnings.
- EF Core CLI `10.0.12` generated
  `20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots`.
- The user committed the generated 72-line offline SQL preview
  (`d6a058b`); its scaffolding guard passed.

## Manual audit of the exact SQL

The reviewed `Phase2G3A4_InvoiceDraftSchema_Review.sql` contains:

1. A single `START TRANSACTION` / `COMMIT` transaction.
2. Three `CREATE TABLE` statements for:
   - `BuildingManagementInvoices`;
   - `BuildingManagementInvoiceItems`;
   - `BuildingManagementInvoiceItemMeterSources`.
3. Six `ON DELETE RESTRICT` foreign keys:
   - Invoice -> Contract, Room;
   - Item -> Invoice, UtilityRate;
   - MeterSource -> Item, UtilityMeter.
4. Five unique indexes:
   - `UX_BM_Invoice_Contract_Period`;
   - `UX_BM_Invoice_Room_Period`;
   - `UX_BM_InvoiceItem_Invoice_Utility`;
   - `UX_BM_InvoiceSource_Global`;
   - `UX_BM_InvoiceSource_Item_Type_Source`.
5. Two supporting nonunique indexes on source meter and utility rate.
6. One `INSERT INTO "__EFMigrationsHistory"`, setting migration
   `20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots`.

The migration does not alter, drop or delete pre-existing business tables.
Precision is `numeric(18,3)` for quantities / amounts, `numeric(18,0)`
for flat unit price, with `date` for source reading date and
`timestamp with time zone` for ABP audit fields.

**SQL structure review: PASS. Actual PostgreSQL execution: NOT YET VERIFIED.**

## Critical test-harness distinction

Existing `BuildingManagement.PostgreSql.Tests` uses `EnsureCreated()`.
That mode does not upgrade its schema through EF migration history, and
may omit `__EFMigrationsHistory`; new invoice tables might also already
exist if the test DB was created with the latest model.

Do **not** run the committed SQL file or `dotnet ef database update`
against `ngoctrang_integration_test` directly. In particular, a naive
direct replay may fail on `__EFMigrationsHistory` or existing tables.

## New isolated SQL rehearsal — PGINV01

`InvoiceMigrationSqlPostgreSql_Tests.PGINV01_Reviewed_Invoice_Migration_Ddl_Executes_And_Rolls_Back`
is a standalone xUnit test, **not** derived from the ABP PostgreSQL
test fixture. It therefore does not call `EnsureCreated()`.

- Reuses the existing `RequireSafeConnectionString` check:
  localhost or 127.0.0.1, port 5432, database precisely
  `ngoctrang_integration_test`.
- Embeds the **exact reviewed 72-line SQL file** as a test resource.
- Parses and permits ONLY the three expected CREATE TABLE and seven
  expected CREATE INDEX statements. It explicitly skips transaction
  wrappers and migration-history insertion.
- Starts a PostgreSQL transaction and creates a unique schema
  `bm_invoice_smoke_<guid>`, with `SET LOCAL search_path` so new
  invoice tables land ONLY in the temporary schema. Existing FK target
  tables resolve from `public`.
- Executes the DDL and checks three tables, five unique indexes,
  and six `ON DELETE RESTRICT` FKs.
- Explicitly **ROLLBACKS**, then verifies the temporary schema
  does not remain. If execution fails, disposing the uncommitted
  transaction rolls back the schema automatically.
- Does not insert/update business records or migration history.
  Transaction-local DDL temporarily executes in the dedicated local
  test database, but leaves no persistent schema changes on success
  or failure.

### Run from repo root (only if your dedicated LOCAL test DB exists)

```powershell
git pull --ff-only origin wip/phase-2g2

# Never print or share the connection string, passwords or secrets.
# BM_PG_TEST_CONNECTION must already target the permitted local
# ngoctrang_integration_test database.

dotnet test .\modules\buildingmanagement\test\BuildingManagement.PostgreSql.Tests\BuildingManagement.PostgreSql.Tests.csproj --filter "FullyQualifiedName~InvoiceMigrationSqlPostgreSql_Tests"
```

**Expected: 1/1 PASS (UNVERIFIED).**

## Limits and next gate

This test proves the reviewed SQL creates a PostgreSQL *invoice schema*
against prerequisite tables; it does NOT exercise the complete historical
migration chain, EF history tracking, user permissions, production
deployments or actual issuance. Those remain separate gates.

A future end-to-end EF migrations rehearsal should use a new,
explicitly disposable test database with the complete migration chain;
never confuse an `EnsureCreated` test database with a migrations-managed
database. No production database updates or issued invoices are authorized.
