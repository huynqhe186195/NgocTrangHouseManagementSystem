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
