# Spec Kit + H4 security + D01/D02 calendar integrated staging review (2026-11-05)

## Merge provenance and exact scope

This review integrates existing, independently tested work on a **single staging-only draft PR**. It is not a production or financial settlement release.

- Base `wip/phase-2g2`: `a9519996c06d42db880cec4a741036a2e520e460`.
- Existing Feature 001 / H4 commit-lease / owner policy candidate: `integration/spec-kit-h4-security-20261105` at `071a638969e6fa75204bdc4779de12b2681ec541` (combined draft PR #5, itself incorporating review of PR #3 and PR #4).
- Feature 002 D01/D02 calendar test source: `test/phase-2h-d01-d02-calendar-golden-20261105` at `a0376d1d4dc29bb4b542e74d55555c17e2bad99c` (draft PR #6).
- Feature 002 test and its three spec/plan/task documents are copied with **identical Git blob SHAs**. No financial application writer or migration is changed.
- The overlap `.github/workflows/buildingmanagement-slice-a-postgres.yml` has the **same** blob `be5c4d3cc077aa0d0fbec67d28f5327372a0c19d` in both source heads; it is intentionally not applied twice. The exact 162/162 Domain and 103/103 PG strict-count checks remain intact.
- The source PR #6 documentation describes it as independent of PR #5; that describes its original review branch. This file supersedes that branching note **only for the combined staging candidate**, not its business acceptance rules.

## What independently passed before combination

- PR #5 exact-source-head `071a6389`: 4/4 workflows SUCCESS (H4 authenticated PostgreSQL 1/1; full disposable migration 1/1; PG 103/103; Slice A 162/162; full Domain 712, Application 183, EF 447, HTTP 76; Identity workflow succeeded). See run IDs 42001089936, 42001089839, 42001089783, 42001089780.
- PR #6 exact-source-head `a0376d1d`: 3/3 workflows SUCCESS (H4 authenticated PG, Slice A PG, safe regression). See run IDs 42005350363, 42005350302, 42005350072.
- The exhaustive 118 calendar scenarios are loop assertions within ONE existing xUnit Fact, not 118 additional tests. Do not sum filtered tests with overlapping full suites.

## Integration acceptance: intentionally not pre-certified

- [ ] Verify this **new combined commit SHA** and required GitHub workflows conclude SUCCESS, with positive evidence that authenticated HTTP and PostgreSQL tests executed (no opt-in early return or skipped fixture).
- [ ] Verify no new 160-vs-162 hard-coded gate regression, unauthorized access or financial write was introduced.
- [ ] Obtain independent human review of the proposed Constitution, Feature 001 security matrix and Feature 002 D01/D02 golden policy; select **this** combined PR OR its individual source PRs, not all simultaneously.
- [ ] Keep original bank-backed H4 top-up collection, issued future-rent-credit consumption, manual refund payout; D01/D02 official-issued-invoice SQL parity, D03 service/rent posting, D06 signatory execution, Owner PDF export and overall BUSINESS ACCEPTANCE as distinct HOLD gates.

## Nonnegotiable boundaries

Only disposable PostgreSQL / CI staging. No `main` merge, production migration, real customer money, payment/refund action or real bank connection. Do not interpret a deferred obligation as settled cash.
