# Phase 2H H2/H3/H4 — financial foundations, 2026-11-03

Branch: wip/phase-2g2. Status: **FINANCIAL FOUNDATIONS — FIVE-GROUP LOCAL QUALITY GATE PASSED at exact code commit e24894f3b830a5640c2e7a5ba1857c0911579c2d (2026-11-04).** Full Phase 2H business acceptance is still HOLD; production and real money operations excluded.

Source: Ngọc Trang Phase 2G–2H Business Process Specification v1.0, section 8; owner-approved H1–H6 in Phase2G2H_ApprovedBusinessDecisions_20261101.md. The existing executable single safe lane remains month-end, linked new contract, all tenants, same rent/deposit and one actual verified carry-forward. It is not modified.

## H2/H3 — issued source rent reconciliation

Existing RoomChangeDailyRentQuoteCalculator and 12 TRF_MID tests already give read-only 30/31/29-day splits with explicit transfer-day ownership and whole-VND rounding. The new RoomChangeMidMonthRentReconciliation:

- Recalculates the public quote to reject forged amounts.
- Requires a same-month rent-only source ledger, a SHA-256-looking immutable issued invoice fingerprint when issued, independently reviewed rent receipt references when receipts are nonzero, and nonnegative whole-VND balances.
- Computes the candidate source credit/debit after already accepted append-only corrections; never edits the original invoice.
- Separately exposes potential overcollection where approved receipts exceed proposed source rent. This must be dealt with by independent finance/refund review, never an automatic payout.
- Always returns CanPostInvoice=false, CanExecuteRoomChange=false and IsVerifiedBankPayment=false.

Hypothetical 15 September 2026 (30 days), source 3,000,000 VND and target 4,000,000 VND, target charged the transfer day, nearest VND: source 14 days = 1,400,000 VND; target 16 days = 2,133,333 VND. An original source rent line of 3,000,000 would have a **proposed** -1,600,000 credit. If 2,000,000 rent had already been independently reviewed as paid, 600,000 overcollection requires a separate reviewed disposition. **This candidate is not a signed D01/D02 business tariff.**

## H4 — cash-backed deposit-delta planning

New RoomChangeDepositDifferencePreviewCalculator requires distinct source/target contract IDs, whole-VND source cash-backed held amount, already POSTED approved deductions, target required deposit, specific ledger/policy references and separate Owner evidence for nonzero deductions. It rejects unresolved disputes, earlier refund/carry, fake evidence, over-deductions and negative or fractional money.

For 3,000,000 VND held and no deductions:
- Target requirement 3,000,000 => proposed carry 3,000,000.
- Target requirement 4,000,000 => proposed carry 3,000,000, future collection still needed 1,000,000.
- Target requirement 2,000,000 => proposed carry 2,000,000, future refund still needed 1,000,000.

The identity source-available = carry + refund and target-required = carry + topup is enforced. The output is **never** bank proof, a completed ledger posting or financial approval; all execution flags remain false. A supplied ledger reference is not independently verified by these pure calculators.

## One-shot verification

- 24 new Domain facts: TRF_RENT01–12 and TRF_DEP01–12, including negative and adversarial inputs.
- 17 previously implemented Phase 2H PostgreSQL requests/roster/concurrency/atomic transfer cases.
- Full migration rehearsal on a newly created and deleted PostgreSQL database.
- Authenticated HTTP PostgreSQL end-to-end regression.
- Full Domain/Application/EF/HTTP isolated regression.
- Execute etc/scripts/verify-phase-2h-financial-foundations.ps1 after fast-forward pulling branch. The script does not read or overwrite the user's untracked SQL audit file.

## Explicit HOLD (not silently assumed approved)

D01: transfer-day charge owner/time; D02: final VND rounding and residual policy; D03: existing issued invoice reconciliation including services, adjustments, due/overdue and monthly receivables; D04: actual bank-confirmed top-up/partial refund, destination and unique transaction ledger; H6: reduced signed handover accepted by both source/target financial writer; mid-month authenticated and PostgreSQL commit-time authorization/race/rollback.

No production enablement, new EF migration, invoice posting, actual refund, contract ending or tenant access is included. This change can become **local/staging foundation verified** when its new exact commit passes all five groups; it is not full Phase 2H BUSINESS ACCEPTED.


## H6 minimum handover gate — 2026-11-03 additive checkpoint

Added `RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion`
as a **server-only transaction-local** last check for the existing 2H month-end
safe lane. It is invoked in `CompleteRoomChangeAsync` strictly **after**
the code has separately verified real original E/W readings, full current
tenant roster vs immutable snapshot, all official source invoice/debt reviews
and actual source held deposit payments. It rejects missing/request-mismatched
inspection, handover, signed policy/review, tenant roster, audit actor/time,
source disposition evidence, completion reference and conflicting future
booking/status. It does not accept checkboxes or "verified" flags from the HTTP
request: those flags are fixed only after the server verification steps succeed.

New regression IDs `TRF_HAND01–08` cover signed minimum package, missing
meter/roster/debt/deposit proof, future committed booking vs Available/
Maintenance mismatch, missing completion references and foreign/backdated
requests. The one-shot verifier now runs 32 focused Domain cases (24 H2/H3/H4
plus 8 H6) before PostgreSQL/HTTP/full regression. No new migration or
public endpoint is added; existing root-only execution and SQL triggers stay.

**Boundary:** evidence references are operator attestations, not independent
cryptographic verification of human signatures. H6 D06 final signatory matrix,
asset/defect evidence review, mid-month H2/H3 financial cutoff and H4 actual
verified top-up/refund execution remain HOLD. Neither this checklist nor its
tests activate the mid-month writer or production.


## 2026-11-03 — unified mid-month reconciliation packet (code committed, verification pending)

**New domain-only preparation:** `RoomChangeMidMonthFinancialReconciliationPacketFactory.Prepare`
binds one specific open RoomChangeRequest, active source Contract, intended
target contract ID, entire immutable tenant roster snapshot, independently
accepted OI-07 policy review, the signed **candidate** daily rent rule, the
issued-source-rent-only ledger snapshot and the actual-cash-backed deposit
split into a single version-1 SHA-256 audit candidate.

This addresses a previously independent-input failure mode: the H2/H3
quote, D03 source invoice correction and H4 cash split could each be
arithmetically correct but refer to **different contracts, dates, policy
revisions or new-contract identifiers**. The packet refuses such mixing.
It requires:

- Source contract active, its room matching the request, transfer date
  within the source contract term, target contract ID distinct from source.
- Exact request date = quote date; source monthly rent = signed quote
  old-room tariff; original rent ledger contract and billing month match.
- An approved, independent OI-07 review of an immutable all-tenant roster;
  NEW linked contract and AgreedDayProration selected explicitly; policy
  pricing reference identical to the quote's stated rule reference.
- Policy deposit reference identical to target-required-deposit evidence;
  selected deposit lane must equal recomputed carry/top-up/refund amounts.
- Actual held source cọc = nominal source Contract.DepositAmount and target
  required deposit = target proposed monthly rent, because the present
  Contract aggregate still ties a contract's deposit to its monthly rent.
  Until that model is explicitly decoupled, this is an intentional
  fail-closed restriction rather than a silent new deposit rule.
- UTC preparation after the independent policy review; recompute both
  original pricing and cash splits; SHA the versioned entire candidate.
- `VerifyFingerprint` rejects later altered amounts; `VerifyAgainst`
  additionally rebuilds the packet from the current request/source,
  original policy, independent review and roster.

**Illustrative scenarios, not authoritative tariffs:**

| Source rent / source cọc | Target rent / new required cọc | Proposed source rent for Sep 1–14 | Proposed target rent from Sep 15 | Candidate cọc disposition |
|---|---|---|---|---|
| 3,000,000 | 3,000,000 | 1,400,000 | 1,600,000 | Carry 3,000,000 |
| 3,000,000 | 4,000,000 | 1,400,000 | 2,133,333 | Carry 3,000,000, collect 1,000,000 later |
| 3,000,000 | 2,000,000 | 1,400,000 | 1,066,667 | Carry 2,000,000, refund 1,000,000 later |

These figures assume a **hypothetical** September 15 handover charged to
the target room and nearest-VND rounding. Neither D01's charge-day owner
nor D02's rounding option has been authorized as a global business tariff.
An invoice already issued is never edited; any computed credit, excess
receipt or future top-up/refund remains a **non-posted proposal**.

18 new `TRF_PACKET01–18` Domain tests cover referential consistency,
transfer date, rent and deposit policy selection, rejected independent
review, historic corrections, excess receipts, disputed held cash, forged
source ledger, candidate tampering, foreign policy/roster re-verification
and UTC preparation. The one-command verifier now requires **50/50
focused Domain and 30/30 transfer PostgreSQL tests**, a fresh scratch
migration, authenticated HTTP/PostgreSQL and Full regression.

**Do not treat preview SHA as independent identity or bank authentication**.
It has no EF mapping, persisted financial journal, remote API or contract,
invoice, debt, bank or deposit writer; all execution flags are false.
D01–D04/D06 final business details, independently authenticated source
records, signed physical checklist and the full 2H mid-month transaction
remain HOLD. Production is excluded. Exact new SHA runtime test PASS
is still required; older PASS logs are not evidence for the new code.


## 2026-11-03 — D01/D02 versioned daily-pricing rule candidate with SQL parity

The approved H3 **principle** is daily rent for the actual days spent
in each room. This does not approve a universal owner of the handover
day or a universal VND rounding/residual rule. Those **detail gates
remain open**.

Delivered an additive, append-only **per-request candidate**:
`RoomChangeDayPricingRuleCandidate.Prepare` requires the current active
source Contract, single-source/target RoomChangeRequest, full verified
immutable tenant roster, approved OI-07 independently reviewed policy
specifically selecting `AgreedDayProration`, explicit signed pricing
reference, UTC actor/review chronology, actual calendar month length,
transfer-day owner and independent VND rounding choice. It recomputes
`RoomChangeDailyRentQuoteCalculator` rather than accepting user-entered
amounts; the original source monthly rate must match the live Contract.
A candidate neither approves a **global** tariff nor posts an adjustment.

Persisted via migration
`20261103070000_Phase2HDayProrationRuleCandidates` as
`BuildingManagementRoomChangeDayPricingRuleCandidates`. A unique
request index, restrictive foreign keys, immutable insert-only and
no-truncate triggers, SERIALIZABLE requirement and original independently
reviewed policy/source scope checks reject direct SQL bypass. PostgreSQL
recomputes the same source/target day splits and nearest-VND / truncate
candidate amounts plus the full canonical SHA-256 field digest. Rollback
refuses to discard any historical candidates. The .NET Domain and
EF model are configured for this archived read-only record; the existing
root-only month-end completion writer does not read it or change its
eligibility.

Added 8 Domain cases `TRF_RULE01–08` (approved source/target
handover ownership, 29/31 days, explicit rounding, rejected review,
separate proposer/reviewer, quote forging and immutable drift),
7 PostgreSQL cases `TRFPG31–37` (actual Domain SHA parity,
forged amounts/evidence, unapproved policy, leap-year calendar,
immutability/rollback, non-SERIALIZABLE and empty up/down). Full
disposable migration test expects the pricing migration last and
checks its trigger. The one-shot script requires **58/58 focused
Domain + 37/37 room-change PostgreSQL cases**, fresh disposable
full migration, authenticated HTTP/PostgreSQL and Full .NET regression.

**Non-acceptance boundaries**:
- An independently reviewed OI-07 reference is necessary but NOT
  self-authenticating human signature proof; per-request candidate
  is not an Owner-approved universal policy or a final D01/D02
  residual allocation decision.
- This code does not select a handover-day owner, silently change
  the ordinary 2G.5 full-final-month rule, repair an already issued
  invoice, post bank cash, move occupants or enable a new HTTP writer.
- D01/D02 signed golden pricing fixtures with rounding residual
  treatment, D03 approved issued invoice/service correction,
  D04 actual cash/bank-reviewed cọc top-up/refund,
  D06 final signatories and full mid-month commit-time race-proof
  remain **BUSINESS HOLD**. Production is excluded.
- New code SHA must pass the local/staging five-gate verifier
  before calling this additional slice STAGING VERIFIED. A prior
  PASS at `b2e65af134042a2df34cc36823e10e6a59a625fd` does
  not certify these added Domain and SQL files.


### Staging-only D01/D02 intake and idempotent retrieval

The `SliceBStagingRoomChangesController` now exposes only under the
existing **isolated staging** and authenticated building/contract grant:

- `POST /api/slice-b-staging/contracts/{contractId}/room-changes/{requestId}/day-pricing-rule-candidate`
- `GET /api/slice-b-staging/contracts/{contractId}/room-changes/{requestId}/day-pricing-rule-candidate`

`POST` accepts ONLY explicit `TargetMonthlyRentVnd`,
`TransferDayOwner`, and `Rounding` (no default or client-supplied
`Accepted`, ledger claims, signed policy reference, money value
calculation or privilege flag). The service locks the contract,
request, destination room and full current tenant roster inside the
scoped serializable UoW, loads independently reviewed OI-07 policy
evidence and calculates the quote from authoritative source rent.
A second identical request returns the already-verified immutable
candidate; a different candidate for the same request is denied.
The SQL unique index rejects simultaneous duplicate writes.
`GET` revalidates the stored hash, source, roster and approved policy
before returning a non-executing candidate.

The isolated security boundary regression is updated for the two
new endpoints and checks authentication and UoW attributes. This
does **not** authorize actual mid-month rent debit, invoice,
deposit disbursement, signed-handover completion or an unrestricted
production route. The individual day-owner/rounding choices are
documented **candidate inputs**, not a project-wide D01/D02 rule,
and the reviewed OI-07 reference is not itself documentary proof
that the specific monetary parameters were signed by all parties.
Test evidence is required at the new code SHA before acceptance.


## 2026-11-03 — D03 official-source monthly rent reconciliation (code committed; test PENDING)

**Scope delivered without financial posting:** A new
`RoomChangeOfficialSourceRentEvidenceBridge.Assess` composes the
*actual recorded, independently issued MONTHLY invoice*, original
immutable financial snapshot, posted official receivable and **ALL**
manually reviewed bank receipts with their complete signed
rent/electricity/water/service allocations into a source **rent-only**
`RoomChangeSourceRentSnapshot` and recalculated H3 rent preview.
The monthly frozen `RentAmount` is used, never the whole issued
invoice total; rent paid comes **only from reviewed RENT allocation
lines**, not a client-entered paid amount, raw bank reference, deposit
or a utility payment.

It fails closed with no partial financial figures if any invoice/post
scope differs, if tariff or billing month does not match the
explicit transfer quote, if the quote is forged, if the original
invoice/source fingerprint cannot be verified, if the posting is
later than the evaluation date, if receipts/reviews/line splits are
missing/duplicated/rejected/overallocated, if the true posted balance
disagrees with the line-category totals, or if ANY monthly invoice
adjustment proposal/decision exists.

**Important financial classification gate:** The existing
`MonthlyInvoiceAdjustmentRecord.SignedDeltaAmount` adjusts the
**whole invoice**, not an independently verified rent line. Even
a reviewed positive/negative delta cannot be silently applied to
source rent. D03 therefore explicitly **HOLDS** all such history
until a separately approved, append-only per-line correction
method and independent classification are implemented. This is not
approval to void/rewrite the issued invoice.

`RoomChangeMidMonthFinancialReconciliationPacketFactory` additionally
provides `PrepareFromOfficialMonthlyEvidence`, which invokes this
D03 source bridge first and only then creates the existing
cross-contract/roster/policy/deposit reconciliation packet.
The earlier `Prepare` candidate factory and the existing safe
end-of-month transfer writer are unchanged. This new method is a
**Domain-only non-executing pathway**, not a new endpoint,
bank gateway integration or persisted financial transaction.

Seventeen `TRF_D03_01–17` Domain tests cover correct unpaid rent-only
charge, a genuine manually reviewed bank receipt split into rent
and utilities, a utilities-only payment which does not reduce rent,
incomplete/duplicated/unreviewed/rejected receipts, foreign posting
and source contract, mismatched month/rate, pending/approved-but-
unclassified corrections, missing history, future invoice posting,
forged receipt amount and a source-bound unified transfer packet.
The one-command verifier now requires **75/75 focused Domain tests
and 37/37 Phase 2H PostgreSQL cases**, plus full scratch migration,
authenticated HTTP staging and Full .NET regression.

The recorded 5/5 PASS for
`fb754eae455aec7ec03c366bb7ecd16360024fd0`
remains valid only for that earlier code SHA. **This D03 addition
is PENDING full runtime execution at its new SHA**, and the complete
history still has to be loaded by a future authenticated,
scope-checked server adapter. Manual bank review does not mean
the actual bank/gateway independently confirmed money movement.

**BUSINESS HOLD still applies:** D01/D02 owner-approved global
transfer day and residual allocation; D03 per-line append-only
invoice correction, fee treatment and authority; D04 actual cash
top-up/partial refund journal, bank evidence and replay control;
D06 legally/contractually approved signatories; the complete
mid-month contract/payment writer. Production remains unused.


## 2026-11-03 — cohesive D01/D02/D03/D04/D06 documentary evidence + last preflight (NEW CODE, TEST PENDING)

This batch is the remaining **non-posting foundations**, not a
mid-month contract or cash implementation. Existing signed
`RoomChangeDayPricingRuleCandidate` only established a user-selected
per-request numeric choice; its prior OI-07 review was already signed
before the actual handover-day/rounding fields were chosen. New
`RoomChangeDayPricingOwnerSignoff` closes that documentary gap by
requiring a SECOND explicit Owner signoff bound by SHA-256 to the
*exact* day choice, rate pair and the original independently reviewed
request. No hardcoded global D01 or D02 rule is introduced.

New `RoomChangeHandoverThreePartySignoff` requires separate documented
Owner, handover Staff and source **primary tenant** signers, checked
against the verified full-tenant roster and source H6 physical
electricity/water handover digest. A signed asset/defect document
reference is mandatory. This is a **candidate D06 matrix**, not a
project-owner approved legal signer policy or cryptographic signature
verification; the complete tenant-consent reference remains separate.

New `RoomChangeIssuedAdjustmentCategoryProof` signs each independently
accepted original monthly invoice correction into complete, unique
rent/electricity/water/service categories, requiring exactly the
original whole-VND total and a third actor to classify after the
independent financial review. The
`RoomChangeIssuedAdjustmentCategoryEvaluator` requires **all**
proposals, decisions and accepted category proofs, validates original
independent monthly issuance, rejects missing/double/future/orphan
history and negative invoice components. It distinguishes approved
**classification** from **official posted receivable adjustment**.
Existing `RoomChangeOfficialSourceRentEvidenceBridge` still properly
HOLDs any unposted category corrections; no fake debt reduction.

New `RoomChangeTransferDepositCashReview` accepts exact completed,
source-linked bank-transfer `PaymentPurpose.ContractDeposit` receipts
for target **top-up** only, or owner-destination-authorized independent
manual bank statement evidence for an outbound **partial refund**.
The original H4 deposit split, packet SHA, proposed target contract,
actual payment status, actor separation, signed references, amount
and UTC chronology must match. Neither leg is a bank gateway
confirmation, a posted refund or an authenticated SQL uniqueness
journal. The existing full-carried cọc safe lane is unchanged.

New `RoomChangeMidMonthDossierPreflight` cross-checks one full
financial packet against the exact signed daily pricing, original H6
electricity/water readings, roster and the candidate D06 signatories,
plus required H4 cash evidence. It returns explicit blockers for
missing/different sources, D03 official invoice posting and any
unresolved overcollection. **It always denies execution** and has
no client-trusted financial/verification flag.

The one-shot verifier now requires **100/100 focused Domain cases**:
previous SHA-verified 75 + 8 `TRF_FINAL01–08` independent policy
and signatory tests + 7 `TRF_CASH01–07` cash bank review cases
+ 7 `TRF_D03_18–24` categorized adjustment cases
+ 3 `TRF_DOSSIER01–03` integrated dossier tests.
The remaining 37/37 PostgreSQL transfer cases, scratch full migration,
authenticated PostgreSQL HTTP and full .NET regression are unchanged.
**This is a new code SHA and has NOT yet been executed in the
operator's Windows/PostgreSQL environment.** It must pass all five
groups before technical acceptance.

**Explicit remaining work before BUSINESS ACCEPTED / final writer:**
1. Owner-approved D01 charge-day instant and project-wide exception
   precedence; D02 residual allocation and signed numerical golden
   cases — candidate signoffs alone do not ratify universal terms.
2. D03 add a genuinely posted, append-only official **per-category**
   adjustment ledger plus receipts/overcollection dispositions,
   invoice due-date and any service fee treatment, commit-time SQL
   and authenticated integration tests.
3. D04 add transfer-specific, unique outgoing refund and target
   top-up cash/disposition journals linked to original source held
   cash and bank/receipt evidence, with independent review and
   concurrency/idempotency proofs. A Payment row or reference is
   not itself proof of new-contract deposit carry-forward.
4. Owner-approved D06 legal signature/tenant consent matrix and
   identity proof implementation (the three-role class is a candidate).
5. One scoped, SERIALIZABLE / commit-proofed EF/PostgreSQL **mid-month
   atomic writer** that ends the old contract, starts linked target
   contract, transfers full roster, posts only independently authorized
   invoice and deposit legs and changes both room states. Must pass
   real HTTP/PG concurrency/replay and full regression.
6. Explicit owner BUSINESS ACCEPTED signoff after executing this full
   scenario matrix. No production action is within scope.

Do not claim the above future writer or money journal is implemented,
even if a source-only or full foundation test suite passes.


## 2026-11-03 — D01/D03/D04/D06 append-only PostgreSQL evidence archive (new code, TEST PENDING)

The Phase 2H evidence candidates introduced in the prior verified
code SHA are **Domain-only**: until stored transactionally, they can
be dropped, replayed or reattributed by a nonconforming adapter.
The new `RoomChangeEvidenceArchiveRecord` explicitly captures one
SHA-verified immutable original documentary proof and its exact
parent into a canonical JSON + SHA-256 evidence envelope, with
the original transfer `RequestId`, source `ContractId`,
`BuildingId`, actor and UTC timestamp.

Four SOURCE-TYPED capture paths enforce the ORIGINAL documents:

- `SignedExactPricing`: signed Owner D01/D02 candidate and source
  daily rule, policy review and immutable current roster;
- `ClassifiedInvoiceCorrection`: independently issued original
  monthly invoice, exact period/source room and independently reviewed
  classified D03 correction;
- `ReviewedTransferCash`: independently reviewed D04 transfer
  top-up/refund evidence checked against the same source-scoped
  non-posting financial packet and original completed target Payment
  for a top-up;
- `ThreePartyPhysicalHandover`: D06 3-party signer matrix,
  original H6 signed E/W meter source and exact roster.

New EF configuration and additive migration
`20261103080000_Phase2HDocumentaryEvidenceArchive` create
`BuildingManagementRoomChangeEvidenceArchives`. The SQL trigger
denies updates, deletes and TRUNCATE; requires SERIALIZABLE;
cross-checks the locked request/contract/building and parent
day-price, original monthly invoice or physical handover where
already persisted; validates the JSON payload identity and both
payload/envelope SHA-256 checksums; rejects reuse of a signed
evidence ID or a second D01/D04/D06 archive for the same request.
D03 may archive multiple independently classified adjustment
documents for the SAME originally issued monthly invoice, as long as
each distinct adjustment proof ID has not been archived before.
The migration refuses downgrade if any signed archive exists.

**Important evidentiary boundary**: D04's parent financial packet
is currently read-only and not persisted as an authoritative
debit/cash journal. Database checks the packet SHA/IDs preserved
in the D04 JSON but CANNOT independently prove source held cash
or executed bank transfer by that SHA. No human signature
authentication or gateway verification is claimed. These archive
rows cannot execute a transfer, issue an invoice, post a monetary
liability, refund or move held cọc.

Added 6 Domain `TRF_ARCH01–06` capture/negative tests, plus
7 PostgreSQL `TRFARCHPG01–07` tests checking all kinds,
canonical archive SHA parity, forged JSON, incorrect scope/parent,
uniqueness, append-only mutations, rollback and non-SERIALIZABLE
denial. One-shot acceptance script now requires **106/106 Domain +
44/44 PostgreSQL**, full disposable EF migration chain (checking
the new integrity trigger), authenticated PostgreSQL HTTP and
Full regression. **New code SHA remains unverified at runtime**
until operator's exact-SHA 5/5 test output is received.

This is NOT Phase 2H BUSINESS ACCEPTED: D01/D02 system-wide detail
decisions, official D03 per-line receivable posting, D04 cash-backed
unique settlement/refund posting and D06 final human authority
remain gated, followed by one commit-time protected mid-month atomic
contract and bank/invoice writer. Production remains excluded.


## 2026-11-03 — D03 official correction journal and D04 original top-up posting gates (NEW CODE / TEST PENDING)

The previously verified `106 Domain / 44 PostgreSQL` archive and
preview work did **not** create a financial posting. A new additive
migration `20261103090000_Phase2HMidMonthFinancialJournal` introduces
`BuildingManagementRoomChangeMidMonthFinancialPostings`, a separate,
append-only, SERIALIZABLE journal with SQL validation against:

- The locked open and mid-month room change request/contract/building;
- The separately archived D01/D02 exact Owner pricing and D06
  three-party source handover, belonging to the SAME request;
- For **D03**: an archived independently classified monthly invoice
  adjustment, the ORIGINAL posted official receivable and issued
  invoice SHA, original adjustment proposal and accepted decision,
  the signed category total and independently reviewed receipt
  ceiling; multiple adjusted amounts cannot reduce the debt below
  accepted receipts or erase the invoice;
- For **D04 incoming top-up**: the original completed manual bank
  Payment with correct target contract, amount, transaction reference,
  still-unapplied deposit disposition, exact bank method/channel,
  and independent D04 archival proof.

The SQL rejects replay of the same document, payment or bank transaction,
multiple cash legs for the same request, mutation, deletion and truncate,
wrong data scope, missing signatures and insufficient transaction
isolation. The D04 **outgoing refund leg is denied**; the manual
statement evidence alone cannot authorize an outbound banking payout.
No production migration or outward payment is executed. Importantly,
a journal row is not a replacement for the full mid-month new-contract
and cash disposition writer; the final atomic commit gate and the
live, authenticated adapter are still not implemented.

Four new `TRFPOSTPG01–04` isolated PostgreSQL safety tests are included;
the one-shot verifier now requires **106 Domain + 48 PostgreSQL**,
the full disposable migration chain with the new SQL trigger, HTTP
and full regression. **Do not mark this SHA PASS until the user's
local PostgreSQL and .NET test output is received.**

The existing month-end completion writer remains unchanged.
The Phase 2H financial rule and legal-signature business approvals
and full bank payout/atomic mid-month writer are still HOLD.


## 2026-11-03 — Original financial claim idempotency + bank actor separation (TEST PENDING)

A full five-group PASS on `db117bcdee473dce43d8e76034430ba55052acbb`
was recorded before this change. The previous D03 journal deduplicated only
by archived *evidence ID*: one independently approved adjustment could be
presented under a fresh archive and posted twice. That is not adequate
original financial source idempotency.

The additive `20261103100000_Phase2HFinancialOriginalClaims` migration
adds `ApprovedAdjustmentId` with a unique original-adjustment claim,
a reference to the genuine monthly adjustment, and a SERIALIZABLE
commit-time trigger. The D03 trigger rechecks immutable original
proposal, accepted decision, same source contract/building, review time
and separation of proposer/reviewer/classifier/posting actors. The
D04 trigger rechecks the original bank payment against bank statement
reference, distinct independently reviewing and owner-approving humans,
and signed review time. The older SQL guards must ALSO pass.

An existing D03 journal requires manual historical audit before migration;
it is never silently backfilled. Down refuses to drop protections if ANY
journal history exists. The new test cases `TRFPOSTPG05–08` cover
duplicate *original* adjustment with a new archive ID, same-person
review rejection, missing D04 bank proof and two original financial
legs in one request. Script now requires **106 focused Domain and
52 focused PostgreSQL**; full disposable EF migration, authenticated
HTTP and Full regression remain mandatory.

**This is NOT a transactionally integrated mid-month contract writer.**
The D04 outbound refund is still fail-closed, and the original
authenticated bank gateway/settlement plus atomic contract-room-deposit
writer are not present. Production excluded. This new SHA must be
tested locally before it can be marked PASS.


## 2026-11-03 — Authenticated operator-facing D03/D04 staging journal read (TEST PENDING)

The previously unconsumed SQL finance journal can now be read at
`GET /api/slice-b-staging/contracts/{contractId}/room-changes/{requestId}/mid-month-financial-journal`.
This read-only endpoint uses the **same authenticated Contract.Manage
identity lease, live building membership commit fence and source
contract lock** as existing staging business operations. It additionally
authorizes and locks the target room under the SAME building, and
reads original PostgreSQL journal rows under `SERIALIZABLE` using
server-bound scoped GUID parameters. It exposes only the counts and
exact VND sums of accepted D03 correction and D04 top-up postings,
never payment account, bank statement, or original witness IDs.
No client-supplied money/verification flags are trusted.

The response explicitly returns false for actual outgoing bank refund,
mid-month atomic contract enablement, and `CanExecuteTransfer`. It
never posts funds or modifies source/target room/contract lifecycle.
Existing month-end completion route stays unchanged. Metadata tests
have been updated to verify the 20 authenticated staging routes and
the new non-executing projection.

This does not substitute for a live authenticated HTTP cross-building
attack test against the actual PostgreSQL adapter; code metadata
checks alone do not establish this. NEW code remains unverified at
runtime until the exact-SHA combined verification completes.


## 2026-11-04 — Separate mid-month **same-rent/same-cash** atomic staging lane (NEW, TEST PENDING)

The source month-end H1/H5 writer is preserved and untouched. An
independent versioned `RoomChangeMidMonthCompletionRecord` and additive
`20261104100000_Phase2HMidMonthAtomicCompletion` introduce the
H2/H3 **same monthly rent / same held deposit** narrow lane, with
a real staging `CompleteMidMonthRoomChangeAsync` command and the
following supporting HTTP operations:

- Owner-sign and archive a per-request original D01/D02 day ownership
  and VND rounding decision;
- Owner document the original D06 three-party source E/W and asset
  handover; no claim of cryptographic signature verification;
- Independently classify a previously approved D03 original monthly
  invoice correction by category, archive it, and post its original
  adjustment to the official receivable once;
- Execute the confirmed whole-roster transfer at **SERIALIZABLE**
  into a fresh linked active contract, end the source `RoomChanged`,
  update both rooms, move every tenant, complete one original
  cash-backed full deposit carry and seal source/target first month
  rent claims.

The application verifies original current-month issued invoice,
frozen electricity/water readings and verified/manual-reviewed
receipt journal; the D03 corrections must exactly equal the
difference between original source rent and the signed prorated
source rent. Corrected original invoice must be fully collected;
no overcollection or other unpaid source receivables is allowed.
The new target's first-month rent is frozen in a unique
append-only completion; a PostgreSQL trigger forbids the later
monthly invoice candidate from charging the full month again.
A deferred SQL commit guard rechecks request, independent
policy/review, day-rule/Owner proof, signed physical/tenant proof,
corrected source invoice, deposits and both contracts. Existing
`Contract.End(RoomChanged)` and terminal request gates now
accept **either** the old month-end completed record **or** the
separately verified new midmonth record. The two histories cannot
re-use the same source/target.

The H4 price/deposit **top-up/refund** variants are **still HOLD**
and denied by this deliberately restricted executable path.
A real bank refund is not inferred from an operator statement.
The D01/D02 per-request attestation and D06 three-party references
remain documentary, not cryptographically authenticated.
Production execution is excluded.

Eight new isolated PostgreSQL adversarial tests
`TRFMIDPG01–08` probe foreign-parent rejection, isolation, immutable
history, target full-month rebilling, old/new double completion and
empty downgrade. They are **not** positive authenticated financial
execution tests, and the complete 2H Business Accepted label remains
HOLD. The one-command verifier now checks **106 Domain,
60 PostgreSQL, scratch full migration, authenticated PostgreSQL HTTP
and Full regression** at the new exact code SHA. The previous five-group
PASS on `687bcc068f6451e5c45c55f05f2c49d29ea20632`
does not certify these new changes.


## 2026-11-04 — Post-completion INSERT fences (NEW, TEST PENDING)

The signed same-rent/same-held-deposit mid-month lane from
`13b20d0f9ac66e7f89cc45c02e0477094c0b8bd4` remains **not
business-accepted** until authenticated positive execution and the
full local verification bundle pass at the exact new SHA. Code review
found a post-commit integrity gap: the existing triggers froze tenant
roster and original cash on UPDATE/DELETE, but not on INSERT. New
records could otherwise silently change a completed financial/tenant
disposition after its original deferred commit check.

The additive migration
`20261104120000_Phase2HMidMonthPostCommitAppendFences` adds two
`DEFERRABLE INITIALLY DEFERRED` PostgreSQL fences:

- A new live tenant in either the source or target contract of a
  completed mid-month transfer must preserve the originally captured
  roster count on **both** sides at commit. This allows legitimate
  initial transfer INSERTs in the same atomic transaction but denies
  later silent roster expansion. The existing UPDATE/DELETE freeze
  still applies.
- A new deposit settlement may not spend the completed source again,
  nor add a new incoming source-deposit settlement after the source
  has been closed. The original carry is identified solely by its
  immutable `CarryForwardSettlementId` and remains permitted within
  the original atomic transaction.

`TRFMIDPG09–11` check target/source live-tenant and source cash
append attempts, including a fabricated extra source deposit.
`TRFMIDPG12–13` confirm that the original in-transaction target
roster and unique cash carry are still allowed by the deferred fences.
`TRFMIDPG14` denies later mutation of the frozen target first-month
prorated invoice candidate back to a full-month rent charge. The
one-shot script now requires **106 Domain + 66 targeted PostgreSQL**
tests, full disposable migration chain, authenticated HTTP and full
regression. The migration's Down refuses removal after **any**
completed mid-month financial/occupancy history. Changes are
**source-only**: neither this environment nor a user application DB
was migrated. These new tests have not been run or marked PASS here.

**Remaining HOLD**: positive authenticated same-deposit mid-month
execution over real PostgreSQL, genuine three-party identity proof
rather than documentary references, H4 independent top-up and
outgoing bank refund settlement/compensation, disputed/overpaid
source rent disposition, and end-to-end cross-building concurrency
under the actual host. Production explicitly excluded.


### 2026-11-04 — Deferred-trigger PG fixture correction (TEST PENDING)

The positive `TRFMIDPG13` original-carry case now preserves the unique
source-to-target carry row initially inserted by the scratch transaction
instead of deleting it and inserting another. PostgreSQL deferred INSERT
events can remain queued for the earlier row after a same-transaction
DELETE, so the previous test fixture could trigger a legitimate
`23514` from its own stale extra-carry event. The updated positive case
forces the actual `TR_BM_2HMid_CashAppend` constraint to IMMEDIATE
after the completion row is present and checks the legitimate original
cash carry. Test count remains unchanged at **66 focused PostgreSQL**.
This is a test-fixture correction, not a production SQL gate bypass.
The test has not been run locally in this environment. It does not
replace collecting the user's original failing PostgreSQL log.


### 2026-11-04 — Actual local Phase 2H gate feedback (not yet accepted)

User's Windows PowerShell log for commit `b3c49968ee1c92293a70bcad3f91e46c287b8c03`
proves **106/106 focused Domain cases PASS**. PostgreSQL credentials were
corrected (the formerly failing `TRFMIDPG01` now PASSED); the
PostgreSQL run subsequently reported a failure in
`TRFMIDPG03_Once_Inserted_Cannot_Update_Delete_Or_Truncate`.
The wrapper stopped at xUnit's first stderr `[FAIL]` line, so this
log does *not* contain the actual exception or establish that no other
PostgreSQL cases failed.

Follow-up test fixture fix: verify the explicit no-TRUNCATE trigger
*before* inserting a mid-month completion into the scratch transaction.
After insertion, the unfinished deferred PostgreSQL trigger events can
cause an earlier PostgreSQL pending-trigger error (`55006`), which does
not verify the intended user trigger's `23514` rule. The separate
post-insert UPDATE, DELETE, and migration downgrade denial tests remain.
The test runner now captures the entire xUnit process and error output
under Windows PowerShell 5.1, then checks the native exit status rather
than aborting on an individual stderr record.

**Post-fix local execution is pending**; no acceptance claim for the
66 PostgreSQL cases, migration rehearsal, authenticated HTTP, or full
regression has been made. No production use.


### 2026-11-04 — 65/66 PostgreSQL feedback; cross-schema catalog isolation fix (TEST PENDING)

Local PostgreSQL run of the previous commit reported **65 passed, 1 failed**.
The sole failure was
`TRFPG03_EmptyHistory_Allows_Rollback_And_Reupgrade` at the
`pg_trigger` count assertion: **expected 0, actual 1**. Its historical
SQL counted a trigger name across every schema in the shared isolated
test database while other xUnit tests executed in parallel. This can
incorrectly count a matching trigger in another scratch schema.

The test now joins `pg_trigger`, `pg_class`, and `pg_namespace` and
filters on `n.nspname=current_schema()`, checking only the test's own
migration artifacts. It creates a second random schema with a same-name
unrelated trigger within its rollback-only transaction to reproduce the
previous false positive deterministically. It continues to assert **0
own-schema triggers after Down** and **1 own-schema trigger after Up**.
No migration, application business logic, or production database was
altered by this correction; no additional C# imports are required.
The post-change full PostgreSQL and five-group gate have NOT yet been
run locally.


## 2026-11-04 — Local five-group foundation gate accepted at exact code SHA

The operator supplied console evidence for
`etc/scripts/verify-phase-2h-financial-foundations.ps1` executed
locally on `wip/phase-2g2` at exact code commit
`e24894f3b830a5640c2e7a5ba1857c0911579c2d`:

```text
Passed! - Failed: 0, Passed: 68, Skipped: 0, Total: 68
=== QUALITY GATE PASS: ALL REQUESTED TEST RUNS SUCCEEDED ===
=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===
COMMIT=e24894f3b830a5640c2e7a5ba1857c0911579c2d
```

The verifier's five required steps are: (1) 106 focused Domain
financial/documentary cases, (2) 66 targeted PostgreSQL transfer
cases, (3) full migration rehearsal on disposable local PostgreSQL,
(4) authenticated HTTP test fixture on disposable local PostgreSQL,
and (5) full isolated regression, including the reported 68 HTTP
tests. The reported five-group PASS is operator-provided evidence
for this **exact code commit**, not evidence of production readiness.
The subordinate full raw test logs were not independently inspected
in this environment. Prior PASS/HOLD comments above are historical
checkpoints; this final note supersedes their pending-test status
for the specific five-group bundle at this SHA.

**Business acceptance remains HOLD** on positive authenticated
mid-month completion of real staged contracts and money, H4 unequal
deposit top-up/outgoing bank-refund writer with independently verified
cash and replay prevention, legal/independently authenticated D06
signature evidence, and cross-building/Identity concurrency E2E
attack acceptance on the actual authenticated endpoint. The routine
itself states that its source-only security checks are *not proof*
of authenticated cross-building CRUD isolation. Do not label the
entire Phase 2H BUSINESS ACCEPTED and do not deploy production.


## 2026-11-04 — H4/D03/D06 Owner change authorization; code acceptance still HOLD

The Owner approved a revised **financial execution policy** after
the prior 5/5 focused foundation PASS:
- The exact handover business date/time must be interpreted in
  `Asia/Ho_Chi_Minh`. The source/target **owner of the transfer
  calendar day** still comes from an explicit, individually Owner
  signed pricing candidate; the latest reply does not prescribe a
  universal source/target selection.
- The owner accepted the original whole-VND midpoint-up rounding
  recommendation for calendar-day proration.
- Services are **not priced identically per building**: price and
  units are independently editable per *Room × Service*, e.g.
  room 301 service 150,000 VND per occupant per month. Source and
  target contract billing history must freeze their different
  rate versions. Existing `Room` entity does not yet map this
  historical per-room service tariff schedule.
- A higher target security deposit **does not prohibit the room
  move while the additional amount is unpaid**. Authorize the
  transfer with a separate due deposit receivable and deadline
  at most one Vietnam **calendar month** from transfer.
  Outstanding/overdue is not cash backed. Existing H4
  deposit-preview arithmetic remains useful but its prior
  "must collect before transfer" assumption is superseded.
- Lower new-room deposit: explicitly and exclusively choose to
  credit the surplus against **next month's rent** or refund
  the tenant after reconciliation, with distinct real evidence
  and bank checks. Retained tenant credit is not a deposit balance
  nor a new cash receipt.
- Three-party physical handover signers (Owner/Staff/primary tenant)
  and attached handwritten signature evidence are approved.
  Owner must be able to export the new linked contract as a file.

Authoritative details and acceptance IDs are in
`Phase2G2H_ApprovedBusinessDecisions_20261101.md` (2026-11-04
addendum) and
`Phase2G2H_PostDecisionImplementationScope_20261101.md`.

**Important:** The existing restricted `CompleteMidMonthRoomChangeAsync`
checks same source/target rent, nominal same deposit and zero unposted
difference; these checks are still in code and safely **deny the new
H4 situations**. They must only be replaced by a separately designed,
versioned and transactionally protected H4 execution lane with
evidence/financial posting, **not** by removing security constraints.
No new migration or API writer was deployed by the above Owner
decision. Historical foundation verification remains valid for its
earlier code SHA, not for this newly authorized scope.


### 2026-11-04 — D01 transfer-day owner CONFIRMED by project Owner

**The unresolved question of who pays for the handover civil day
is CLOSED.** The original source room is chargeable through the
**previous Vietnam calendar day**; the destination/target room
is chargeable **on and after the confirmed transfer day**. The
Vietnam business day is derived in `Asia/Ho_Chi_Minh`, separately
from immutable UTC audit timestamps. This is the binding rule for
future executed Phase 2H transfers, not an optional client-supplied
alternative.

For a full-month billing context with `N` calendar days and
target starting on day `k`: old days `k−1`, new days
`N−k+1`; never count day `k` twice. The approved D02
whole-VND rounding rule remains unchanged.

The previously designed day-rule candidate and archive allowed
an explicit `TransferDayOwner` choice; **implementation of the
new business rule is not yet complete**. Completion preflight,
Owner pricing signoff, SQL commit guard and authenticated
PostgreSQL tests must be updated to refuse source-day candidates
for new transfers while retaining immutable prior evidence.
Do not bypass or alter the SQL approval checks merely to accept
an outdated signed record.

The room-specific service prices, one-calendar-month deferred
top-up, exclusive next-rent-credit vs refund, three-party
handover and Owner contract export remain approved as recorded
in the companion 2026-11-04 decision register. They are
separate implementation gates. No production work was performed.


## 2026-11-04 — First implementation batch after Owner final D01/H4/service decisions (CODE COMMITTED, RUNTIME TEST PENDING)

**Do not mark this commit business accepted until the exact new SHA
passes the full local gates.** The prior
`e24894f3b830a5640c2e7a5ba1857c0911579c2d` 5/5 PASS remains
historical foundation evidence only.

### Implemented code

1. **Binding D01/D02 execution rule:** New
   `RoomChangeApprovedH2H3ExecutionPolicy` validates that new
   transfers allocate the Vietnamese local handover civil day to the
   **target**, partition the month without overlap, and use whole-VND
   midpoint-up rounding. Historical SourceRoom/truncation candidates
   can still be verified/read as immutable evidence but can no
   longer be proposed through the authenticated staging intake or
   executed through `CompleteMidMonthRoomChangeAsync`.
   `RoomChangeMidMonthCompletionRecord.Complete` independently
   refuses noncanonical/legacy signed rule values.
2. **Additive PostgreSQL commit-time gate:** New EF migration
   `20261104130000_Phase2HApprovedTargetDayCommitFence`
   creates a deferred `TR_BM_2H_ApprovedTargetDay_Commit` trigger,
   verifying original transfer date, rule ownership, rounding,
   actual calendar days and target first-month rent.
   This is intentionally a **new migration**, not an edit to a
   historical migration that may already have been applied. Down
   refuses to erase existing mid-month completion history.
   `TRFMIDPG15` exercises source-day denial and valid target-day
   allowance in an isolated PostgreSQL scratch schema; it is
   deliberately not a positive real-bank financial transaction.
3. **H4 deferred deposit policy (domain-only):**
   `RoomChangeDeferredDepositDispositionPlanner` recomputes the
   established cash-backed source preview and distinguishes
   `CarriedTargetDepositVnd`, original **unpaid top-up
   receivable** due within one local calendar month, and the
   mutually exclusive **next-month-rent-credit** vs
   **reviewed manual refund** liability. It will not treat
   uncollected money as held cash or mark a payout/credit posted.
   The new policy deliberately does not switch on the old
   same-rent/same-deposit mid-month writer.
4. **Room-specific service pricing (domain-only):**
   `RoomScopedServiceTariffVersion` and
   `RoomScopedServiceChargePreview` represent independent
   RoomId × ServiceId tariff versions, effective dates, per-person,
   per-room and metered price units, explicit whole-month vs
   calendar-day charging and a source SHA-256 digest. Every
   quote is non-posting. An invoice must later bind the original
   tariff version and authoritative verified tenant count/meter
   sources; later edits cannot silently change issued charges.
   This code does **not yet** persist tariff records in EF or
   expose Owner edit APIs, and it does not claim to do so.

### Quality gates added

- Existing focused Domain: **106**.
- New binding D01/D02 tests `TRF_D01_01..08`: **8**.
- New H4 deferred top-up and surplus-choice tests
  `TRF_H4DEF01..13`: **13**.
- New room-specific service tariff tests
  `TRF_SVC01..10`: **10**.
- **Focused Domain expected: 137**; targeted PostgreSQL expected:
  **67**, including new `TRFMIDPG15`; full migration chain
  now requires the 20261104130000 migration last.
- Run existing five-group
  `etc/scripts/verify-phase-2h-financial-foundations.ps1`
  against the dedicated local test DB. Full regression and
  authenticated HTTP remain included; never run against the
  application/production database.
- The connected coding environment has no `dotnet`, Docker or
  local PostgreSQL test runtime. All newly authored changes are
  **code review only, not runtime PASS** until the Owner supplies
  exact-SHA full logs. Any compiler/SQL issues must be fixed at
  their source, not by deleting assertions or bypassing guards.

### Still required for complete Phase 2H BUSINESS ACCEPTED

- Persist new RoomId × ServiceId tariff versions and immutable
  invoice/contract billing snapshots; authenticated Owner edit/
  access policies and overlapping version concurrency safety.
- H4 **actual atomic writer**: linked contract with different
  target rent/deposit, original held cash carry, independently
  verified later top-up receivable collections, tenant surplus
  future rent-credit application or actual independently reviewed
  bank refund, plus unique ledger and SQL integrity constraints.
  The existing restricted same-deposit transfer remains fail-closed
  for H4 until that writer has genuine PostgreSQL/HTTP coverage.
- Authenticated successful financial midmonth completion and
  cross-building/revocation/race test on real disposable PG,
  genuine D06 signed-attachment origin review and Owner-scoped
  exported new-contract file. No production.


## 2026-11-04 — OWNER-VERIFIED five-group local gate for D01/H4/service *foundation*

The operator furnished the following terminal output for exact code SHA
`01fcd141f7a7c268ec90d855130adf098f5c79e4`:

```text
Passed! - Failed: 0, Passed: 68, Skipped: 0, Total: 68
=== QUALITY GATE PASS: ALL REQUESTED TEST RUNS SUCCEEDED ===
NO DATABASE MIGRATION, NO POSTGRESQL TEST (COMPILE ONLY), NO LIVE APPLICATION STARTED.
Source-only checks are NOT proof of authenticated cross-building CRUD security.
=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===
COMMIT=01fcd141f7a7c268ec90d855130adf098f5c79e4
Restricted same-rent/same-deposit mid-month lane requires positive authenticated execution acceptance; H4 top-up/refund and production remain HOLD.
```

As defined by the exact code SHA's verifier: **137 focused Domain**,
**67 focused real PostgreSQL**, **disposable fresh complete migration chain**,
**isolated authenticated PostgreSQL HTTP fixture** and **full regression** passed.
The final HTTP host test assembly reported 68/68. The full subordinate
raw logs were not supplied here for independent inspection; the verified
top-level five-group gate is operator-reported. The quoted
`NO DATABASE MIGRATION...` line belongs to the nested full-regression
quality gate and **does not negate** the separate PostgreSQL and scratch
migration steps in the Phase 2H wrapper. The untracked audit SQL file
`etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`
was left untouched.

**Acceptance:** Phase 2H Financial Foundations as of the exact code
SHA above: **LOCAL TECHNICAL GATE PASS**. This does NOT establish
positive authenticated execution of the broader H4 mid-month variants,
bank-completed external refunds, versioned room-service persistence,
Owner-facing contract export, cross-building end-to-end revocation
and race behavior, or full Phase 2H BUSINESS ACCEPTED. No production
deployment or migration on the application DB.
