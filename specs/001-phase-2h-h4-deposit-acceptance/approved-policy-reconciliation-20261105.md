# Feature 001 — binding owner-policy precedence and delivery crosswalk

**Reconciled:** 2026-11-05; author-side source/CI review, not Owner acceptance of code or Constitution.
**Controlling policy:** [Phase 2G–2H approved business decisions](../../etc/phase-audits/Phase2G2H_ApprovedBusinessDecisions_20261101.md), especially the **2026-11-04 additional decisions and final D01 confirmation**.
**Implementation baseline:** `wip/phase-2g2` at `a9519996c06d42db880cec4a741036a2e520e460`; combined PR #5 reviewed at `7179cfacea658790582802b4397a532b7411629f`.
**Safety:** no production, live bank, live customer funds, migration, feature activation or application financial-writer modification.

## Source precedence

1. The SRS `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx` and explicit H1–H6/OI-01–OI-06 owner approvals remain authoritative.
2. The **later owner addendum of 2026-11-04 supersedes 2026-11-03 candidate-only HOLD statements** for D01/D02/D03/D04/D06; keep those old artifacts as dated historical evidence.
3. An Owner's rule approval is not code implementation, an independently verified receipt, bank disbursement, business acceptance, or release. The 2G.4 ordinary-manual-refund addendum is not automatic proof of a 2H-H4 refund.
4. Feature 001 is H4 evidence/intent reconciliation. The new Owner export requirement and broad per-room tariff subsystem need a separate, scoped implementation spec/PR; do not smuggle them into this test-only PR.

## Decision-to-implementation gates

| ID | Approved and must **not** be reopened | Required technical/financial acceptance; **not yet certified by this Spec Kit PR** |
|---|---|---|
| D01 | Transfer uses Vietnam civil date; **target owns date D**, source ends D−1; physical UTC instants are audit metadata. | Re-signed target-day policy for new execution; reject legacy source-day candidates at new transfer; no overlapping days; positive/negative HTTP + PostgreSQL SQL parity. |
| D02 | Charge each room for its calendar chargeable dates using that month's 28/29/30/31 days; round each nonnegative rent line to whole VND, with 0.5 VND rounded up. | Signed immutable day/rate snapshots; boundaries 1st/last/Feb leap/Jan31/Apr30; exact expected VND, residual/no duplicate day, prior issued invoice unchanged. |
| D03 | Each Room+Service has its own effective-dated, versioned VND tariff and billing unit; independent editor/audit; per-person usage uses verified occupants and meter charges real readings. | RoomId+ServiceId+unit/rate/version effective-date migration, immutable issued snapshot, independent correction/receipt link and **explicit fixed-vs-daily rule for every non-metered service** (no invented universal rule). |
| D04 positive top-up | Transfer may **complete even when positive top-up is unpaid**; create separate outstanding deposit receivable due **no later than one Vietnam calendar month after D**, not fake held cash. | `RequiredDeposit` versus `CashBackedHeld` versus `OutstandingTopUp`; due-date edge/clamp, partial/late collection, independent bank/receipt review, immutable posting, SQL-bypass and replay/rollback. Never silently undo a completed transfer for overdue debt. |
| D04 excess | Owner selects **exactly one** disposition: credit the **next month's authentic issued rent**, or refund to the tenant through independently reviewed bank/manual evidence. | Separate pending instructions from financial executions; one credit against original issued rent (without rewriting its lines), approved beneficiary/bank ref, dual review, no duplicate refund/credit across contracts; test the proposed H4 7-business-day refund target rather than misstate it as completed payment. |
| D06 | Signed handover requires **Owner, handing-over Staff and primary tenant/authorized representative**; signed paper scan/upload accepted; cryptographic e-sign integration not required now. | Bind independent actor identities, roster, attachment digest, E/W readings, condition/assets/defects and signoff to commit-checked proof; authenticated third-party forgery denial. |
| Owner contract export | Owner can export new contract as a **file** while retaining old contract and linkage. | Scope-checked PDF/file export of canonical finalized contract, price/service snapshots, document number/version, auditable authorized download and confidentiality. Separate feature; not produced in Feature 001. |

## Financial safety acceptance examples (must be added/tested on the implementation SHA)

- **D01/D02:** 30-day handover on 15th yields source 14 days and target 16; 31-day handover on 15th yields 14/17. Handover on first yields source 0 and target N; last yields N−1/1. Verify billing-unit arithmetic for February 28/29, month-end and each independently rounded VND line.
- **D04 top-up:** Source held 3,000,000 VND, new target needs 4,000,000: carry 3,000,000, record *unpaid* 1,000,000 separately with max one-calendar-month deadline; then prove partial/full/late independent payments and refuse duplicate proof.
- **D04 excess:** Source held 3,000,000 VND, target needs 2,000,000: carry 2,000,000; **either** reserve 1,000,000 for next month's authentic issued rent **or** retain a 1,000,000 refund liability until approved bank settlement. No double disposition, even under races or SQL tampering.
- **D03/D06:** Two rooms have distinct service rates for the same ServiceId; freeze original invoice snapshots on new tariff version. Refuse unsigned or forged Owner/Staff/Tenant upload, stale/revoked building grant and cross-building file access.

## Evidence scope at audited integration head

- Draft PR #5 HEAD `7179cfacea658790582802b4397a532b7411629f` is a compatible union of PR #3 Spec Kit docs and PR #4 test-only bounded H4 permission/membership race. Four **exact-head** GitHub workflows reported success: [H4 PG](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41997785666), [Slice A PG](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41997785826), [Windows safe](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41997785836), [Identity](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41997785977).
- Job logs showed H4 authenticated HTTP 1/1 with its multiple internal scenarios, migration 1/1, PostgreSQL H2–H6 103/103; Slice A Domain 162/162 and PG 103/103 plus authenticated HTTP 1/1/migration 1/1; Windows selected H4 Domain 165/165, full Domain 712/712, Application 183/183, EF 447/447, HTTP 76/76. The selected Domain cases overlap the full suite. Do not add test counts or claim 3 independent H4 HTTP tests.
- **These tests cover protected deferred H4 obligations, transfer atomicity and bounded Identity/Building revocation; not actual top-up cash collected, rent credit applied or bank refund paid.** Source audit and CI logs are not independent TRX XML extraction or human acceptance.
- Any change after `7179cface` needs fresh current-head CI. Old run links remain proof of the old head only.

## Next implementation package (separate PR)

Implement **one coherent, reviewable D01/D02/D03/D06-and-H4 settlement slice only after analyzing existing source**; do not rewrite the H4 pending-intent/completion records. Build the exact bank-reviewed receipt/credit/refund posting and snapshot/role controls together with authenticated positive/negative, cross-building, serializable race, SQL direct bypass, rollback and real migration-chain tests on disposable allowlisted PostgreSQL. Treat unresolved per-service billing-unit/compensating-invoice details as explicit gates, never inferred authorization.

**Status:** D01/D02/D03/D04/D06 owner decisions = RECORDED/APPROVED (within their defined scope); implementing/verifying them and external money settlement = PARTIAL/HOLD; proposed Constitution = pending Owner review; business acceptance = HOLD; production = FORBIDDEN by this work.
