# Plan — Feature 002 calendar rent regression and remaining integration gates

## Execution strategy

Preserve the existing domain calculator and approved-policy validator; code review did not identify a reason to rewrite arithmetic. Extend two existing xUnit Fact methods instead of creating new Fact/Theory cases:
- `TRF_D01_04`: loop every D in nonleap Feb-28 (2026), leap Feb-29 (2028), Apr-30 (2026) and Jan-31 (2026). This executes **118 assertion scenarios within one Fact**, NOT 118 new xUnit tests.
- `TRF_D01_08`: prove 22.5→23 whole-VND on BOTH room lines in 28/30-day months and check 29/31 denominators.

No source writer or schema migration is required for this test-only slice. Do not alter strict CI expected test counts simply because the internal assertion loop expanded.

## Quality gates

1. Inspect the exact test diff and ensure all approved D01/D02 and 2G.5 invariants stay unchanged.
2. Verify matching GitHub Actions workflows on the **current new commit SHA** (Windows safe regression, disposable H4 PostgreSQL, Slice A PostgreSQL and isolated Identity where triggered); review actual conclusion, executed test counts and failed/skipped status. No PASS before confirmed results.
3. If a workflow fails, classify behavioral failure vs strict-count mismatch; do not loosen filter, skip, or allowlist to get green.
4. Keep documentation status as TEST/CI only after evidence. Real signed-source, issued-line, authenticated HTTP, PostgreSQL parity, SQL-bypass, double-entry balance, rollback and owner acceptance are a separate implementation/evidence gate.
5. Do not merge draft PR #3/#4/#5 redundantly with this independent PR. No `main`, migration against live DB or production deployment.

## Rollback

Revert this branch's test and documentation commits; there is no runtime, schema, or customer-money side effect.
