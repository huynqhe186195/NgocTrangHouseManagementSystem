# Phase 2G.3A.2 — Draft invoice persistence schema audit (WIP)

## Boundary

This increment defines a **DRAFT-only EF Core data model**; it does NOT
add any API or background job that inserts, issues, posts, deletes or pays
an invoice. Database migration has NOT been generated or applied.
No real/production database must be updated at this stage.

Prior 2G.3A.1 preview tests passed on the user's workstation:
72/72 Application/EF Core and 8/8 snapshot Domain tests.

## Draft data schema

- `BuildingManagementInvoices`: contract, room, year, month, Draft status,
  nullable provisional utility total and ABP audit metadata.
- `BuildingManagementInvoiceItems`: one utility type per draft, copied
  rate ID, rate status, recorded quantity, nullable unit price, nullable
  provisional amount and JSON-serialized coverage warnings.
- `BuildingManagementInvoiceItemMeterSources`: every reading/replacement
  closing contributing to a line, with historical source ID, meter ID,
  physical reading date, previous and current indices. Values remain
  available even if an original reading is subsequently hidden by
  soft-delete; this table does not reuse the original reading as mutable
  invoice quantities.
- Draft factory constructs header, lines and source references together
  from validated `InvoiceDraftSnapshot` and refuses duplicate/empty IDs.
  No public Issue/Post/Pay mutation exists.

## DB constraints

- One invoice per `(ContractId, BillingYear, BillingMonth)` with a
  **nonfiltered** unique index. No draft soft delete that could recycle
  the same period key.
- One item per `(InvoiceId, UtilityType)`.
- Each `(SourceType, SourceId)` is unique **globally** across draft line
  meter sources, preventing a physical reading/handover from appearing
  on two draft invoices. The eventual adjustment flow must be separate.
- Restrictive FKs: invoice -> contract and room;
  item -> invoice and optional utility rate;
  source -> item and physical utility meter.
- Numeric fields use 18,3 for counter / quantity and preliminary amounts,
  and 18,0 for the current flat utility unit price.
- Rate ID is not the authority for historical invoicing; immutable
  unit-price and source counters are copied onto snapshot rows.

## Checks and test status

- **User verified:** on 23/10/2026, all 7/7 invoice persistence
  domain tests and 75/75 schema + application tests passed.
- **New follow-up checks, NOT YET RUN:** four actual SQLite persistence
  tests `INVDB01-INVDB04` save/reload header, items and meter counter
  evidence, enforce duplicate contract-period and global source-ID
  uniqueness at the database, and prove modifying a UtilityRate does
  not reprice a previously saved draft.
- **New safety regression, NOT YET RUN:** `ID06` blocks quoting full
  room-month charges when two Active contracts overlap the room-period.
  Even an unexpected manually imported contract must not reassign an
  entire month's utilities to multiple tenants.

- Seven domain cases in `InvoiceDraftPersistence_Tests` for draft-only
  status, contract identity, preserved price/source, null instead of
  zero for missing tariff, rejection of duplicate IDs and immutable
  outward collections.
- Three EF metadata cases in `InvoiceSchema_Tests` for header period
  uniqueness, invoice-line/source indexes, and restrictive delete rules.
- EF Core model metadata was tested successfully, but the new roundtrip
  SQLite persistence tests are still awaiting execution.

## Build verification and fixes

- User-reported attempt on 23/10/2026: both commands stopped during
  compilation due to CS1503 at InvoiceDraftPersistence_Tests.cs:80,
  which invoked Guid.NewGuid() instead of passing the Guid.NewGuid
  delegate required by Invoice.CreateDraft.
- Corrected the test, changed the nullable provisional-amount comparison
  to definite-assignment pattern matching (CS8629), and removed one
  duplicated EF Core using directive (CS0105).
- These corrections have not yet been rerun on the user's workstation.
  Four Mapperly RMG012 warnings from Tenant to TenantDto mapping remain;
  avoid altering audit DTO fields without a separate API compatibility review.

## Remaining blockers

1. **NO persisted draft workflow yet** — an operator-authorized write
   command must be designed with a shared contract lock and validation
   against fresh meter/rate sources immediately before commit.
2. **NO migration yet** — run EF Core migration generation in the regular
   DbMigrator startup project *only after tests are green*, review SQL
   and snapshot changes, then deploy through approved migration
   procedure. Do not manually issue psql commands or reset databases.
3. **NO issuance or debt recognition** — still blocked by rate-cutoff
   policy, contract split periods, meter evidence sign-off, rent/fees,
   rounding, duplicate payment allocation and audit of adjustments.
4. The global uniqueness of source references is intentionally strict;
   corrections, canceled drafts and manual adjustments need an explicit
   audited lifecycle rather than deleting and reusing historical sources.
5. **Permissions** — no new read/write API for draft persistence is
   exposed yet; existing preview routes need an authorization plan
   before tenant/public deployment.

## Test commands (repo root)

```powershell
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftPersistence_Tests"

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoiceSchema_Tests"

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoicePersistenceIntegration_Tests"

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~UtilityMeterAppService_Tests"
```

The two newly added groups should report 7 and 3 tests respectively;
existing application tests should now run 73/73 (ID06 added). The results are not
assumed until the user runs them.
