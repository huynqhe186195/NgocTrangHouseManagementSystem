# Feature 001 — Specification Quality Checklist

This is a **review checklist**, not proof that code or banking behavior is accepted.

## Business meaning

- [x] Specification identifies owner-approved H4, H1/H5/H6 dependencies and the authoritative decision register.
- [x] Top-up, next-rent credit and refund-liability have distinct outcomes.
- [x] Pending financial instruction vs actual receipt/disbursement is explicitly distinguished.
- [x] Exclusions include production and rewriting approved existing workflows.
- [x] Binding Owner decision register addendum of 2026-11-04 reconciled: D01 target-day, D02 actual-calendar VND rounding, D03 room-specific services, D04-H4 unpaid top-up and exclusive credit/refund, D06 three signers (see `../approved-policy-reconciliation-20261105.md`).
- [ ] Actual all-path implementation/SQL/HTTP acceptance for these approved rules and outstanding per-service tariff/posting details; Owner new-contract export is a separate delivery.

## Security and accounting

- [x] Specification requires cash provenance, unique VND disposition and immutable audit trail.
- [x] Specification requires authenticated ABP building isolation and independent approvals.
- [x] Specification includes replay, duplicate, concurrent request, rollback and SQL-bypass negatives.
- [x] Each AC-H4-01–12 mapped to source, test IDs, exact CI context and evidence gaps in `../acceptance-matrix.md`. Mapping completion is NOT full AC acceptance. T007's bounded Identity/Building revocation regression passed on PR #4 exact SHA, but broader AC-H4-09 across future financial-posting endpoints remains PARTIAL. Combined draft PR #5 exact-head four-workflow CI also passed at `7179cface`; neither check is business acceptance.

## Delivery evidence

- [x] Baseline commit and relevant two GitHub Actions run URLs recorded.
- [x] Combined PR #5 integration HEAD `7179cfacea658790582802b4397a532b7411629f` verified for the reviewed documentation/test candidate. Recheck again before any future code implementation.
- [x] CI job logs and their real test counts reviewed and recorded in `../acceptance-matrix.md`; artifact ZIP/TRX XML was not independently downloaded, and PR checks used the synthetic merge SHA.
- [x] Acceptance matrix completed; unmet H4 external financial settlement remains T009/D04-H4 HOLD. T007's bounded H4 staging-security regression is evidenced in PR #4, with combined PR #5 exact-head CI verified and human approval still open; no financial implementation is inferred.
- [ ] Independent code PR (if necessary) passes matching SHA checks.
- [ ] Business acceptance decision recorded by authorized owner.
- [ ] Production release authorized separately. **Currently forbidden.**
