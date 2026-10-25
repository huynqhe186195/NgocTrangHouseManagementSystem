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
