# Ngọc Trang — Phase 2G–2H approved business decision register
**Decision status:** BUSINESS DIRECTION APPROVED (NOT CODE ACCEPTANCE)  
**Approved in project conversation:** 2026-10-31 19:17:57 UTC / **2026-11-01 02:17:57 Asia/Ho_Chi_Minh**  
**Authority:** Project owner, explicit selections H1–H6 plus acceptance of the previously proposed OI-01–OI-06 directions.  
**Authoritative baseline:** `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx` (v1.0, 2026-10-22), especially §§3, 5, 6, 7, 8, 9 and 11.  
**Branch:** `wip/phase-2g2`; **production:** excluded.  
**Companion plan:** [Post-decision implementation scope](./Phase2G2H_PostDecisionImplementationScope_20261101.md).

## Status definitions

- **APPROVED PRINCIPLE**: owner has selected the business outcome/choice; no additional yes/no on this principle is needed.
- **DETAIL GATE**: exact arithmetic, evidence, statutory/contractual precedence, user permissions or operational SLA not settled by the above choice. Explicitly document and validate it; deny unsafe operations meanwhile. A proposed algorithm is **not** an approved formula.
- **IMPLEMENTED SAFE LANE**: existing limited staging code passes its isolated tests; broader approved behaviors are not implied.
- **BUSINESS ACCEPTED** requires implementation plus tests for the approved scope. Earlier CI PASS is neither approval of broader behavior nor a reason to enable remote/production APIs.

## Phase 2H — owner-approved H1–H6 (OI-07)

| ID | Approved business policy | Present implementation vs future scope | Detail gate / non-negotiable |
|---|---|---|---|
| **H1** | End source contract with `RoomChanged`; **create a new contract**, preserve source history and explicit old→new link. No versioned addendum as the normal transfer mechanism. | **Already implemented for safe lane** via linked new contract / atomic room transitions. | Never mutate historical invoices, rent, meter evidence, tenants or settlement on the source. |
| **H2** | Permit **mid-month transfer after** an approved and tested between-period pricing rule exists; not immediately enabled by this decision. | Current staging supports **month-end only**. | Until H3 details are implemented and evidenced, leave other dates fail-closed. |
| **H3** | For a mid-month transfer, allocate **rent by actual calendar days of use** in each room; do not blindly charge a full month in both rooms. | Current safe lane charges the whole old month, begins the new contract on the following day. | Confirm charge ownership of handover date, month-length denominator, edge dates, rounding to VND, old/new effective tariffs and effects of already-issued invoices. The tentative candidate formula is NOT approved yet. **The SRS full-final-month rent rule remains in force for ordinary 2G.5 move-out**, pending explicit exception language for transfer. |
| **H4** | Allow proven **deposit top-up** for a higher required deposit, or **partial carry-forward plus verified refund** for a lower one, after financial reconciliation and authorization. | Safe lane permits only full carry-forward of same held amount; no price/deposit delta. | Do not represent intended money movement as collected/refunded funds. Keep provenance, audit references and one exclusive disposition per currency unit; refund method/timing and payment confirmation remain detailed gates. |
| **H5** | **All current tenants move together**; a subset moving requires a **separate split-contract workflow**, outside present 2H acceptance. | Existing snapshot and exact-copy roster align with this choice. | Keep original tenant history immutable; target must match the verified, consented roster. |
| **H6** | Use a **shorter transfer handover checklist**, but retain essential signed evidence. | Existing signed source E/W readings, inspection references, debt and deposit checks remain mandatory. | Minimum: final source electricity/water readings, room/assets/defects, arrears and invoice reconciliation, deposit disposition, actual source/target occupant roster, signed handover + actor/time/reference. Do NOT bypass final metering, finance, maintenance conflict or audit checks. |

**OI-07 status:** H1/H5 and the existing restricted H2/H3/H4/H6 lane are implemented and tested; the *future choices* are approved principles, not full implementation. New policy choices must have their own evidence and serializable SQL commit guards. The `RoomChangeBusinessPolicyRecord` enum having values for day-proration/top-up/refund does NOT mean their execution is implemented.

## Phase 2G — owner-approved OI-01 through OI-06

| ID | Approved direction | Implementation boundary / specific details |
|---|---|---|
| **OI-01** — notice | Normal move-out: one **calendar month** of notice, return at **calendar month-end**. The deterministic policy `EndOfMonth(submissionDate.AddMonths(1))` is the baseline candidate for 29th–31st (e.g. 2026-01-31→2026-02-28); early departures/date changes require **Owner-approved documented exception**. | Preserve the source §3 one-month minimum and month-end baseline. Date changes after approval cannot silently change financial terms or bypass ownership checks; test leap years, 29/30/31 and rescheduling. Exception pricing/approval details stay gated. |
| **OI-02** — authority | Tenant may submit own request and **cancel while Pending**; an authorized, building-scoped **Staff** may approve ordinary compliant requests; **Owner** approves exceptional early/date changes. | An Approved request cannot be casually self-cancelled. Only current account, scope, role and contract tenant relationship grant access. Current staging root/owner-only or deny-all routes must NOT be opened by editing docs. Independent financial approvals continue to require their existing stronger rules. |
| **OI-03** — invoice | **Ordinary move-out** retains full rent for final month per SRS. E/W use actual legitimate readings; Internet/trash/parking/other services follow **their individually authorized contract/pricing policy**. Post-issuance correction must be append-only/audited, never edit or delete the original issued invoice. | Mid-month **transfer** uses H3 as a separate, explicitly versioned exception, not a silent change to 2G.5. Per-service proration vs fixed billing and VND rounding/effective dates still need precise tariffs. Never confuse a manual receipt claim with bank verification. |
| **OI-04** — debt/offset | Only **confirmed legitimate receivables** and **Owner-approved compensation** may reduce held deposit. Priority proposal accepted as business direction: **unpaid rent → electricity/water → other approved services → approved damage**. Disputed/unverified amounts are excluded from automatic offset; **Owner final authorization** required. | No automatic offset of all open invoices. Record each allocation and residual amount; do not invent completed bank transfers. Refund method, bank-proof requirements, timing, disputed-amount custody/release require explicit operating details before enabling novel paths. |
| **OI-05** — damage | Any damage deduction >0 requires **clear reason, amount, photo OR signed inspection/condition report, and Owner approval**. Zero-damage case must not demand fictitious damage evidence. | Image alone is not a cash approval. Require signed actor/time/evidence and immutable references. Maintenance eligibility and compensation are distinct decisions (OI-06). |
| **OI-06** — reservation vs maintenance | **Safety/maintenance takes precedence** over making a damaged room available. When a confirmed future Reservation/Signed Contract exists, resolve it by an **explicit, audited reassignment, reschedule or authorized cancellation**; never silently delete or override a tenant commitment. | A conflicting maintenance + committed future booking remains **HOLD** until a valid booking resolution is recorded. Existing `MoveOutRoomDispositionRecord` already fail-closes this collision; the coordinated resolution workflow is not silently approved as delivered. Keep `Available`/`Reserved`/`Maintenance` based on verified source facts and commit-time guards. |

## Reconciliation with source SRS — explicit exceptions, no silent precedence

1. **§3 and §11 (OI-01/02):** the minimum notice and five move-out checklist obligations remain. Tenant self-service and delegated staff powers are a **new permission-delivery requirement**, not a license to expose unscoped API methods.
2. **§5 full final month vs §8 mid-month transfer:** there is a deliberate **potential conflict**. Interpret the accepted H3 as a **transfer-only pricing exception**, requiring explicit signed/versioned rule and audited itemization before use. If SRS text is treated as universal, update the SRS addendum first. No double rent and no rewriting previously issued charges.
3. **§6 deposit provenance:** preserve first Reservation Deposit receipt requirement (exactly VND 500,000 or full required deposit), actual paid contract deposit, exclusive carry/refund, reasoned compensation. H4 extends, never weakens, this invariant. The current `Contract.DepositAmount` is linked to monthly rent: deposit-delta work must reconcile actual held money with new contract required amount, not infer a transfer from the model property alone.
4. **§7 CompleteMoveOut:** approved notice alone never ends tenancy; complete only with E/W, final invoice, settled financial state, deposit and signed handover in a transaction. OI-06 maintenance with future booking stays fail-closed pending separately audited reservation resolution.
5. **§8 RoomChange:** H1/H5 preserve linked history/all occupants; H6 reduces only redundant steps, **not** evidentiary requirements. An authorized policy proposal and a successful immutable completion remain separate states.
6. **§9 technical/security:** Asia/Ho_Chi_Minh `DateOnly` business dates, UTC instants; stable lock ordering, SERIALIZABLE or equivalently proven isolation, idempotent unique financial writes, permission+membership checking to commit, scratch PostgreSQL migration only. Production DENY-ALL/HOLD.
7. **Previously accepted 2G.1/2G.2** are not reopened by old prose in the October 22 document. Review regression only where new cross-phase code actually touches them.

## Detail register: decided intent, **not** decided implementation formula

| Detail ID | Open implementation detail | Safe provisional state |
|---|---|---|
| **D01** | For H3, who pays the move date (source or target)? Is the start instant midnight Vietnam time or an attested handover time? | No mid-month writer. Proposed half-open occupancy periods must be signed/approved before calculation. |
| **D02** | Per-month actual calendar-day denominator and **VND rounding** per line/invoice; monthly edge dates and leap year. | No prorated invoice posting until golden fixture values approved. |
| **D03** | How to handle an already-issued source-month bill, void/correct by append-only delta, due date, promotions and fixed service fees. | Preserve prior issued evidence; do not mutate/delete. |
| **D04** | Deposit top-up collection method, independently verified receipt, partial refund timing/recipient, approved bank reference, failed/late payment and disputed amount. | No automatic refund/top-up or fake payment evidence. |
| **D05** | Exceptional early move-out charge/waiver, notice-date reschedule process, and boundary for an Approved notice. | Owner exception with reference; no uncontrolled override. |
| **D06** | Exact minimum handover signatory matrix, whether a photo alone suffices or an independent inspector must also attest, optional asset inventory format. | No missing E/W/contract/debt/evidence acceptance. |
| **D07** | Detailed per-service final-month tariffs and whether specific non-rent fees are fixed or daily; refund SLA. | Fee-by-fee policy; zero invented charges. |

**These are refinement gates, not re-openings of H1–H6/OI-01–OI-06.** Once chosen and tested, add versioned policy references. Never hardcode defaults that change tenants' financial obligations.

## Approved Phase 2G.4 operational addendum — 2026-11-02

**Owner acceptance:** the owner explicitly approved the proposed manual-refund,
recipient, disputed-debt, retry and 7-business-day handling in the 2026-11-02
project conversation. This addendum resolves the **ordinary 2G.4 move-out
manual payout process** without pretending an external bank integration exists.

1. **Destination:** root Owner approves the exact beneficiary name, bank,
   account and signed destination reference before any refund confirmation.
   The amount is inherited exclusively from the immutable deposit decision;
   the customer/client never supplies a trusted refund amount.
2. **Payment evidence:** external manual transfer and bank statement,
   separate transaction reference and signed independent review are required.
   The reviewer differs from the approving Owner. A recorded Owner confirmation
   alone does not equal a completed payment or trigger contract closeout.
   Duplicate references across incoming receipts, failed attempts and refunds
   must be blocked at PostgreSQL commit, including direct SQL.
3. **Deadline:** seven **business days after approved final reconciliation**,
   counting from the next day. Saturdays/Sundays and statutory holidays
   supplied in the *signed Vietnam holiday calendar* are excluded; the
   calendar reference and calculated due date are frozen with the Owner
   destination instruction. Missing/unapproved holiday schedules are NOT
   grounds to invent a longer payment deadline.
4. **Failure/late handling:** record each failed bank attempt with immutable
   reason, failed timestamp and bank reference. A failed reference must not
   be reused as proof of an executed refund. Retry uses a **new** transaction
   reference; overdue/pending/failed are not settled statuses. Reconciliation
   remains pending until a new distinct independently reviewed transfer exists.
5. **Dispute:** open a dispute only for an original official receivable,
   preserve disputed evidence and amount, and deny cấn trừ/deposit closeout
   while disputed. Owner independently signs an AcceptDebt or RejectDebt
   resolution. AcceptDebt resumes source reconciliation; RejectDebt does
   **not** delete or rewrite the issued invoice and remains blocked until
   official corrective evidence is handled through the existing immutable
   invoice-adjustment process. No unsafe automatic allocation within an
   underfunded shared E/W priority tier is authorized.
6. **Scope and environment:** staging-only, isolated disposable PostgreSQL.
   No bank API verification, bank transfer execution, production deployment,
   feature switch, production migration or live transaction is authorized.
   This settles D04/D07 only for *ordinary Phase 2G.4 manual refunds*;
   different-contract deposit top-ups, partial transfer and the H4 room-change
   financial lane remain separate Phase 2H gates.

The implementation must pass real PostgreSQL migration, SQL-bypass, immutable
history, failure/retry, authenticated HTTP and Full regression on the exact
commit SHA before declaring 2G.4 staging technical acceptance.

## Audit baseline and compliance

- Last code commit before this decision: `c12e37031b8ac42a1ca6d2dfc58d3956f16055ab`. Full regression, scratch PostgreSQL including authenticated positive/concurrent 2H HTTP and immutable history, and ABP Identity CI were reported successful **for that SHA**. Do not reuse that PASS claim to certify a later SHA or new business paths.
- Code presently implements one **month-end, same-rent/same-deposit, all-tenants, new-contract** safe lane. Additional mid-month/delta lanes remain denied pending implementation and PostgreSQL proofs.
- **Decision recorded only; no production deployment, runtime feature switch, migration or transfer/refund is authorized by this document.**
