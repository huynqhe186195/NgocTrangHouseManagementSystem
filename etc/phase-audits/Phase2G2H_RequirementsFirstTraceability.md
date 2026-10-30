# Phase 2G.1–2G.5 and Phase 2H — requirements-first implementation ledger

**Authoritative source:** `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx`, *Business Process Specification – Phase 2G & Phase 2H*, v1.0, 22 October 2026, §§1–11.

**Development branch:** `wip/phase-2g2`. This document compares present branch code with the source business requirements; **Slice A/B/C/D are implementation/security workstreams, NOT substitutes for the six business phases.** Test evidence from earlier commits does not certify a newer commit. No production deployment.

## Permanent phase order and gates

| Business phase | Document's expected behavior | Code / test evidence observed | Residual gap or decision | Delivery label |
|---|---|---|---|---|
| **2G.1 Move-out Request** (§3; MOUT01–06, MOUT-PG01) | Active contract, one open notice, Vietnam one-calendar-month + month-end, submit/approve/reject/cancel, 5 initially-open checklist items; approval NEVER ends Contract, frees Room or refunds deposit | `MoveOutRequestAppService`, `MoveOutNoticePolicy`, `MoveOutRequestAppService_Tests`, `MoveOutRequestConcurrency_Tests`, scoped Slice B staging route. MOUT01–06 and MOUT-PG01 explicitly cover core invariants; EF context maps MoveOutRequest and Checklist and migration exists. | OI-01: exceptional notice date changes/early move-out; OI-02: Tenant self-service ownership, cancellation cut-off and Staff/Owner approval authority are **not approved**. Keep unreviewed remote access denied. | **Core implemented; owner/staff staging covered, policy/tenant gates open** |
| **2G.2 Meter & Pricing** (§4; METxx) | Room meters and replacement history; valid monthly/final readings; effective prices and frozen historical invoice charges | UtilityMeter/MeterReading/UtilityRate domain, replacement + integrity migrations, monthly usage/coverage checks, final-meter evidence preflight, 5 UtilityRate and 9 Meter + 4 Reading scoped staging routes. | PostgreSQL staging HTTP is currently RED at commit `80c61400` due to raw SQL mistakenly referencing unprefixed `Rooms`/`Floors`; corrected in follow-on commits. Re-run CI and diagnose further failures; no automatic prorating or tiered billing until cutoff/tariff rules are approved. Source-import/photo/final-evidence fields need a business-data decision before expanding schema. | **Implemented in part; current 18-route CI gate pending** |
| **2G.3 Invoice & Debt** (§5; INVxx) | Monthly plus final invoice (rent, electricity, water, internet, trash, parking, other contracted services), immutable prices/lines, paid/partial/overdue/outstanding, audited adjustments | Draft snapshot+revision and final invoice evidence/approval; staging-only issued-invoice ledger, manual receipt allocation and PostgreSQL immutability/closeout guards. `InvoiceStatus` currently models Draft while separate issued-ledger models a candidate issued record. | Complete recurring-invoice/debt lifecycle and permitted service fees, due/overdue logic and corrections; reconcile two models and test every payment state. Final-month **full rent** is the stated SRS rule, but non-rent proration is OI-03. Do not treat a draft or manual evidence claim as a settled external payment. | **Partial staging, not full §5 acceptance** |
| **2G.4 Deposit Settlement** (§6; DEPxx) | Held deposit, validated compensation with reason, no double refund/carry-forward, authorized debt offset, refundable/remaining amounts | Existing `DepositSettlement` entity and closeout approval/immutability migrations; Slice B closeout writes signed manual-refund/forfeit ledger in one UoW and validates receipts. | OI-04/05: debt-offset scope/order, approval and dispute handling, required damage evidence, refund method. Current staging requires invoices reconciled before closeout and only attests manual bank transfer; it is NOT a decision on all debt offsets or independent bank verification. | **Partial staging, policy pending** |
| **2G.5 Complete Move-out** (§7; CMOUTxx) | Approved MoveOut + final readings, inspection, final invoice/debt, deposit and handover, checklist; atomic End Contract, Complete request, select Room Available/Reserved/Maintenance | Staging-only `SliceBStagingFinalInvoiceMutation.CompleteMoveOutAsync` executes financial closeout, five checklists, Contract.End and Room transition in one UoW; direct `ContractAppService.EndAsync` bypass is disabled. PostgreSQL authenticated HTTP positive closeout rehearsal exists. | **Confirmed discrepancy:** closeout currently refuses ANY `PendingPayment`/`Reserved` future reservation and chooses only `Maintenance` based on `ForfeitAmount>0` or `Available`, never `Reserved`. §7 calls for Reserved when a valid future commitment exists. Define disposition of overlapping/future reservations and damage separately (OI-06), then implement and test race/rollback. Also reconcile signoffs completed *in* the closeout transaction against prerequisite checklist evidence. | **Core staging implemented, §7 room-disposition mismatch** |
| **2H Room Change / Transfer** (§8; TRFxx) | Atomic transfer request, destination eligibility, old room final readings/inspection/debt, deposit carry-forward, preserved old/new contract history, both room states | `DepositSettlementType.RoomChangeCarryForward` and `ContractTransfer` exist as domain vocabulary. No complete RoomChangeRequest/RoomChange orchestrator/API/tests found on this branch. | OI-07: old/new contract policy, mid-month pricing, deposit difference and occupants to transfer. Do not fake this as setting Contract.RoomId or reusing a settlement enum. After approved policy, implement complete transaction and TRF tests. | **Not delivered** |

## Non-negotiable preserved requirements

1. **Approval is not departure.** Phase 2G.1 approve must not end Contract, release Room, post invoice or refund cọc; only phase 2G.5 can finalize.
2. **Reservation deposit first receipt:** exactly VND 500,000 or 100% RequiredDepositAmount — no intermediate initial payment (§2.1, §6.1).
3. **Actual held contract deposit** persists during tenure; deduction above zero requires documented damage (§2.1, §6.2).
4. **Transaction and stable lock order** across Contract / Reservation / Payment / Settlement; idempotent complete/refund/carry-forward/payment callback; PostgreSQL concurrency tests (§9.2).
5. **Asia/Ho_Chi_Minh business dates, UTC timestamps** and an approved schema/migration plan; no migration on unidentified/production DB (§9.2).
6. **Owner/Staff resource-scoped ABP grants; Tenant access only to own contract**; no PII from unscoped routes (§9.2).

## Decision register: not silently approved

| Code | Business decision in source §11 | Safe engineering state |
|---|---|---|
| OI-01 | Calendar-month edge cases, early departure and exceptions | Current deterministic `DateOnly.AddMonths(1)` then month-end remains; no special-case bypass |
| OI-02 | Tenant self-service and Staff vs Owner approval | Keep unscoped routes denied; Tenant permissions not assumed |
| OI-03 | Other fees for incomplete final month and audited correction | Do not invent proration or automatic charge |
| OI-04 | Offsettable debt and ordering/approval/disputes | Do not silently auto-offset all debt |
| OI-05 | Images, condition report and approval proof for damages | Require explicit evidence before relaxing financial safeguards |
| OI-06 | Damaged room with a confirmed future reservation | Needs explicit room disposition + reservation conflict workflow |
| OI-07 | Transfer contract model, mid-month charges, delta deposit, occupants | Hold 2H final workflow until policy defined |

## Immediate execution and verification steps

1. **Verify 2G.1** against MOUT01–06, MOUT-PG01, current scoped staging authorization, dates/checklist, direct-End deny. If matching, do not rewrite; keep only OI-01/02 work open.
2. **Unblock 2G.2 first failing test**: raw PostgreSQL lock queries in `SliceCStagingUtilityMeters` and `SliceCStagingMeterReadings` must use actual `BuildingManagementRooms` and `BuildingManagementFloors` (the `Buildings` table has no prefix). Run actual Identity, Full regression and disposable PostgreSQL authenticated HTTP CI at final commit; fix next first failing assertion before declaring PASS.
3. **2G.3** finish recurring and final invoice/service/debt lifecycle; do not confuse immutable candidate issuance or manual receipt with fully settled finance.
4. **2G.4** finish approved offset/disposition workflows without double-spend or pretending bank proof is independently verified.
5. **2G.5** close Reserved/Maintenance discrepancy and attest all checklist prerequisites, then CMOUT HTTP+PG rollback/race tests.
6. **2H** implement only after OI-07 decisions, then TRF scenarios, deposit exclusivity and atomic source/destination updates.

**Acceptance labels:** IMPLEMENTED (code exists), STAGING VERIFIED (actual current-SHA tests pass), BUSINESS ACCEPTED (source rules satisfied, open decisions closed), PRODUCTION ENABLED (separate authorization, not part of current mandate). These are not interchangeable.

## Phase 2G.3 continuation — 30 October 2026

**User-provided delivery rule:** Phase 2G.1 and 2G.2 were already accepted
in previous conversations. Do not restart their implementation/tests or
reopen completed work merely because the October 22 document contains an
older status. Any genuinely new cross-phase regression is still a defect to
address in the currently edited phase, without rewriting accepted features.

**Current work branch and changes (NOT a claim of full Phase 2G.3 completion):**

- `572c2854`: `IssuedInvoicePaymentReconciler` computes completed
  receipts **as of the Vietnam business date**, not from future receipts,
  and rejects ambiguous non-UTC completion timestamps. Four new domain cases.
- `64d031d5`: `IssuedInvoiceLedgerRecord.VerifyStoredTerms` checks
  archived auxiliary fee line totals, multiplication, valid categories,
  evidence references and per-service uniqueness. Existing EF test now
  covers tampered archived terms with recalculated SHA-256.
- `cb8551aa`: read-only staged invoice-debt evaluator and application
  reader. It loads the persisted issued ledger + original evidence/review,
  all contract invoice receipts (including soft-deleted), and allocation
  journal under a consistent transaction; blocks any missing/mismatched
  evidence. Seven domain scenarios were added.
- `97aa4fac`: regression checks assert the new reader is **not remote**
  and declares `RepeatableRead`. This is an internal/staging calculator;
  even a clean mathematical balance is **not posted debt**, a bank
  verification, deposit offset or refund permission.

**Verification:** `64d031d5` completed GitHub CI successfully.
The follow-up changes require CI verification at their **new final SHA**;
do not claim them PASS from the older run. No PostgreSQL test database
migration, production release, or full financial issuance acceptance
follows from these changes.

**Still required for Phase 2G.3 BUSINESS ACCEPTED:** a complete recurring
invoice issuance path, pricing approval for ancillary charges, audited
post-issuance corrections, authorized debt/payment posting, and tests of
all official invoice states with current-SHA PostgreSQL security/concurrency.
Only after resolving those prerequisites should 2G.4 deposit offset and
2G.5 final closeout depend on the official debt ledger. Phase 2H requires
the source §8 contract/transfer policy decisions before its orchestration.
