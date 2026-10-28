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

## Shared contract scope policy groundwork

The enforced `BuildingCrudAccessPolicy` can now check the already-defined
`Contracts.Read` and `Contracts.Manage` capabilities for a server-resolved
Contract → Room → Floor → Building resource graph. Creating a contract
requires `ManageContract` on its Room parent; neither `Rooms.Manage` nor
a permission for another building may substitute. Tests `CRUDSEC17–19`
cover positive scope, cross-building denial, read/manage separation and
capability/resource mismatch. This is an internal prerequisite only; the
five unscoped Slice B HTTP services remain denied until mutations actually
call the policy and use an Identity/Building commit-bound fence.

## Verified first functional Slice B read slice

Added a separate **isolated-staging-only** authenticated contract GET/list
controller. It does not reuse or unlock legacy unscoped ContractAppService.
Both requests require ABP `Contracts.Read`, a live Identity account, a
commit-bound Identity permission lease, and current per-building membership.
The SQL query joins Contract → Room → Floor → Building and filters
membership inside the same SQL statement BEFORE ordering/paging.

The disposable PostgreSQL HTTP test seeds separate contracts in buildings
A and B, checks that a B-only operator sees only B and cannot GET A,
verifies root's two-building listing, rejects oversize pagination and proves
direct Contracts.Read revocation waits until the read UoW completes.
Any legacy write/action remains intentionally blocked. This does **not**
complete Slice B write authorization or tenant privacy.

## Draft contract create/update write slice (isolated staging)

The explicit staging contract controller now exposes POST and PUT **only**
through `SliceBStagingContractMutation`; the legacy unscoped
`ContractAppService` HTTP routes remain forbidden. The adapter:

1. Rejects unauthenticated/invalid input; resolves the current Room/Contract
   building from trusted repositories and checks `Contracts.Manage`.
2. Holds a persisted ABP Identity permission/account lock through the Building
   transaction commit. The existing PostgreSQL membership fence separately
   locks the actor's grant through commit.
3. Inside that SAME transaction, locks contract row (`FOR UPDATE` on update)
   and live room/floor/building graph (`FOR SHARE`), comparing the graph with
   the authorized building. Rechecks the permission+membership after mutation.
4. Uses original `IContractAppService` Draft validation and persistence
   inside the protected UnitOfWork, never a second independent commit.
5. **Forbids cross-building moves**, even with two memberships, until a
   separate two-building transfer protocol is designed.

This does not unlock signing, renewal, activation, tenant linkage or
reservation/deposit side effects.
