# Feature 002 — D01/D02 calendar-day and VND rounding acceptance

**Status:** test-enhancement candidate, not BUSINESS ACCEPTED.  
**Business source:** Phase 2G–2H v1.0 SRS; Owner-approved H1–H6/OI-01–OI-06 and binding 2026-11-04 D01/D02 addendum in `etc/phase-audits/Phase2G2H_ApprovedBusinessDecisions_20261101.md`.  
**Code baseline:** `wip/phase-2g2` at `a9519996c06d42db880cec4a741036a2e520e460`.  
**Safety:** no application writer, database migration, production change or external money operation.

## Approved rules — do not re-open

- **D01:** Vietnam civil transfer day D belongs to the TARGET; source rent days run through D−1. Never charge the same civil day to both rooms.
- **D02:** Each room's separate nonnegative transfer-rent line uses its own signed integer-VND monthly rent, divided by the actual number of days in that month (28/29/30/31), multiplied by chargeable days, rounded to whole VND with `MidpointRounding.AwayFromZero` (0.5 VND rounds up). Do not silently truncate or assume 30 days.
- This is ONLY transfer-specific 2H rent; ordinary Phase 2G.5 move-out retains its independent full-final-month rent policy.
- Preserve immutable historical quote candidates using earlier source-day or truncation options; never allow such candidates to authorize NEW H4 execution.
- `Contract.ActualEndedAt` records the real event time, not D−1. The signed pricing rule holds the source's last CHARGEABLE date. Do not rewrite a real audit timestamp to spoof civil-day rent ownership.

## Existing checked implementation

- `RoomChangeDailyRentQuoteCalculator.Calculate`: calculates proposed room-specific rent for a month; a quote cannot issue invoice or execute transfer.
- `RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote/Rule`: blocks legacy source-day or truncation choices on executable transfers and recalculates canonical fields.
- `SliceBStagingRoomChangeH4Intent` and `SliceBStagingRoomChangeH4Execution` call the approved-rule check after signed rule verification; `RoomChangeMidMonthCompletionRecord.Complete` also checks it.
- The H4 writer additionally requires positive days for both rooms and rejects handover on a month's last day; a domain quote for last-day handover is NOT by itself an executable end-to-end H4 transfer.

## Acceptance requirements

1. Test **every** D in representative 28-, 29-, 30- and 31-day months: source days D−1, target days N−D+1; their sum N; source last date D−1 (null on first), target first date D.
2. Recompute separately rounded source and target VND figures using an independent test oracle for each D; assert the combined quote equals the sum of the separate rounded amounts.
3. Test exact half-VND midpoint-up on TWO even-denominator months (Feb nonleap 28; Apr 30); verify 29-day leap Feb and 31-day Jan with independent non-half denominators. Per-line rounding may make the two line totals differ from a single separately rounded full-month rent; do not invent an offset.
4. Keep negative tests blocking legacy source-day, truncation and forged quotes and ensure the preview never has invoice-posting or transfer-execution authority.
5. **Residual acceptance HOLD:** original issued invoice and its rent line, independent allocation/correction approval, immutable posting/remaining balance and SQL direct-write defense; real authenticated H4 positive/negative and rollback plus 2G.5 distinction at the exact implementation SHA. Golden domain tests alone do not certify these.

**Exclusions:** PR #5 Spec Kit H4 security compatibility remains separate and draft; no duplicate merge, `main` merge, external settlement or production approval.
