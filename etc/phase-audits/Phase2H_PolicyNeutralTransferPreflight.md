# Phase 2H — policy-neutral transfer preflight (development only)

Branch: `wip/phase-2g2`. This is a **non-mutating** domain assessment and tests only; it does not implement a transfer API, contract mutation, deposit carry-forward, room release, migration, or production deployment.

## Grounding

Aligned to the 22 October 2026 Phase 2G–2H Business Process Specification, sections 8, 9.2, 10.1 and open item OI-07. The gate checks source Active/Occupied, a distinct target in the same building, target Available, reservations/contract commitments, tenant roster, final meters, signed source inspection, reconciled debt, and exclusive deposit disposition.

For safety, a destination with a PendingPayment reservation is treated as unresolved, even though PendingPayment is not a confirmed occupancy commitment for Phase 2G.5. Reusing the 2G.5 'room becomes Available' rule for Room Change would be incorrect.

## Deliberately not inferred

- Whether to end the original contract/create a new contract or use a versioned addendum.
- Mid-month effective date, new rent and utility cutoff/proration calculation.
- Who moves when several tenants share a room.
- Additional deposit, approved difference refunds, dispute handling and carry-forward authorization.
- Whether source handover may use a reduced checklist.

These are **policy gates**, not Boolean defaults to silently mark true in an HTTP request. The eventual authorized orchestrator must derive every flag from verified database evidence under SERIALIZABLE transaction/lock ordering and repeat validation at commit. The preflight result does **not** authorize any financial or room mutation. Do not use client-provided flags to authorize Room Change.

## Acceptance

New TRF01–TRF12 domain tests check room status, competing reservations (including PendingPayment), signed/active contracts, same-building scope, proof, debt, deposit exclusivity and missing decisions. Two new CMOUT62–CMOUT63 tests harden existing Phase 2G.5: future reservation/contract facts with unsupported states can no longer be silently ignored.

Full Phase 2H remains HOLD until OI-07 is explicitly decided and a PostgreSQL-backed, authenticated atomic transfer API, immutable carry-forward ledger, rollback and concurrency integration tests are implemented. This commit changes no production data and does not claim Phase 2H is complete.
