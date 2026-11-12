# Phase 2G.3A.3 — Internal draft storage safety audit

## Verified baseline

On 23/10/2026, the user reported **80/80 passing** for the
EF Core SQLite suite covering the previously implemented invoice schema,
invoice persistence roundtrip, tariff snapshot consistency and 73
utility/candidate application cases. Build succeeded with four existing
Mapperly RMG012 warnings; they are not resolved by this increment.

## Verified regression checkpoint

The user confirmed **87/87 PASS**, build successful with four
Mapperly RMG012 warnings, on 23/10/2026. Subsequent room-period uniqueness
and immutable reading-source audit checks were also verified: **89/89 PASS**.

## New implementation (verified before latest audit repairs)

- Added an **internal-only** `InvoiceDraftStorageAppService` with
  `[RemoteService(IsEnabled = false)]`. No public draft-write HTTP endpoint
  or permission is enabled, and no migration or production DB change
  has been performed.
- `CreateAsync` only accepts ContractId, BillingYear and BillingMonth.
  The caller cannot submit room, meter indexes, unit prices, meter source
  IDs or invoice amount.
- Acquires the existing Contract row lock first in a transactional unit
  of work. Checks a duplicate contract-period invoice before processing.
- Recalculates an entire server-side candidate from recorded readings and
  building tariffs. Requires active contract, whole calendar-month
  coverage, no competing active/ended-in-month contract, complete
  meter readings, positive single rate throughout the month,
  both electricity and water lines with physical source evidence.
  Missing/ambiguous quantities or amounts are rejected, not coerced to
  zero.
- Rebuilds an immutable `InvoiceDraftSnapshot` and stores its Invoice,
  InvoiceItem and InvoiceItemMeterSource graph in a single unit of work.
  DB uniqueness protects duplicate contract-period keys and reused
  meter evidence even when two commands race.
- `GetAsync` reconstructs a stored draft from header, line and source
  tables without consulting mutable present-day tariffs to reprice
  historical items.
- No issued status, invoice delete/edit command, payment allocation,
  debt ledger posting, contract termination, deposit settlement or
  tenant charge notification is implemented.

## Regression cases (verified at the 87-test checkpoint)

Seven additional application tests ID07-ID13 cover:
1. Snapshot persistence and retrieval with the exact billable source IDs
2. Duplicate contract and month rejection
3. Missing water utility / tariff / readings
4. Contract starting partway through the billing month
5. Nonexistent contract
6. Two overlapping active contracts in one room
7. An ended previous contract overlapping the current billing month

This grew the previous filter from 80 to **87/87 PASS** (7 new app cases).
No new standalone domain or EF metadata test groups were added.

## Additional business loopholes found after 87/87 PASS

- **Duplicate room-month across contract identities:** one contract-month
  unique index cannot stop two separate contract IDs receiving the same
  room's full-month consumption. While the business rule forbids partial
  months, enforce UNIQUE (RoomId, BillingYear, BillingMonth) as well.
  INVDB05 directly inserts two different contract drafts with separate
  physical source identifiers, verifying the room-level index. A future
  split-tenancy design requires a new allocation model and reviewed
  migration rather than bypassing this index.
- **Independent source uniqueness test:** INVDB03 was adjusted to
  attempt reusing a physical reading in a different room. That ensures
  the global source uniqueness test is not accidentally passing because
  of the new room-month index instead.
- **Snapshot survival after source soft deletion:** INVDB06 inserts a
  historical meter reading, snapshots its counters into a draft, then
  soft-deletes the source reading. The draft must still retain source ID,
  prior/current counters and original amounts. This is a test, not a
  public license to delete invoiced readings.
- **Migration gate remains closed:** new indexes are in the EF model
  used by isolated SQLite tests, but no PostgreSQL migration has been
  generated or run in production.

**Verified by the user on 23/10/2026:** the additional INVDB05 and
INVDB06 tests passed. The selected regression suite reached **89/89 PASS**,
with build success and four Mapperly RMG012 warnings.

## Remaining release blockers

1. **Authorization:** remote write is DISABLED pending operator-only
   permission design and full API access control. An internal caller
   is not itself permission-protected; it must not be exposed until a
   security policy is implemented and tested.
2. **Migration:** application test SQLite creates its isolated schema,
   but PostgreSQL has not had this invoice schema migrated. Production
   calls would not work until an reviewed regular EF migration is applied.
   Do not run manual psql or hand-edit real databases.
3. **Concurrency:** contract row lock serializes draft creation relative
   to other contract-mutating workflows. Meter and tariff mutation flows
   do NOT necessarily lock that same row, so the snapshot can become
   stale during an in-flight draft transaction. The result remains
   Draft-only; issuance needs revalidation under a consistent lock
   order and a PostgreSQL race test.
4. **Source validation:** source identities are built from server-side
   reading/replacement queries, not user DTOs. A final issuance flow
   must also cross-check source room/type with the locked contract and
   enforce approved cutoff/hand-over rules.
5. **Business policy:** rent/service fee inclusion, meter sharing,
   month-end cutoff, partial occupancy, invoice rounding and tenant
   transfer remain unapproved. Thus even stored utility-only drafts
   are NOT legally or financially payable invoices.
6. **Amendments:** the unique period/source indexes preclude casually
   deleting/recreating a draft. Corrections require a specific
   audited version/adjustment process before public rollout.
7. **Soft-delete/data access:** future read/write authorization must
   scope invoice and contract records by building/operator and ensure
   no unauthorized customer can browse another room's financial data.

The gate to public 2G.3B issuance remains CLOSED.
