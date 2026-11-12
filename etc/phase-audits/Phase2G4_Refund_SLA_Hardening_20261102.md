# Ngọc Trang — Phase 2G.4 manual refund and SLA hardening (2026-11-02)

**Branch:** `wip/phase-2g2`  
**Scope:** development / isolated disposable PostgreSQL staging only. No production deployment, actual bank transfer or live database operation.  
**Normative business source:** `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx` §6 and `Phase2G2H_ApprovedBusinessDecisions_20261101.md`, approved Phase 2G.4 operational addendum.

## Implemented change set on this branch

1. A refund bank review and a positive-refund CompleteMoveOut must match the *same immutable* Owner-authorized destination instruction (contract, request, approved decision, building, Owner, approved VND amount), whose approval predates the attested `PaidAtUtc`. The cash amount is **never** accepted from HTTP input.
2. The seven-business-day refund due date starts on the Vietnam calendar date of the **approved final reconciliation / deposit decision**, not the later beneficiary-instruction date.
3. The actual approved statutory-holiday set is now stored canonically as `StatutoryHolidaysJson` in the immutable refund instruction. Same reference with different holiday dates is a conflicting replay, not idempotence.
4. Additive migration `20261102024000_Phase2G4RefundHolidaySnapshotAndSlaAnchor` recomputes the seventh eligible Monday–Friday from the original signed decision day, excluding frozen valid statutory-holiday dates, enforcing the due date on SQL INSERT even if the HTTP service is bypassed. It deliberately **refuses migration if older instruction records already exist without an explicit frozen calendar**, rather than fabricating `[]` for missing evidence. Never run it on an unmanaged/non-disposable database.
5. Refund operating status only reports `RefundVerified` if the independent bank review actually joins to an approved destination instruction, with matching original decision, amount and approval-before-payment timestamp. Prior failures do not hide an overdue refund.
6. Existing debt-offset priority/Owner split, duplicate transfer references, independent reviewer, retries, invoice adjustment, immutable evidence, disputes and 2G.5 closeout safeguards stay intact.

## Changed verification tests

- `ContractDepositRefundOperationsPolicy_Tests.OI04OPS07_Overdue_Takes_Precedence_Over_Failed_Retry`: failed -> overdue after deadline; independent review remains terminal verified status.
- `SliceAStagingPostgreSqlHttp_Tests`: authenticated independent refund review before Owner destination instruction is forbidden; same beneficiary and policy reference with a different statutory-holiday array is forbidden; the previously existing positive approved-payee -> independent review -> closeout exercise remains in the same HTTP/PG scenario.

## Acceptance gate for exact final branch SHA

**Not accepted solely by source review or by PASS results for an older SHA.** Before marking `Phase 2G.4 BUSINESS ACCEPTED (staging)`, a CI operator must capture:

- .NET 10 compile + domain/application/HTTP regression for the **new SHA**; `etc/scripts/verify-2g-security-batch.ps1 -Mode Full`.
- A *fresh, isolated, disposable PostgreSQL* full EF migration chain including this migration, followed by authenticated positive refund HTTP and negative preapproval/conflicting-calendar HTTP flows.
- PostgreSQL direct-SQL negative tests for tampered, backdated, duplicate and mismatched payee/holiday/refund records; concurrency/rollback proving failed invalid writes cannot leave partial transfers or contract closure.
- ABP Identity + building membership authorization regression; no cross-building refunds and no reviewer==approving Owner.
- Full test result counts and CI run URLs tied to the final commit SHA (not earlier baseline). No production migration or bank API activity.

**Status at document creation:** code and test cases committed, but a fresh exact-SHA compile, scratch PostgreSQL run, direct SQL-bypass test and Full regression are **not yet independently observed** through the available integration. Retain `HOLD / NOT BUSINESS ACCEPTED` until that evidence is captured.
