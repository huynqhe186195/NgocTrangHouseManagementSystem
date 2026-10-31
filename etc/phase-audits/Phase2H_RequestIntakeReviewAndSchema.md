# Phase 2H request intake — isolated staging only (2026-10-31)

This change follows the supplied Phase 2G–2H specification. Phase 2G.5 remains the only way to close an occupied tenancy. It does not turn Phase 2H into a completed transfer.

## Delivered

- Persisted **RoomChangeRequest** entity with explicit actor/time audit and sealed source contract, source/target rooms, building, requested date and reason.
- Request states **Pending → PolicyHold → Rejected** or **Pending/PolicyHold → Cancelled** only. NO Approved, Completed, Transfer or deposit carry-forward status.
- PostgreSQL FK, source/target same-building / occupied/available trigger, collision checks against existing active/signed target contracts and even unpaid PendingPayment reservations, move-out conflict, one-open-request-per-contract unique partial index, immutable identity/history and irreversible terminal reviews.
- Isolated staging API: read/list, submit, policy hold, reject, cancel; all rights are server-owned with active account ABP Contracts.Manage + live membership lease; no arbitrary building or tenant ID authority; two named scratch databases and explicit staging flag required. Room locks precede request insertion. No client-supplied approval flags.
- Domain / HTTP / disposable PostgreSQL tests. No production operations, no contract/room/meter or cash mutations.

## Explicit HOLD

OI-07 contract treatment, pricing mid-month, which tenants move, deposit difference/offset/carry forward and source handover scope. There is no executed Room Change until an authorized serializable orchestrator, immutable deposit carry-forward ledger, hard rollback/concurrency controls and end-to-end tenant/building authorization tests are implemented.

The migration is intended only for an explicitly disposable PostgreSQL rehearsal and rejects rollback when audit history exists. Do not apply it to live databases.
