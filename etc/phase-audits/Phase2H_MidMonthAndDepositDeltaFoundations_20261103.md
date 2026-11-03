# Phase 2H H2/H3/H4 — financial foundations, 2026-11-03

Branch: wip/phase-2g2. Status: **new code committed, exact-SHA runtime test evidence pending**. Production and real money operations excluded.

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
D03 can hold several independently classified corrections, but may
not archive the SAME issued invoice parent twice within one request
until a future per-correction parent key is introduced.
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
