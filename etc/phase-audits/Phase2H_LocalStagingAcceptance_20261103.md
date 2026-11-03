# Phase 2H — local/staging foundation acceptance and remaining execution gates

**Date:** 2026-11-03 (Asia/Ho_Chi_Minh)  
**Tested code baseline:** `d047734a120a3efcf256f1a94e973854d84d8d1b`  
**Branch:** `wip/phase-2g2`  
**Acceptance:** H2/H3/H4/H6 **FOUNDATION STAGING VERIFIED**; full mid-month transactional Room Change **HOLD**.  
**Production:** no deployment or activation. Database and financial writer changes excluded.

## User-reported local verification evidence

The operator ran `etc/scripts/verify-phase-2h-financial-foundations.ps1` at the exact code baseline above. The provided final output was:

```text
=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===
COMMIT=d047734a120a3efcf256f1a94e973854d84d8d1b
Full mid-month contract/cash writer remains HOLD; production not used.
```

The script has five hard-stop gates:
1. Exactly **32/32** focused Domain tests: 24 rent/deposit foundations and 8 server-verified H6 handover scenarios.
2. Exactly **17/17** PostgreSQL room-change request/roster/atomic transfer tests.
3. Full migration rehearsal on a throwaway local PostgreSQL test database.
4. Authenticated staging HTTP/PostgreSQL regression on independently named disposable databases.
5. Full Domain/Application/EF/HTTP regression requiring its exact quality-gate banner.

This is **user-provided local test evidence**, not an independently retrieved GitHub Actions run URL. Logs remain local in `$env:USERPROFILE\NgocTrang-Test-Logs\15-...` through `19-...`. Do not assert that CI has passed this SHA without an actual corresponding run.

## Cross-layer closeout guard review

- Existing `Phase2HWallClockCompletionValidation` SQL commit function `BM_VerifyRoomChangeCommit` already recalculates the transfer-completion canonical SHA-256 over the archived typed fields, so a duplicate hash-only migration is unnecessary.
- The same SQL function still restricts the executable completion to the legacy safe policy lane: `PricingTreatment=1` (full old month/new following month) and `DepositTreatment=1` (full verified carry with no difference). The Domain `RoomChangeCompletionRecord` and the authenticated `SliceBStagingRoomChangeRequests.CompleteRoomChangeAsync` independently enforce that lane.
- H6's `RoomChangeReducedHandoverChecklistGate` runs only **after** original E/W meter verification, immutable all-tenant roster matching, official receivable settlement and cash-backed held-deposit checks, and before either contract/room state is mutated. Its `ServerVerified*` flags are constructed by trusted server code, not accepted from HTTP input.
- A signed string reference is **not** a cryptographically verified signature or proof of a successful bank transfer.

## Remaining specific HOLD items (not new owner approval of H1–H6)

| Gate | What is still required before widening the writer |
|---|---|
| D01 | Signed rule identifying the charged room on handover date and exact source/target effective cutoff. |
| D02 | Approved VND rounding per rent line, residual policy and month-length/leap-day examples. |
| D03 | Independently reviewed append-only correction of already-issued rent/service debt and receipt provenance; prevent negative or duplicate receivables. |
| D04 | Verified deposit top-up receipt or independently reviewed partial refund disbursement with approved beneficiary, immutable journal, idempotency/concurrency/rollback. |
| D06/H6 | Approved signatory matrix and signed physical asset/defect/room-handover checklist; no fake document/reference acceptance. |
| Final execution | Authenticated non-safe-lane HTTP writer, PostgreSQL deferred commit checks for both rooms/contracts and cash disposition, race/rollback/security test evidence. |

No further Phase 2H financial mutation, default proration policy, production migration or tenant-facing access should be inferred from the five PASS groups. Phase 2G.4 and 2G.5 accepted code baselines remain unchanged by this documentation.
