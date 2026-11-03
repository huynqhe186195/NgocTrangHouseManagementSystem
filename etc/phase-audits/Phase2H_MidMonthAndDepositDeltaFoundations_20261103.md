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
