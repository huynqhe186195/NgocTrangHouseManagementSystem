# Phase 2G.3A.1 — Contract-bound invoice draft candidate (WIP)

This increment deliberately does **not** create a financial obligation.
No database table, EF migration, invoice issuance, deposit mutation or
payment allocation is introduced by this increment.

## Scope and safety decisions

- Preview is requested by `ContractId` and billing year/month, not just
  `RoomId`. It resolves the contract's own room and uses existing
  meter/rate previews for electricity and water.
- A candidate may quote a provisional total only when the contract is
  currently Active **and** its dates cover the entire calendar billing
  month. A tenant arriving or departing mid-period receives no price
  based on the prior or next tenant's unallocated full-month consumption.
- A missing rate, in-month rate change, missing meter read, stale cutoff
  or history review produces no overall provisional total; missing amounts
  remain null rather than being converted to zero.
- Snapshots defensively copy contract/room identity, rate identifier,
  unit price, reading source identifiers, counter ranges and computed
  recorded units into immutable in-memory objects.
- Reject duplicate utility lines, repeat source references, negative or
  inconsistent reading deltas, and estimated amounts inconsistent with
  source units times unit price.
- `CanPersistDraft == false`, `CanIssue == false`.
  These are not persisted invoice snapshots. A read-only preview can go
  stale immediately; the future invoice issuance transaction must redo
  validation under locks and persist approved immutable records.

## Tests and verification

- Prior Phase 2G.2 regression runs, user-reported on 23/10/2026:
  67/67 EF Core app tests and 2/2 coverage domain tests passed.
  Earlier 7/7 final meter domain tests and 6/6 PostgreSQL PGUTIL00-05 passed.
- Phase 2G.3A.1 added eight independent
  `InvoiceDraftSnapshot_Tests` and five EF Core service tests
  `ID01-ID05`: contract source attribution, partial occupancy,
  draft status, unknown contract and one missing utility price.
- Confirmed by user on 23/10/2026: 72/72 Application/EF Core tests and
  8/8 invoice draft snapshot Domain tests passed.

## Still blocking actual draft persistence and invoice issuance

1. Confirm contract allocation policy across move-in, move-out, renewal,
   overlapping/transitioning tenants, and time-of-day cutoffs.
2. Confirm tariff choice: flat vs tiers, split when changing mid-month,
   included/shared meters, service fees and rounding to currency units.
3. Design `Invoice`/`InvoiceItem` EF tables and a real immutable
   line snapshot, unique contract-and-period constraint, invoice version,
   soft-deletion policy and audit trail.
4. Obtain FinalMeterReading handover approval, permission checks, and
   actual consumption/cutoff readiness verification.
5. Implement issue/finalize under a consistent transaction/lock order
   with snapshot persistence; handle adjustments after issuance rather
   than editing historical debt.
6. Do not treat any provisional quote as payment due, a debtor balance,
   or a completed move-out settlement.

Do not merge this increment into `main` or run database migrations as
part of these in-memory-only tests.
