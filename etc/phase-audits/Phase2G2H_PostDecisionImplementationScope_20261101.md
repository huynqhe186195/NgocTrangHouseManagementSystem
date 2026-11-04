# Phase 2G–2H — post-decision implementation scope and acceptance plan
**Scope version:** 2026-11-01, owner-approved decisions [H1–H6 / OI-01–OI-06](./Phase2G2H_ApprovedBusinessDecisions_20261101.md).  
**Baseline:** SRS `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx` §§3–11 and `wip/phase-2g2` code at `c12e370`.  
**No production actions**; staging only with identified disposable databases. No rewriting already accepted 2G.1/2G.2 functionality.

## Delivery strategy: cohesive batches, not ad hoc isolated code

**Stage A — preserve current baseline (no rework):**
- Retain already-passing month-end atomic 2H transfer: explicit independent policy, signed source electricity/water handover, full old month officially issued/settled, identical new contract rent/deposit, full tenant roster, exactly one immutable carry-forward and linked new active contract.
- Retain full 2G.1/2G.2 implementation, 2G.5 direct-`End` denial and ABP ownership/membership security; no reopening unless new code exposes a failing cross-phase regression.
- Verify current branch and preserve SHA-scoped CI evidence. Previous full + PostgreSQL + Identity successes apply to `c12e370` only.

**Stage B — Phase 2G financial and lifecycle policy foundations:**
1. **OI-03 pricing / adjustment:** establish versioned service-line billing rules with deterministic VND-only immutable snapshots. Ordinary 2G.5 final-month rent remains full. Adjustments to issued invoices are compensating/append-only records with reason, actor, independent approval and stable linkage, not direct updates.
2. **OI-04 debt & deposit:** reconcile authoritative official receivables and accepted receipt reviews; implement specific rent→E/W→service→damage allocation with Owner approval, disputed amount hold and nonnegative unique allocation journal. Reuse `DepositSettlement` and existing source payment evidence—no second uncoordinated deposit ledger.
3. **OI-05 source inspection:** minimal structured inspection evidence (photo OR signed condition report, description/amount/signatures), damage deduction owner approval; keep cash evidence separate.
4. **OI-06 final room disposition:** explicit maintenance-vs-future-commitment resolution state with audit references; no silent reservation cancellation; destination/signed booking and move-out completion must be checked under shared locks and commit-time proof.
5. **OI-01/OI-02 workflow amendments:** implement month-edge tests, Owner exception intake/audit, authenticated Tenant OWN-contract submit/cancel only while Pending, scoped Staff normal approval; preserve strong permission and membership lease/lock at transaction commit.
6. *Gate:* no financial amount is treated as paid or refunded without actual source receipt and independently required review. No old invoice deletion; direct contract end remains denied.

**Stage C — Phase 2H day-proration financial expansion (H2/H3), as one reviewed slice:**
1. Capture signed requested effective date/time, old last chargeable date, new first chargeable date and rate snapshots. A transfer is special—do **not** accidentally apply its day-proration to ordinary full-rent move-out.
2. Design candidate: per calendar month, split an interval into **nonoverlapping day ranges** between source and target. For a month with `N` days, charge `monthlyRent × occupiedDays ÷ N` for each relevant contract; separately itemize services and any approved adjustment. This is a **proposal**, not approved until D01/D02/D03 golden examples and day ownership are signed. Use decimal VND, deterministic rounding with documented residual allocation; no double bill or uncovered day. Preserve source prior issued invoices.
3. Reconcile final source readings and new room meter start readings; ensure usage/price cutoff is exact and effective-dated; no clobber of prior month invoice. Post official receivables only after independent approvals.
4. Extend OI-07 policy record, domain preflight, authenticated staging service and PostgreSQL commit guard **together**; mere exposure of `AgreedDayProration` enum value remains insufficient.
5. Retain SERIALIZABLE, live ABP permission/building membership lock, both-room status/booking locks, anti-overlap and request idempotency.

**Stage D — H4 deposit delta + H6 reduced handover, cohesive with Stage C:**
1. Compute `actual held + verified new top-up - authorized refund - approved deductions = target held` with immutable source→target money trace, distinguish pending instruction vs completed payment and refund. Do not rely on `Contract.DepositAmount == MonthlyRent` as cash proof.
2. For top-up: first require posted/verified actual collection and authorization; for refund: require Owner-approved destination, ledger entry and proof of executed disbursement. Never both refund and carry the same amount. Replays and cross-request races must fail.
3. Implement H6 minimal signed checklist: source final E/W, assets/defects, official debts, deposit split, all-tenant roster and signed physical handover. Never skip original E/W, inspection, any outstanding debt or live occupancy checks.
4. H5 all-tenants remains the only 2H roster policy. **Subset transfer is excluded** from this phase and will require separately approved split-contract, responsibility and deposit allocation design.

## Traceability — source vs approved policy vs implementation gate

| Requirement | Owner direction | Required deliverable | Acceptance ID |
|---|---|---|---|
| SRS §3 OI-01 | Calendar month + month end; Owner documented exceptions | MoveOutNoticePolicy date-edge / reschedule gate | MOUT-POL01–04 |
| SRS §3/§9 OI-02 | Tenant own contract Pending submit/cancel; delegated Staff normal approval | ABP direct/scoped remote security + revocation race | AUTH-POL01–06 |
| SRS §5 OI-03 | Move-out full last rent; transfer special daily price; immutable issued correction | Fee schedule / correction and posted receivable audits | INV-POL01–08 |
| SRS §6 OI-04 | Confirmed debt rent→utilities→services→damage, disputed amounts hold | Authorized offset, immutable deposit journal and audited refund | DEP-POL01–09 |
| SRS §6 OI-05 | Reason + photo/inspection + Owner | Damage evidence and approval | DMG-POL01–05 |
| SRS §7 OI-06 | Maintenance priority, explicit future-reservation resolution | Commit-safe room disposition + no auto reservation rewrite | CMOUT-POL01–08 |
| SRS §8 H1/H5 | linked new contract; all tenants | Keep tested single safe lane, old data frozen | TRF-SAFE01–07 |
| SRS §8 H2/H3 | Mid-month when daily calculation approved | Proration/effective-date ledger; nonoverlapping days; corrected published invoice | TRF-MID01–12 |
| SRS §8 H4 | Verified deposit top-up or partial carry/refund | Money provenance and exclusivity against refund/replay | TRF-DEP01–10 |
| SRS §8 H6 | Reduced handover with essential evidence | Signed minimal checklist + E/W, finance and room condition | TRF-HAND01–06 |

## Required golden business examples before H2/H3 execution

These are **test case requirements, not selected money formulas** until D01–D03 are resolved:
- Source room A 3,000,000 VND/month; target B 4,000,000; transfer on 10th/15th/last day of 30-day and 31-day months and leap-February. Verify old+new chargeable days do not overlap; day-of-handover charged once.
- Transfer on the very first day, day 29 of leap-February, and ordinary end-of-month safe lane: no implicit double charge. Compare invoices before/after transfer with the signed cutoff.
- Existing already-issued source invoice with one settled, one partially paid, one overdue: direct mutation denied; signed compensating adjustment and independently approved receipt reconciliation only.
- Existing security deposit = 3,000,000; target requirement = 4,000,000 and = 2,000,000; full carry, verified 1,000,000 top-up or 1,000,000 refund as appropriate; test failed top-up, duplicate bank reference, duplicate refund and forged payment.
- Multi-tenant source roster: all migrate identically, subset denied; competing target reservation (Reserved, PendingPayment), target Maintenance and competing completion requests denied.
- Damaged source room with confirmed future reservation: cannot silently alter/erase booking; explicit resolution and Room Maintenance evidence. Rollback all related writes on failed preflight/commit.
- Cross-building and revoked account/building-grant scenarios: 403/deny at last authorization check; no data leakage, no cross-DB race that creates a committed transfer after revocation.

## Verification / non-negotiable release gate

1. **Build and unit tests:** domain, application, EF, HTTP host, backwards regressions; no unreviewed warning suppressions. Maintain existing tests and add new IDs per above.
2. **PostgreSQL scratch tests:** real migrations from baseline to latest; checksum, FK/unique, deferred commit triggers, direct SQL bypass negative probes, up/down with audit-history rollback refusal, no destructive commands against unknown DB.
3. **Atomicity/concurrency:** two concurrent complete requests must yield one winner; refund vs carry, reversal vs correction, reservation vs transfer, membership revocation vs mutation, partial failure after any financial write must fully rollback.
4. **Authenticated HTTP:** Tenant own-contract only; Staff vs Owner approvals; denied non-member, removed-grant and cross-building; positive end-to-end with independent source E/W and invoice/deposit evidence; no unsigned client Boolean preflight flags.
5. **Immutable financial history:** source contract, issued lines and tariffs, tenant roster, old readings, deposit payment receipt, adjustment/settlement and completion SHA protected from later SQL tampering.
6. **CI must PASS at exact final SHA:** `verify-2g-security-batch.ps1 -Mode Full`, scratch PostgreSQL full migration/HTTP concurrency gates, isolated Identity membership composition. Passing an earlier SHA does NOT certify a new implementation. Report exact run URLs and counts, or label HOLD.
7. **Production:** NONE. Deployment, feature switch, migration on user DB and real payment/refund are explicitly out of scope; unknown business details = deny, do not invent amounts.

## Deferred — not automatically approved by this decision

- Partial transfer of a tenant subset and splitting responsibilities/deposit.
- Mid-month transfer until day ownership, denominator, rounding and issued-invoice correction are recorded as a versioned signed pricing rule.
- Refund SLA, bank verification specifics, per-service fee proration and dispute release until their evidence/operating details are decided.
- Changing the SRS full-month ordinary move-out rule, any production API exposure, mass database migration or financial disbursement.

## Sequencing / done criteria

- **Current safe lane remains STAGING VERIFIED** only while its exact code SHA passes its real tests.
- **Business direction approved now:** record + trace + explicit detail gates. Documentation is not a new code acceptance.
- **Next implementation deliverable:** finish Stages B–D as cohesive changes, resolve D01–D07 without making financial assumptions, preserve historical invariants, run the full safety suite, commit and report SHA-scoped results. Any still-unspecified formula stays HOLD; do not unblock it by a dummy flag.

## Implementation evidence — Stage B / OI-04 offset preview, 2026-11-01

- Added `ContractDepositOffsetPreviewPolicy` as **read-only domain evaluation** of independently verified, explicitly Owner-approved claims. Its fixed priority is rent, electricity/water jointly, other approved services, approved damage. Within the same priority tier claims have stable-ID presentation ordering, not a newly approved legal creditor priority.
- It rejects negative/fractional VND, duplicated/unverified/disputed evidence, missing policy/Owner approval and unsupported damage evidence. Any blocker suppresses ALL preview amounts instead of partially allocating cọc.
- Its amounts are **proposals**, never posted offsets, payment receipts, bank refunds or mutations. Both `CanPostDebtOffset` and `CanExecuteRefund` remain false, and the existing `ContractDepositSettlementDecisionRecord` continues to deny nonzero outstanding debt. No legacy closeout gate was relaxed.
- Added `DEP_POL01–DEP_POL12` domain tests and wired Domain/DepositSettlements changes to scratch PostgreSQL workflow triggers for full cross-phase regression. Await exact-SHA CI evidence before marking this implementation PASS.
- **Still HOLD**: assemble claims from official ledger with persisted owner decision and dispute status; atomic signed `DepositSettlement` offset allocation/write; source receipt and refund proof; PostgreSQL rollback/race and authenticated HTTP; D04 refund verification/timing. This preview is not Phase 2G.4 full acceptance.

- OI-04 same-priority ambiguity: owner approved E/W jointly but not order of individually partially funded claims. `DEP_POL13` forbids silently allocating part of an underfunded priority tier with multiple claims; `DEP_POL14` proves a single partial claim is arithmetically unambiguous. Owner must explicitly sign any future intra-tier allocation policy before write authorization.

## OI-04 continuation — persisted read-only financial composition (2026-11-01)

- An internal \`ContractDepositOfficialOffsetReadAppService\` now reads an **approved move-out** and all source deposit payments/settlements through the existing deposit auditor, plus all monthly issued invoices, official postings, month archives/reviews, invoice receipts (including soft-deleted), both payment journals, bank reviews, adjustments and existing deposit decisions under **REPEATABLE READ** with the existing isolation guard.
- It only returns monthly charge-category evidence when every monthly invoice is officially posted, has an immutable verified source and is completely unpaid **with zero payment/correction activity**. Any final invoice, missing posting, anomalous deposit ledger, incoming/outgoing disposition, payment, journal, correction or future-dated posting fails the **whole contract** closed.
- Its output deliberately has \`CanPostOffset = false\` and \`CanRefund = false\`; it has no remote endpoint or financial writer. Owner dispute approval / refund instructions / actual bank verification are **not** implied by a posted invoice.
- Added \`DEPREADER01–03\` and \`DEP_BRIDGE08\` guards. Remaining: independently persisted dispute decisions, verified receipt-to-invoice-line allocation for partly paid/corrected bills, authorized atomic offset/refund writer and PostgreSQL commit-time evidence protections. Do not mark the full OI-04 phase BUSINESS ACCEPTED yet.

## Phase 2G.4 signed per-charge monthly receipt journal — 2026-11-01

- Added source-only \`OfficialMonthlyReceiptLineAllocationRecord\` + immutable EF configuration + PostgreSQL migration \`20261101010000_Phase2G4OfficialMonthlyReceiptLineAllocations\`. Each journal line is keyed to **one official monthly posting + independently accepted bank receipt review + original payment journal**, and carries a distinct signed allocation actor/time/reference.
- PostgreSQL protects original invoice/receipt joins, rejects forged categories and overspending the paid amount or frozen original charge, serializes concurrency via invoice/review row locks, requires a **full exact split per receipt at deferred COMMIT**, and blocks history UPDATE/DELETE. Migration downgrade refuses if signed history exists.
- The existing OI-04 reader reconstructs actual \`OfficialInvoiceReceiptEvidence\` from original Payment + immutable monthly journal, requires all original receipt reviews to match the known monthly postings and uses signed line amounts to classify the remaining unpaid categories. Missing, rejected, future-dated, duplicated or incomplete splits fail the **whole contract** closed.
- \`DEP_LINE01–07\` and \`OI04SQL01–04\` cover accepted partial payment, double-use, archived charge limits, independent review, SQL tampering, deferred completeness and safe rollback.
- **DO NOT interpret as a financial writer:** receipt verification is human-attested, signed references are audit pointers rather than cryptographic signature verification, and this code has no authorized debt-offset/refund API. Current 2G.5 closeout still requires all debts fully paid. Remaining work: authorized dispute status, documented bank evidence validation, Owner-committed priority/partial-tier policy, atomic real offset posting with same-transaction debt decrease and held-cash decrease, refund proof and concurrent replay tests.


## 2026-11-04 — Revised implementation and acceptance scope (binding Owner feedback)

This section **supersedes conflicting future-scope proposals** earlier
in this plan; already accepted H1–H6/OI-01–OI-06 and passed historical
Phase 2H Financial Foundations are not reopened.

### One cohesive delivery instead of disconnected feature flags

**2H-DELTA-A — Room-specific service tariff and contract snapshot**
- Introduce versioned RoomId × ServiceId tariff records, authorized
  editor, unit (per person / per room / metered / other approved basis),
  whole-VND price, validity window and immutable audit/EF change
  history. Different rooms are allowed different prices for the
  same service.
- Validate nonoverlapping active versions for each room/service,
  current room/building membership, and required metered readings.
- An issued invoice binds the exact room-specific service version,
  number of people if applicable and price snapshot; changing a room
  price later cannot rewrite a historical charge. Versioned effective
  dates and future contracts are explicit.
- Upon transfer, source and target service lines use their own
  respective room tariffs/verified headcounts for their actual
  periods; do not charge two rooms for one day of a service when the
  approved service-charge proration applies. The room price is
  editable; the charging algorithm is **not** implicitly always
  prorated. Add per-service billing mode/effective policy.

**2H-DELTA-B — H4 deposit receivable and exclusive surplus outcome**
- Change the prior proposed "no transfer until top-up paid"
  precondition. Permit the signed H4 room-transfer transaction to
  finish when the target deposit requirement is larger than actual
  source cash; carry only the verified amount and atomically create
  a **separate original outstanding deposit receivable**.
- Local `DateOnly` maximum due date is one calendar month after
  transfer, with month-end clipping. Do not infer that the uncollected
  difference is cash-backed. Record pending, partial, collected,
  overdue and independent receipt verification. On overdue, keep
  the debt visible and follow an authorized collection workflow;
  never silently undo or erase the completed move.
- When target required deposit is lower, carry exactly the target
  requirement; the remainder is a tenant liability to discharge
  by one immutable, Owner-approved **alternative**:
  **NextRentCredit** or **ManualRefund**. No automatic choice.
  - `NextRentCredit`: credit the **following month's genuine rent
    receivable**, creating an immutable allocation and preventing
    second cash refund or application against a different tenant.
    Remaining credit not yet applicable to issued rent remains
    unspent tenant credit; never fabricate a payment.
  - `ManualRefund`: use authorized recipient, bank statement,
    signed Owner approval, independent reviewer and distinct
    external settlement reference. Track request, pending/failed,
    retry and paid separately; no payout without real external
    proof. Apply the accepted seven-business-day refund target
    from approved reconciliation with a signed VN holiday calendar.
- Invariants: source actual held = carried target deposit + remaining
  tenant-credit/refund liability + any **previously posted approved**
  deductions; target required = actually carried + outstanding
  top-up; each monetary unit can be consumed only once. Original
  issued invoices never rewritten.
- New SQL/EF journals must have source and target contract FK,
  request idempotency, unique cash/bank references, immutable
  audit, serializable commit-time assertions and same-transaction
  authorization/membership checks. Distinguish authorizing a transfer
  from authorizing a later cash posting.

**2H-DELTA-C — Owner contract document export and D06**
- An authorized Owner may export the *new finalized contract* from
  canonical persisted contract/tenant/room/rent/service/deposit
  evidence and the original old→new contract link. No client
  financial fields are trusted for PDF generation.
- Capture a signed/attested three-party handover from Owner,
  authorized Staff, and primary tenant or documented representative.
  Handwritten signed document attachment is acceptable; persist
  digest/version, actor and UTC upload evidence; verify current
  authorization and all roster/meter/asset evidence. Never represent
  a text SHA as cryptographic legal e-signature.
- Scope contract download by building and role, protect tenant
  personal data, audit every document export, deny foreign building,
  revoked Owner permission and pre-finalized versions.

**2H-DELTA-D — Exact tests before BUSINESS ACCEPTED**
- D02 whole-VND 0.5-up rounding for 28/29/30/31-day cases;
  `Asia/Ho_Chi_Minh` day boundary and DST-independent fixed +07
  effective date. Continue explicit signed per-request day owner:
  owner's latest answer establishes timezone but did not explicitly
  replace per-request day ownership with a universal rule.
- `TRF-SVC01..08`: same service different prices in room 301/302,
  editable version, per-person roster, no retroactive issued amount,
  unauthorized foreign-building edit, boundary overlap rejection,
  real meter reading, different service billing modes.
- `TRF-DEP-DEFER01..12`: approved transfer with pending top-up,
  local one-calendar-month due date edges, partial/late/duplicate
  payment, no fictional held cash; exclusive next-month rent credit
  vs refund, no double-use, source/target financial reconciliation,
  rollback and request races.
- `TRF-CONTRACT-DOC01..06`: Owner authenticated export of finalized
  linked contract, correct specific room tariff snapshots, required
  signatory evidence, no pending/unfinalized export, role revocation,
  cross-building data confidentiality.
- Positive **authenticated** mid-month transfer against real
  disposable PostgreSQL, multiple valid contracts; SQL trigger
  adversarial writes, retries and actor revocation at commit; full
  migrations, Domain/Application/EF/HTTP and regression at **same
  final code SHA**.
- Never replace test PASS with a source-only grep or deliberately
  weakened negative fixture. All migrations in disposable local DB
  only; production remains out of scope.

### Open precision — safe implementation decisions without blocking drafts

1. **RESOLVED on 2026-11-04:** Owner confirmed the **TARGET room**
   owns the handover civil date in Vietnam. Source room ends billing
   the previous calendar day. New D01 signing and posting must enforce
   this global rule. Stale signed `TransferDayOwner=Source` records
   remain immutable history but must be rejected by the new completion
   flow, not retroactively overwritten.
2. Room-specific service rate is approved; **per-service billing
   proration/fixed-fee mode** must be configured explicitly before
   using it to alter amounts.
3. Template/content of the generated new-contract document remains
   a versioned owner-configurable template. Until implemented,
   no mock document may be presented as a signed executed contract.

The 2026-11-04 five-group foundation PASS applies ONLY to
`e24894f3b830a5640c2e7a5ba1857c0911579c2d`.
Future implementation commits require their own full run.


### D01 FINAL acceptance matrix — Owner confirmed target-day policy (2026-11-04)

| Month/transfer date (Vietnam civil day) | Old-room days | New-room days | Required behavior |
|---|---:|---:|---|
| 30-day month, day 15 | 14 | 16 | Day 15 belongs only to target |
| 31-day month, day 15 | 14 | 17 | Day 15 belongs only to target |
| Leap February, day 29 | 28 | 1 | Correct leap-day denominator |
| February non-leap, day 1 | 0 | 28 | Target begins at first of month |
| 31-day month, day 31 | 30 | 1 | Last-day transfer target charged once |

**Required code verification:** the existing quote calculator and
versioned pricing candidate must compute these values; new staging
execution must require `TransferDayOwner=Target` and the matching
exact Owner signoff. SQL deferred completion gate must independently
verify this field and the target contract effective date rather than
trust a client's choice; confirm no double rent on issued target
invoice. Previous source-day candidates are archived but fail new
execution. Run negative tests for source-day override, UTC midnight
boundary, leap-year edge and double billing; run authenticated
PostgreSQL positive execution and full regression at final code SHA.
This table changes business policy only until the code/SQL/test gates
have been implemented and passed.
