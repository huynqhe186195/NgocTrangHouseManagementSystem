# Phase 2G.3A.5 — Invoice candidate HTTP exposure gate

## Starting baseline

Phase 2G.3A.4 successfully completed: selected application/EF tests
`109/109` PASS, PostgreSQL invoice DDL PGINV01 `1/1` PASS,
and the full `BuildingManagementDbContext` migration chain PGMIG01
`1/1` PASS on a disposable local PostgreSQL database (user reported
on 25/10/2026). The database was removed following the test.
None of this applied migrations to production.

## Gap discovered by auditing adjacent business services

`InvoiceDraftStorageAppService` was already explicitly
`[RemoteService(IsEnabled = false)]`, but
`InvoiceDraftCandidateAppService` (a class inheriting ABP
`ApplicationService`) had no equivalent conventional HTTP gate.
The candidate includes contract and room IDs, monthly consumption,
meter source details and tariff/amount information. The module's
`BuildingManagementPermissionDefinitionProvider` does not yet define
a dedicated invoice read permission or room/operator data scope.

Under ABP conventional controller generation, this mismatch could
inadvertently publish candidate information remotely while invoice
storage remains internal. An internal read-only method is still a
sensitive-data access path.

## WIP hardening (not yet locally tested)

- Marked the candidate application service itself
  `[RemoteService(IsEnabled = false)]`, matching invoice storage.
- Added xUnit `InvoiceRemoteAccessGate_Tests` with two cases:
  `InvoiceDraftCandidateAppService` and
  `InvoiceDraftStorageAppService` must both have an explicit disabled
  RemoteService attribute. Does not require PostgreSQL or schema writes.
- Internal dependency-injection calls and all financial policies remain
  unchanged. No HTTP endpoint was opened.

### Verify locally

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~InvoiceRemoteAccessGate_Tests"
git status --short
```

**Expected: 2/2 PASS (not yet executed).** Follow with the previously
selected 109-case regression suite for unchanged behavior.

## Still blocked

Do **not** expose invoice candidate or storage via HTTP solely by
removing `[RemoteService]`. Before an operator-facing API is enabled:

1. Define dedicated permissions for draft preview/read/create and an
   explicit building/room scope based on authenticated operator.
2. Test cross-building access denial even if a caller knows a contract,
   room or invoice GUID; do not trust IDs in a client request.
3. Confirm ABP controller conventions and proxy metadata reflect the
   disabled routes in a host integration test.
4. Synchronize mutating meter readings/replacements/tariffs with invoice
   draft generation or introduce fail-closed issuance revalidation.
5. Validate populated database migration upgrade and host DbContext
   migration chain separately.

No issuance, debt, payments or production schema update in this phase.
