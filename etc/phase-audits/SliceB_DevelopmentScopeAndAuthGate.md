# Slice B — development authorization gate and phased scope

**Branch:** `wip/phase-2g2` · **Not a production deployment plan**

## Current threat and safety decision

The existing five application services for contracts, contract-renewal holds,
tenants, room reservations and move-out requests are functional domain workflows,
but their original public methods do not yet enforce the current ABP operator
and per-building membership on every read, link and write. In particular, tenant
detail/list APIs can disclose identity and contact information to any
authenticated user if they remain available with authentication only.

**Temporary fail-closed HTTP rule:** the actual ABP conventional MVC controller
model applies an unconditional DENY authorization policy to those five
service types, on top of required authentication. This operates before action
execution, without injecting application repositories or changing domain
transaction logic. It also rejects forged admin/building headers. The rule
does not claim that internal calls or other service types are tenant-scoped.

Direct contract termination is independently omitted from MVC route discovery;
do not make it routable. This gate is intentionally NOT a green light to
expose contract, tenant or move-out APIs to authenticated callers.

## Follow-up implementation to remove the gate

- Reuse Slice A's persisted account/permission and building membership checks.
- Derive Contract/Reservation/MoveOut scope from their Room → Floor → Building
  relationship; derive Tenant read scope through authorized linked resources,
  not by assuming a tenant belongs to only one building.
- Require authorization of **both** source and target when linking a tenant,
  changing a room or creating a renewal hold; check resources inside the same
  DB transaction, with Identity authorization lease through commit for writes.
- Preserve existing contract, reservation-payment and move-out row-lock order.
  Do not change deposit settlement behavior without rollback/concurrency tests.
- Activate remote operations only in cohesive groups once real authenticated
  negative HTTP tests, PostgreSQL races and Full regression pass.

## Test evidence expected

- Real ABP conventional controller discovery for all five Slice B services.
- Every discovered service action carries an authenticated DENY authorization.
- Signed-in GET list and tenant-by-ID requests return 403 with no DB attached,
  proving denial before any repository access.
- Existing Slice A Building/Floor/Room filters remain unchanged.
- Keep the source/route gate fail-closed when future methods are added.
