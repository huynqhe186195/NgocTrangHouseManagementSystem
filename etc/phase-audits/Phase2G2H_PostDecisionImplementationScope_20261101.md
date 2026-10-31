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
