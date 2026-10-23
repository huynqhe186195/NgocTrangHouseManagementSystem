# Phase 2G.3A.4 — Offline invoice draft migration preparation

## Last validated build and business audit

- User report, 23/10/2026: **89/89 selected SQLite/EF/application
  regression tests PASS**, build succeeded with 4 existing Tenant
  Mapperly RMG012 warnings.
- Contract-period AND room-period uniqueness were tested in the real
  SQLite provider; globally unique meter source IDs were independently
  tested against two physically distinct rooms. Snapshot numbers
  survived source-reading soft deletion.
- The internal service is still not an HTTP endpoint, and no issued
  invoice, debt, allocation or collection logic has been enabled.

## What this change does

The repository now provides:
- An explicit offline mode for `BuildingManagementDbContextFactory`,
  selected ONLY when `BM_INVOICE_MIGRATION_OFFLINE=1`. It uses an
  unreachable local port rather than loading any real DB credentials.
  Design-time `migrations add` builds the EF model without connecting.
- A guarded PowerShell helper
  `etc/scripts/scaffold-invoice-draft-migration.ps1` that:
  1. Requires `wip/phase-2g2` and a clean working tree.
  2. Requires the existing EF Core CLI (`dotnet ef`) and does NOT install
     tools, start a database or run DbMigrator.
  3. Calls ONLY `dotnet ef migrations add` and
     `dotnet ef migrations has-pending-model-changes`.
  4. Clears its offline flag in a `finally` block.
  5. Inspects the generated `Up` operations and requires exactly
     THREE `CreateTable` operations (invoice/header, item, evidence)
     with four essential unique indexes and no destructive Up changes.
  6. Prints the migration source, Designer and model snapshot to review.

This prepares scaffolding; **the migration files do not exist in Git
until the local .NET SDK/EF CLI generates and commits them**. No
PostgreSQL migration is applied by this helper.

## Local steps (no database write)

From the repository root:

```powershell
git pull --ff-only origin wip/phase-2g2

# Ensure a clean git status before running.
git status --short

# This only creates migration files; no database schema update:
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\scaffold-invoice-draft-migration.ps1

# Show exactly what EF Core generated:
git status --short
git diff -- modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/BuildingManagementDbContextModelSnapshot.cs

# The migration .cs and Designer.cs are NEW, so inspect them directly too.
```

If the helper reports missing `dotnet ef`, stop and install/restore the
EF Core 10 CLI using your project's approved tool setup. Do not switch
to SQL manually.

## Review gates before commit/deployment

1. Up creates only:
   `BuildingManagementInvoices`,
   `BuildingManagementInvoiceItems`,
   `BuildingManagementInvoiceItemMeterSources`.
2. Verify that contract-period and room-period uniqueness both exist;
   item utility type uniqueness and global evidence-source uniqueness
   also exist. FKs must be restrictive, not cascading into financial
   history.
3. Compare generated metadata (ABP audited columns, types, nullability,
   precision/scale, indexes) with the current EF Core model.
4. Do not commit if the migration contains unrelated schema changes,
   especially `DropTable`, `AlterColumn`, raw SQL, or other model drift.
5. Only after code/schema review, rerun the EF Core test suite and
   separately validate PostgreSQL on an *isolated test database*
   following the project's `BM_PG_TEST_CONNECTION` convention.
6. Do NOT run `dotnet ef database update`, DbMigrator or SQL against
   real production data as part of generation/testing.
7. Keep issuance, debt recognition, deposits, payments and remote draft
   storage disabled until authorization, business policy and audited
   invoice lifecycle are implemented.

## Still open

- The script and offline design-time mode have **not been executed here**
  because the working environment has no .NET 10 SDK or PowerShell.
- Migration generation, actual output review and isolated PostgreSQL
  validation remain to be performed locally. SQLite PASS is not proof
  that the eventual migration applies cleanly on PostgreSQL.
- Meter/tariff modifications do not share the same locking discipline
  as draft creation: final issuance needs atomic revalidation and
  concurrency tests. Tenant/contract split policy and operator
  permissions remain unresolved.

**Do not mark Phase 2G.3A ready for production or the issuance phase.**
