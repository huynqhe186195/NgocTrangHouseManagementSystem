# Feature 001 — Specification Quality Checklist

This is a **review checklist**, not proof that code or banking behavior is accepted.

## Business meaning

- [x] Specification identifies owner-approved H4, H1/H5/H6 dependencies and the authoritative decision register.
- [x] Top-up, next-rent credit and refund-liability have distinct outcomes.
- [x] Pending financial instruction vs actual receipt/disbursement is explicitly distinguished.
- [x] Exclusions include production and rewriting approved existing workflows.
- [ ] D01/D02/D03/D04/D06 detail gates reconciled with the controlling signed policy for a full mid-month launch.

## Security and accounting

- [x] Specification requires cash provenance, unique VND disposition and immutable audit trail.
- [x] Specification requires authenticated ABP building isolation and independent approvals.
- [x] Specification includes replay, duplicate, concurrent request, rollback and SQL-bypass negatives.
- [x] Each AC-H4-01–12 mapped to source, test IDs, exact CI context and evidence gaps in `../acceptance-matrix.md`. Mapping completion is NOT full AC acceptance; T007 remains partial.

## Delivery evidence

- [x] Baseline commit and relevant two GitHub Actions run URLs recorded.
- [ ] Current HEAD rechecked immediately before implementation.
- [x] CI job logs and their real test counts reviewed and recorded in `../acceptance-matrix.md`; artifact ZIP/TRX XML was not independently downloaded, and PR checks used the synthetic merge SHA.
- [x] Acceptance matrix completed; unmet H4 executed-settlement work and H4-specific revocation test audit retained as open T007/T009/D04-H4, without inventing an implementation.
- [ ] Independent code PR (if necessary) passes matching SHA checks.
- [ ] Business acceptance decision recorded by authorized owner.
- [ ] Production release authorized separately. **Currently forbidden.**
