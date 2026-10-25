# Phase 2G.3A.6 — Invoice draft source consistency gate (pre-implementation audit)

## Verified baseline (25/10/2026)

- Phase 2G.3A.4: 109/109 selected app/EF tests, PGINV01 1/1
  PostgreSQL DDL test, PGMIG01 1/1 full BuildingManagement migration
  chain test all reported PASS. Fresh scratch DB was deleted.
- Phase 2G.3A.5 gate A: InvoiceRemoteAccessGate_Tests 2/2 PASS.
  Candidate and draft storage application-service classes are both
  explicitly `[RemoteService(IsEnabled = false)]`.
- Builds show four existing Tenant Mapperly RMG012 warnings.
- **Not a production-release gate approval.**

## Verified regression and first implementation slice — 25/10/2026

The user reran the existing 109-case selected EF/application regression
after the invoice HTTP gate changed and reported **109/109 PASS**
(duration 27.6s, build succeeded with four previously known Mapperly
warnings). The invoice HTTP attribute tests separately passed **2/2**.
Those results predate the new transaction-isolation changes described
below; a new run is required.

**Partial source-snapshot consistency hardening (not yet locally tested):**
The root `InvoiceDraftStorageAppService.CreateAsync` transaction and
the independently callable, internal
`InvoiceDraftCandidateAppService.GetMonthlyCandidateAsync` transaction
now both declare:

```csharp
[UnitOfWork(true, IsolationLevel.RepeatableRead)]
```

On PostgreSQL, this requests one MVCC read snapshot for all SELECTs
within the same unit of work, preventing a draft's electricity and
water usage/rate statements from observing *different committed
versions within that single transaction*. The existing contract row
lock remains. SQLite's provider treats this isolation level as a
minimum and promotes it to serializable.

The new `InvoiceDraftIsolationGate_Tests` has two attribute-regression
cases; **no new tests have been run locally**. Both services remain
`[RemoteService(IsEnabled = false)]`.

**Limitation / no overclaim:** ABP's `UnitOfWork` attribute does not
override an already ambient UOW. A caller that starts a PostgreSQL
READ COMMITTED UOW can still bypass the new desired isolation.
We therefore cannot claim this phase's source consistency gate is
complete. A runtime isolation-level check and controlled PostgreSQL
writer-vs-capture concurrency tests are required before the internal
draft service is made available for financial use. A later source edit
can also invalidate a stored draft; issuance still needs atomic
revalidation/correction policy.

### Next local verification

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftIsolationGate_Tests|FullyQualifiedName~InvoiceRemoteAccessGate_Tests|FullyQualifiedName~InvoicePersistenceIntegration_Tests|FullyQualifiedName~InvoiceSchema_Tests"
```

Report exact pass/fail counts. No database migration or PostgreSQL
connection is needed for this SQLite/application regression.

## Compile blocker and corrective fix (25/10/2026)

Initial isolated gate implementation used the incorrect attribute syntax
`[UnitOfWork(IsTransactional = true, IsolationLevel = IsolationLevel.RepeatableRead)]`.
User's .NET 10 local build failed with **four CS0655 errors** in the
two invoice application services. ABP `UnitOfWorkAttribute` exposes
nullable `bool?`/`IsolationLevel?` properties, which cannot be used
as C# attribute named arguments. This was a source code error, **not
a PostgreSQL failure**.

Both methods now use the ABP-supported **positional constructor**
`[UnitOfWork(true, IsolationLevel.RepeatableRead)]`
(available in the ABP 10.x API). The two test cases inspect the
attribute's `IsTransactional` and `IsolationLevel` values and do not
need modification. This correction has not yet been compiled or tested
on the user's machine.

## Critical concurrency exposure — fail closed before financial issuance

Repository read on current WIP:

| Operation | Lock / consistency mechanism | Risk |
| --- | --- | --- |
| `InvoiceDraftStorageAppService.CreateAsync` | `IContractRepository.FindForUpdateAsync` locks contract row | Serializes draft against contract mutations using same row, but not utility rate/reading mutations |
| `MeterReadingAppService.CreateAsync/UpdateAsync` | `IUtilityMeterLock.AcquireAsync(meterId)` | Same physical meter mutations serialized, but not with invoice draft's contract row lock |
| `UtilityMeterAppService.ReplaceAsync` | Physical-meter lock | May change candidate's relevant source set during snapshot construction |
| `UtilityRateAppService.CreateAsync/UpdateAsync/DeleteAsync` | Overlap check and repository writes, no shared invoice lock | A tariff revision may race with the candidate's tariff read |
| `InvoiceDraftCandidateAppService.GetMonthlyCandidateAsync` | Transactional UOW, reads contract/utility previews | PostgreSQL READ COMMITTED can observe different committed state between SQL statements |
| `UtilityChargePreviewAppService.GetMonthlyChargePreviewAsync` | Sequential usage + floor/building + rate queries | Without shared lock or serializable validated snapshot, utility readings and tariff may not come from one atomic point in time |

An in-memory snapshot copies evidence and prices by value; this
makes it immutable **after creation** but does NOT prove it was created
from one concurrent-consistent historical state.

Existing database uniqueness prevents reusing a physical meter source
in multiple invoices. It does NOT independently guarantee the copied
amount matches a consistent pair of rate and reading versions when
other operations are concurrently committing.

## Next implementation gates (NOT complete)

1. Decide and codify draft-vs-issued source correction policy. A draft
   has no issue/cancel/delete transition yet; do not permanently block
   correcting a meter reading or tariff based solely on a draft without
   an approved correction path.
2. Map all write paths that can change a relevant source set, utility
   rate, room/building mapping, active contract, and meter replacement.
   Establish ONE documented lock order or serializable transaction/
   version-check scheme across draft capture and writers. Avoid new
   contract/meter inverted locks that can deadlock.
3. In PostgreSQL concurrency tests, synchronize competing requests to
   force a meter-reading update vs draft create, tariff update vs draft
   create, and meter replacement vs draft create. Assert that a draft
   is either built from a consistent version or is rejected; never
   allow a mixed-version amount/evidence graph.
4. Verify no existing permitted corrections become impossible;
   check repeat creation and globally unique meter-source constraints.
5. Keep issuance and receivables disabled; **revalidate all sources
   and current permissions at issuance time** even after draft
   concurrency fixes.
6. Add scoped operator permissions for cross-building read/create
   and host HTTP-route discovery tests before enabling remote
   invoice endpoints.
7. Run 109-case regression, new security tests and PostgreSQL
   concurrency tests after implementation; do not infer green from
   older 109/109 and 2/2 test results.

## No unsafe changes made in this audit

- No schema/migration changes or production DB writes.
- No locks or rate/meter mutation restrictions added without tests.
- No draft or issuance HTTP routes opened.

**Status: 2G.3A.5 internal-only remote access gate PASS; 2G.3A.6
source consistency investigation OPEN.**
