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

## Existing tenant link/role scoped vertical

Because `Tenant` is a global PII entity with no own `BuildingId`,
an arbitrary `TenantId` is NOT evidence of authority. The isolated
staging API now permits adding an existing tenant to a Draft contract only
when an active `ContractTenant` relationship in the **same building** is
proven and locked (`FOR SHARE` across tenant, source contract, room, floor,
building and relationship rows). First-time tenant registration remains
DENY until a dedicated, building-bound onboarding design is accepted.

Read `GET /contracts/{id}/tenants` returns only `TenantId` and `Role`,
never name/phone/identity/address. It requires `Contracts.Read`, a persisted
Identity lease, a server-resolved contract building and SQL-side live grant.
Existing-link role change and removal require a draft, `Contracts.Manage`,
the persisted Identity commit lease, a locked current contract row, room/floor
graph, member grant and exact link row; original domain validation remains
authoritative. Source/target inter-building operations remain DENY.

## Atomic new-tenant onboarding (staging only)

POST `/api/slice-b-staging/contracts/{id}/tenants/new` takes a
`CreateTenantDto` plus an explicit member/primary role. It is the only
reviewed new-tenant entry point: authenticated `Contracts.Manage` with a
persisted Identity commit lease, live grant row lock, verified
Contract → Room → Floor → Building graph, **Draft** state under contract
row lock, then tenant creation and contract-tenant relationship insertion in
the **same Building transaction**. Both original services retain their
phone/identity normalization, uniqueness and primary-role validation;
failures roll back the entire UnitOfWork, leaving no orphan profile.

Global phone and citizen-ID uniqueness failures are deliberately presented
as generic denials, never with the existing record's identifying value.
Successful API responses contain only `TenantId` and `Role`, not tenant
names, phones or identity documents. This does not open generic Tenant CRUD.

## Contract sign/activate (isolated staging)

The scoped staging contract controller exposes `POST /{id}/sign` and
`POST /{id}/activate` only through the existing fenced
`SliceBStagingContractMutation`; legacy HTTP service stays deny-all.
The current operator needs `Contracts.Manage`, an active Identity account,
persisted ABP permission locked through Building UoW commit, and live scoped
membership locked by `EfBuildingScopedWriteFenceCandidate`. The current
contract row is locked `FOR UPDATE`, the room is locked `FOR UPDATE`
**before** checking other signed/active contracts, and room→floor→building
graph is re-resolved under row locks. This serializes concurrent signing
and activation attempts on **different** contracts for the same room.

The original domain service still validates Draft/Signed lifecycle,
exactly one primary tenant, committed occupancy overlap, Available/Reserved
room status, and changes room status to Occupied on activation. No new
method for direct ending, financial settlement or cross-building transfer.

## Scoped contract renewal and hold consumption (staging only)

The isolated staging controller now offers:
- POST `/{id}/renewal-holds` (contract-bound hold creation)
- POST `/renewal-holds/{holdId}/cancel` (hold → contract server lookup,
  reject foreign building; contract and hold locked)
- POST `/{id}/renew` (renew Active contract with existing valid hold)

All paths lock persisted `Contracts.Manage` Identity permission through the
Building commit and the operator's live membership row. Contract row is locked
`FOR UPDATE` BEFORE hold mutation, matching shared payment/reservation
deposit settlement order. For renew, the room is also locked exclusively so
concurrent sign/activate cannot invalidate overlap checks. The legacy
`IContractAppService.RenewAsync` still enforces exact period, tenant,
unique renewal, active 24-hour hold, no conflicting reserved reservation,
copy of valid tenant memberships, cancellation of intersecting unpaid
pending reservations, and hold Completed state in the same UoW.

No direct unscoped hold/renewal endpoint is re-enabled; no production
deployment. Other payment/reservation APIs remain denied until audited.

## Reservation create (staging-only)

A first scoped `POST /api/slice-b-staging/contracts/reservations` wraps the existing RoomReservationAppService.CreateAsync behind persisted `Contracts.Manage` Identity lock, current Building membership transaction lock, Room→Floor→Building revalidation, exclusive Room row lock, and a locked existing ContractTenant provenance path for the requested tenant in the SAME building. Unknown/global-only tenant IDs fail closed. No legacy unscoped reservation route is opened. Payment completion, refund and cancellation remain locked until separately assessed.


## Scoped reservation read/list (isolated staging only)

The explicit staging controller now exposes \`GET /api/slice-b-staging/contracts/reservations\`
(with bounded pagination) and \`GET /api/slice-b-staging/contracts/reservations/{id}\`.
Both require an authenticated operator, a current Contracts.Read ABP permission,
a persisted Identity account/permission SHARE lease held through the transactional
Building read UnitOfWork, and current non-revoked per-building membership. A
single SQL query joins Reservation → Room → Floor → Building and filters the
operator's live grant before pagination or returning any row. Direct ID access
to another building, unknown IDs and invalid pagination fail closed.

The read DTO projects reservation financial terms, status, room/building and
tenant GUID only. It never projects global tenant PII, free-form notes, or
payment evidence. Legacy reservation CRUD, cancellation and deposit settlement
endpoints remain DENY. Disposable PostgreSQL authenticated HTTP tests cover
two-building list/get visibility and missing/foreign IDs. No production rollout.


## Scoped move-out request/approval (isolated staging only)

The staging contract controller adds contract-bound GET/POST
\`/{contractId}/move-out-requests\` and POST
\`/{contractId}/move-out-requests/{requestId}/approve\`. Creation rejects a
body ContractId differing from the route; both writes use the existing
Contract-row lock, Contracts.Manage Identity commit-bound lease, live
building membership fence, and server-rechecked Room→Floor→Building graph.
Approval also checks the request belongs to the locked contract before
calling the unchanged domain application service. The transaction keeps
creation/approval and checklist creation atomic. Reading joins the live
MoveOut→Contract→Room→Floor→Building graph and membership in SQL and exposes
only schedule/status metadata, no free-form private reasons. Legacy global
MoveOut endpoints stay DENY, and no move-out completion, deposit settlement,
Contract termination or Room release is enabled.

The disposable PostgreSQL HTTP fixture exercises foreign contract denial,
body/route mismatch, authorized scheduling and approval, privacy-preserving
read, checklist persistence and unchanged occupied Room state.


## Scoped move-out rejection, cancellation and checklist (isolated staging)

The staged contract routes \`POST /{contractId}/move-out-requests/{id}/reject\`,
\`POST /{contractId}/move-out-requests/{id}/cancel\`, and
\`GET /{contractId}/move-out-requests/{id}/checklist\` are now scoped to
the authenticated building operator. Both mutations use persisted
Contracts.Manage, the Identity commit lease, contract-row UPDATE lock, live
membership and an exact request→contract ownership check. The original
domain service still rejects non-pending lifecycle transitions. Checklist
read uses Contracts.Read and verifies the live request→contract→room→floor→
building graph with SQL membership, exposing only task status/types.
The disposable PG HTTP suite tests foreign route/request rejection,
successful reject/cancel followed by new scheduling, invalid later approve,
checklist privacy, and unchanged contract/room state.


## Scoped reservation cancellation (isolated staging only)

\`POST /api/slice-b-staging/contracts/reservations/{id}/cancel\`
requires an active persisted Contracts.Manage permission, locked Identity
account/permission until commit, and live building membership for the
reservation's server-resolved Room. The existing reservation row is locked
FOR UPDATE and its current Room→Floor→Building graph is locked and
revalidated in the same Building transaction. The original
RoomReservationAppService.CancelAsync still validates cancellation reasons
and performs paid-deposit refund/forfeit in the same UoW; we do not expose
the raw settlement service or silently change room occupancy. Foreign and
missing reservations fail closed. The PostgreSQL HTTP fixture checks
cross-building denial, successful pending cancellation, duplicate denial,
and no invented settlement for an unpaid reservation. The existing PG01–04
transaction tests remain responsible for actual paid refund/forfeit rollback.


## Scoped pending reservation deposit record (isolated staging only)

\`POST /api/slice-b-staging/contracts/reservations/{id}/deposit-payments\`
permits recording a **Pending** deposit only for an authorized reservation.
Body RoomReservationId must equal the route ID, never a foreign ID. The
same reservation row lock, current Room→Floor→Building membership fence,
persisted Contracts.Manage Identity lease through commit, and original
PaymentAppService.CreateReservationDepositAsync amount/method/reference
validation all apply. No payment completion, bank integration, refund
transfer, or global payment API is enabled by this stage. The disposable
PostgreSQL HTTP test covers route/body mismatch, foreign reservation denial
and success for a B-scoped pending payment.


## Reservation deposit completion (isolated staging only)

\`POST /api/slice-b-staging/contracts/reservations/{reservationId}/deposit-payments/{paymentId}/complete\`
accepts only an existing ReservationDeposit with a matching persisted
reservation relationship, and requires a current Contracts.Manage
permission with commit-bound Identity and Building membership. Unlike the
pending-payment create/cancel gate, completion deliberately does NOT
pre-lock the Reservation: the original PaymentAppService.CompleteAsync
acquires its Active Contract→Reservation→Payment locks in order and
serializes competing holds/renewals. The staged wrapper checks the original
payment relationship before and after completion, revalidates the
Room→Floor→Building graph under PostgreSQL row locks before commit, and
keeps global ContractDeposit, generic Payment APIs and bank integration
blocked. Isolated PostgreSQL HTTP tests cover mismatched route, successful
cash confirmation, duplicate denial and persisted Reserved status.


## Critical legacy payment HTTP boundary

\`PaymentAppService\` was previously covered by authentication only and
had no current resource-to-building authorization. The conventional
controller policy now explicitly DENIES every legacy PaymentAppService
HTTP action before action execution, including global GET/list, deposit
creation/completion/cancellation and allocation/settlement. Isolated
staging's explicit reservation-specific routes remain separately fenced.
The MVC policy test adds PaymentAppService to the denied service set.
This is mandatory for privacy and avoiding a bypass around staged
Contracts.Manage enforcement. No changes to internal PaymentAppService
transaction logic or production rollout.


## Isolated staged reservation deposit allocation

Added contract-bound POST allocation for a completed reservation deposit on the **same exact room** as the Active contract. The flow locks Contract before Reservation, verifies Contracts.Manage and building membership to commit, then calls the existing payment allocation service for amount/primary-tenant/duplicate validation and accounting. Generic legacy payment HTTP remains DENY; no production rollout.


## Scoped Draft/Signed contract cancellation

Staging-only POST contract cancel uses the same commit-bound Contracts.Manage and building fence; original ContractAppService.CancelAsync only accepts Draft/Signed. Active tenancy termination and room/deposit settlement remain blocked. PostgreSQL HTTP checks foreign contract denial, Active rejection and idempotent terminal rejection.


## Paid reservation cancellation full refund coverage

The PostgreSQL HTTP fixture now also exercises the supported 3,000,000 VND completed full deposit refund path, verifies a single completed refund accounting journal on cancellation and rejects a repeated cancellation. Cash refunds are ledger disposition records here; they are not evidence of an external bank transfer. The separate 500,000 VND forfeit case remains covered.


## Scoped move-out final readiness preflight — STILL BLOCKED

Added isolated staging read-only GET on an exact contract/request relation. PostgreSQL transaction holds a shared lock on the request, contract/room/building graph and operator membership through the readiness service call, and Identity Contracts.Read is locked to commit. The original internal MoveOutCompletionReadiness evaluator remains authoritative and **CanCompleteMoveOut is always false** until final invoice issue, actual debt clearance and verified deposit disposition exist. No contract termination, room release or cash payout API is exposed. HTTP tests prove foreign request denial and the explicit blockers.


## Scoped read-only final financial closeout review

GET \`/api/slice-b-staging/contracts/{contractId}/move-out-requests/{requestId}/financial-readiness\`
requires the existing locked Contracts.Read Identity lease and a matching live
request→contract→room→floor→building→membership SHARE-locked PostgreSQL path.
Inside the SAME UoW it combines persisted final-invoice release readiness
and contract-deposit ledger reviews, including historical allocation anomalies.
Its DTO contains no tenant PII or free-form notes; monetary totals are labeled
OBSERVED evidence and all issue/debt/refund/closeout permissions remain false.
Foreign/missing request IDs return 403, and final issuance, receipt allocation,
bank transfers, room release and direct contract termination stay DENY.


## Evidence-gated final meter signoff (isolated staging only)

POST \`/{contractId}/move-out-requests/{requestId}/verify-final-meters\`
is the only scoped checklist completion write. It demands persisted
Contracts.Manage **and** Rooms.Manage Identity permission leases to commit,
current building membership, matching request/contract and locked Contract.
The existing internal MoveOutFinalMeterSignoffAppService re-checks both
electricity and water physical evidence and marks ONLY FinalMeterReading
complete atomically. If actual readings are missing or ambiguous it refuses,
and no other checklist task, invoice, deposit or contract state changes.
PostgreSQL HTTP tests cover foreign IDs and no-evidence rejection with
all checklist tasks still incomplete.


## Scoped direct ContractDeposit collection (isolated staging)

Staged \`POST /{contractId}/deposit-payments\` records a Pending direct
contract deposit. \`POST /{contractId}/deposit-payments/{paymentId}/complete\`
accepts only a persisted ContractDeposit receipt for the exact route
ContractId, under Contract FOR UPDATE, active Contracts.Manage Identity
permission and live building membership held to commit. The existing payment
service validates primary tenant, deposit coverage, receipt method/reference
and atomically posts a single completed allocation journal. A second confirm
is refused. The global legacy Payment HTTP controller stays DENY. Tests
cover unauthorized building, mismatched contract identifiers, successful
500k cash confirmation, duplicate rejection, and one ledger row.


### Database truth for the closeout financial read

The development PostgreSQL schema does **not** contain the design-only invoice evidence archive/reviewer/journal tables. The API intentionally reads the existing FinalInvoiceIssuePreflight (persisted draft and utility evidence) and ContractDepositLedgerReview (actual settlement journal) instead of querying un-migrated FinalInvoiceReleaseReadiness relations. It explicitly returns `StagedInvoiceEvidencePersistenceNotMigrated`, `IndependentInvoiceReviewPersistenceNotMigrated` and `FinalInvoiceIssuanceNotImplemented` blockers, and keeps all posting/closeout booleans false. No migration is fabricated or production financial state inferred.


## Staging-scoped deposit receipt status queries

The unscoped PaymentAppService HTTP controller remains DENY. GET /{contractId}/deposit-payments and GET /reservations/{reservationId}/deposit-payments return at most 100 recent records filtered by exact ContractDeposit or ReservationDeposit purpose. Every read proves persisted Contracts.Read Identity permission and current membership, and its SQL query joins Payment to Contract/Reservation → Room → Floor → Building before selecting. Only receipt ID, amount, status, time and purpose are exposed; no bank reference, payment number, global tenant identity or notes. PostgreSQL HTTP tests assert foreign-building denial and private-field exclusion. Reads are evidence, never refund or invoice posting authorization.


## Scoped cancellation of Pending deposit receipts

Both reservation and contract payment cancellation are isolated-staging POST operations. The calling scope first locks the authorized Contract or active Contract→Reservation and live building membership, then an exact-purpose, exact-owner Pending Payment row FOR UPDATE. Only after this ownership+state proof does the original PaymentAppService.CancelAsync run in the same transaction. Completed receipts are never cancelled, duplicate cancellation is denied, and cancellation cannot create a DepositSettlement or cancel the underlying Reservation/Contract. PostgreSQL HTTP tests cover foreign resources, duplicates, completed-receipt rejection, and zero settlement for a cancelled pending receipt. Global PaymentAppService remains DENY.

## Source-only Slice B final-invoice evidence/reviewer PostgreSQL migration

`20261029200000_SliceBFinalInvoiceEvidencePersistence` adds only the
`BuildingManagementFinalInvoiceIssueEvidence` and
`BuildingManagementInvoiceIssuanceReviews` staging tables. It has **not**
been applied to a real development/application/production database.
The disposable CI PostgreSQL test invokes the migration's own Up/Down SQL
inside a random schema and ALWAYS rolls back its enclosing transaction.

The archive rejects UPDATE, DELETE and TRUNCATE. Its invoice/contract/room
period uniqueness and source FKs prevent duplicated or orphaned staging
records; a database hash-format check does not substitute for application
evidence checksum/provenance verification. The reviewer table accepts only
initial Submitted records, validates archived scope/hash and chronology on
INSERT, and permits exactly one independent reviewer decision under a
database trigger. A second decision, self-review, historical re-assignment,
deletion or TRUNCATE is refused. The Down migration refuses populated
archives/reviews after obtaining both ACCESS EXCLUSIVE locks.

**Still blocked:** operator authorization for any stage writer, independent
review workflow, issued-invoice ledger, receipt allocation, debt posting,
deposit refund, room release and MoveOut completion. Existing staging
financial-readiness explicitly continues to report the source tables as
not migrated because this change creates migration *source only*.
No HTTP route, migration startup hook or production enablement is added.

## Source-only staged invoice payment allocation journal

`20261029201000_SliceBInvoicePaymentAllocationStaging` adds the previously
modeled `BuildingManagementInvoicePaymentAllocationJournal` table as an
**offline migration source only**, with a strict unique `PaymentId`, FK to
`BuildingManagementPayments`, numeric/identity constraints, an immutable
UPDATE/DELETE/TRUNCATE trigger and a populated-history rollback refusal.

An INSERT trigger locks and compares the source receipt against its persisted
purpose (`InvoicePayment`), completed status, non-deleted flag, invoice and
contract attribution, amount, payment number, UTC paid date and transaction
reference. If a receipt later changes, the existing read-only journal auditor
must detect drift; the presence of this row **does not** post a financial
allocation. No live authorized writer or database deployment is provided.

There is still no issued-invoice ledger and therefore deliberately **no FK to
a draft Invoice as though it were issued**. Until an immutable actual issued
invoice, transaction-scoped authorization, monetary terms and source-locking
allocation writer exist, all issue, debt, refund and closeout gates remain
false. PostgreSQL tests execute this actual Up/Down in a transaction-local
random schema and always roll back, including after unsuccessful operations.

## Source-only immutable issued-invoice ledger (Slice B)

`20261029202000_SliceBIssuedInvoiceLedger` adds an issued-invoice ledger model
and PostgreSQL migration **source only**. It is not applied to any application
or production database. Tests run the actual migration SQL in a random schema
inside a rollback-only local PostgreSQL transaction.

Ledger rows reference the existing Draft, immutable archived evidence and
independent design-review ticket. Unique keys protect the invoice number,
draft, evidence, review, contract-period and room-period. Trigger validation
checks the exact stored graph, approved-for-design review chronology and
base amount while locking the source Draft through commit. Existing issued
history cannot be UPDATEd, DELETEd, TRUNCATEd or downgraded; draft headers
linked to it cannot subsequently be rewritten.

**Not yet allowed:** a review marked `ReviewedForDesign` is *not* legal
issuance authority. There is no exposed constructor, authenticated posting
service, approved fee/due-date writer, transactional source-provenance check,
registered DB posting role or payment/deposit settlement. The presence of
migration source or a matching evidence hash cannot turn staged money into a
recognized receivable. Do not turn on an issuance endpoint until those
conditions and their concurrency/rollback tests have been implemented.

The issued ledger's source immutability also covers InvoiceItems and
InvoiceItemMeterSources, not only the Draft header. Both mutation triggers
lock the owning Draft row before checking issued history; this lock order
serializes with ledger insertion and prevents late inserts/updates/deletes of
meter evidence after issue. Additional PostgreSQL probes exercise those
paths. No application issuer is enabled by these database checks.

## Single-chain issued invoice → completed receipt candidate reconciliation

`20261029203000_SliceBInvoiceReconciliationBoundary` is an **additional
source-only** migration that links staged allocation rows to the actual
immutable issued ledger (not the Draft), caps their sum under a row lock,
refuses cross-contract references, verifies the existing completed payment
source through the earlier trigger, disallows fractional-VND issued amounts,
and checks financial terms JSON against immutable indexed metadata.

A read-only `BuildingManagementInvoiceAllocationCandidateBalances` view
reports the exact issued total, staged attributed payments, outstanding
candidate balance, and whether all *staged* receipts match the total. Its
`IsDebtLegallyCleared`, `MaySettleDeposit`, and `MayCompleteMoveOut` fields
are deliberately FALSE. A paid-looking journal is NOT authorization to
refund, offset a deposit or release a room. Source SHA verification, an
approved policy/issuer, confirmed bank disbursement and a transaction-scoped
closeout operator grant remain required for a real posting service.

The PostgreSQL acceptance test executes the **entire four-migration Slice B
chain** (archive, review, staged allocation, issued ledger, reconciliation)
in a generated schema inside one rollback-only transaction, probing invalid
draft references, cross-contract allocation, over-allocation, duplicate
receipts, false closeout flags and rollback of populated history. No
production, application DB, payment processor, or unscoped API is touched.

## Entire runtime EF financial migration chain verification

`SliceBFullFinancialMigrationChainPostgreSql_Tests.SBCHAIN01` additionally
creates a random dedicated PostgreSQL database (not just a scratch schema),
executes `BuildingManagementDbContext.Database.MigrateAsync()` through the
new final finance migration, verifies real tables/FK/terms constraints, rolls
back only the empty Slice B financial migrations to Slice A's final recovery
migration, confirms invoice draft and membership tables survive unchanged,
then upgrades the real chain again and finally drops only its own database.
This is a CI-only opt-in using an ephemeral container and createdb-capable
test role. It never runs on a user or production database.

## Atomic isolated-staging final invoice and move-out closeout

Isolated host APIs now provide a single, explicitly opted-in, building-scoped
path for preparing final evidence, independent design review, root-only
candidate invoice issuance, manual completed bank receipt attribution and
an atomic root-approved move-out closeout with explicit deposit disposition.
The closeout transaction enforces approved date, existing final meter signoff,
all issued invoices fully backed by completed journaled receipts, verified
held contract deposit, no cross-room active leases/reservations, signed
inspection/handover and written deposit policy, documented damage for any
forfeiture, and a root attestation/reference for an ALREADY completed refund.
It inserts Refund/Forfeit records, immutable deposit approval, updates the
remaining four checklists, ends Contract, completes MoveOutRequest and
releases Room (or sets Maintenance when damage occurred) in one Building UoW.
Any failure rolls the entire transaction back. No automatic deduction from
debt, automatic bank transfer or production access is enabled; a manual
bank reference remains a human claim, not independently bank-verified funds.

The scoped stage adapter holds the persisted Identity permission/account
lease and building membership through the Building commit; BootstrapOwner
is additionally required for issuance, financial receipt and closeout.
For database-level financial history, `20261029204000` adds the immutable
approval table with source and completed disbursement constraints. No actual
application or production migration has been applied.

### Contract closeout SQL debt guard

The source-only deposit approval trigger now acquires the persisted contract
row FOR UPDATE and rejects any closeout whose held amount disagrees with the
contract DepositAmount or whose **any issued contract invoice** has less/more
journaled payments than its total. The settlement mutation trigger also
acquires the same contract row to serialize unscoped SQL writes with a
pending approval. Test probes cover unpaid, partially paid, fully allocated
and spoofed contract-deposit cases. This does not verify manual bank transfers
nor activate any production financial writer.

The immutable deposit closeout domain verifier now compares **every** archive
proof index against the JSON plus SHA-256, including times, refund and forfeit
IDs, bank reference, damage, inspection, signed handover and policy reference.
Unit tests force a mismatch in each individual index, proving it is rejected
without trusting a previously stored hash alone. Staging writer and controller
changes also trigger the PostgreSQL regression workflow going forward.

The SQL closeout gate additionally rechecks each journaled receipt against
its current `BuildingManagementPayments` row (completed invoice purpose,
non-deleted, amount, issued invoice and contract attribution) and refuses
unjournaled completed invoice payments in the same contract. This closes the
gap where a valid staged journal could outlive modified source payment data.
Scratch PostgreSQL tests cover later status drift and orphan receipts.

### Isolated ABP HTTP Slice B finance authorization smoke

The existing two-database staging HTTP fixture now explicitly registers the
Slice B financial mutation adapter, verifies the endpoint is mapped and the
service resolves, and calls final invoice preparation, review, receipt and
closeout endpoints with cross-building / missing-evidence requests. Denials
must leave issue evidence, issue review, issued ledger, allocation and deposit
approval tables empty. This is an *authorization/rollback smoke test*, **not**
yet a full happy-path issuance-to-closeout test. Only isolated scratch PG is
used; it is never activated for production.
