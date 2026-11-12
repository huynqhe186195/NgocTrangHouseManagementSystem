# P3.4 — Final Review and Release Proposal (Owner decision document)
**Date:** 2026-11-12 (Asia/Ho_Chi_Minh)
**Candidate:** branch `feat/local-safe-startup-20261109` (PR #10 trunk, containing the Owner-approved P3.1 merge of PR #5)
**Final SHA under proposal:** `feb8938` (docs: P3.3 acceptance matrix; all executable code unchanged since `1ddf2e6`)
**Basis:** P3.3 acceptance matrix `etc/phase-audits/P33_AcceptanceMatrix_20261112.md` (Owner-reviewed this date) + the full regression evidence chain on this branch.

## 1. Proposal

**Propose: merge `feat/local-safe-startup-20261109` → `main` (PR #10), gated on the preconditions in §3.**

This closes the P3 integration phase: the trunk carries one coherent, fully-tested tree — payOS D04 financial security stack (P0/P1: native JWT auth, least-privilege DB roles, encrypted vault, crash-recovery, internal simulated staging E2E) merged with the H4/D01–D03 business lane (P2 staging evidence), with the entire program's local regression green at the exact final SHA.

**This document is a PROPOSAL. Nothing merges, deploys, or switches to real money without the Owner's explicit authorization below (§6).**

## 2. Final review — evidence at the exact final SHA

| Layer | Evidence at final SHA | Status |
|---|---|---|
| Executable code | identical to `1ddf2e6` (feb8938 = docs-only commit; `git diff 1ddf2e6..feb8938 --stat` touches only the 3 doc files) | ✅ verified |
| Full regression batch (2g Full: 3 source gates + Domain 720/720 + Application 231/231 + EF-SQLite 447/447 + HTTP 76/76) | re-run at `feb8938` (§2.1) | ✅ (re-run appended below when complete) |
| 2H financial foundations 5/5 groups, 2g5 staging 4/4 groups, PG 103/103, auth HTTP, migration chains | ran at `1ddf2e6` (identical code) — TRX `15–18-2h-*.trx`, logs `11–14-2g5-*.log`, all stamp `PHASE2H_COMMIT=1ddf2e6...` | ✅ |
| payOS harnesses: native JWT matrix, writer-only, FULL bundle 01–27, P1.5 internal E2E 20/20 | ran at `38f0037`/`7f48c59`/`01cba31` (predecessor SHAs of the same tree; only test-fixture/guard/docs commits since) | ✅ |
| Solution build | 0 errors at `01cba31` (merge) and unchanged through `feb8938` (regression re-build each run) | ✅ |

### 2.1 Exact-final-SHA regression re-run record
`verify-2g-security-batch.ps1 -Mode Full` re-run at final SHA `feb8938` on 2026-11-12 (branch `feat/local-safe-startup-20261109`, clean tracked tree, exit 0):
- 3 source gates PASS (building-scope design-only, invoice-revision no-write, move-out lifecycle)
- Compile-only PostgreSQL project PASS
- SQLite module startup smoke 1/1
- Domain **720/720**, Application **231/231**, SQLite EF **447/447**, HTTP Host **76/76** — 0 failed, 0 skipped
- QUALITY GATE PASS banner: ALL REQUESTED TEST RUNS SUCCEEDED

Combined with the `1ddf2e6` runs (identical executable code, verified by `git diff 1ddf2e6..feb8938 --stat` = 3 doc files only): 2H foundations 5/5 groups, 2g5 staging 4/4 groups, and the payOS harness chain (§2 rows above) stand as the exact-final-SHA evidence set.

## 3. Merge preconditions (all must hold before executing the merge)

1. **GitHub billing resolved** (SEC-06): the CI-evidence column of the acceptance matrix is HOLD until Actions runs succeed at the merged PR head. Either (a) Owner resolves billing first, then CI runs green on PR #10's final SHA, merge; or (b) Owner explicitly accepts local-evidence-only and defers CI evidence with a recorded decision. **CI-first is the recommended path** — the billing lock affects all workflows, and `main` merge is the least reversible action in the program.
2. **P2 scope decision** (recorded deferral): P2.1–P2.6 remain open business work. Merging to `main` merges the *current* tested scope (P0/P1 + P2 staging evidence). The Owner must either accept "main = tested-staging-scope, P2 continues on follow-up branches" or hold the merge until selected P2 items close. **Recommendation: accept the deferral** — the P2 items are additive features on an already-frozen spine, and `main` currently has none of this work at all.
3. **Known scope limits carried forward** (from P1.2/P1.3, not new): Host writer connections in the D04-B harness lane still use the disposable superuser account in local rehearsals; production role provisioning, KMS, cloud malware scan, legal hold = HOLD. These are operational-production items, unaffected by merge.
4. **No merge-to-main without this document's §6 signature** (house rule).

## 4. What merging does NOT authorize (unchanged HOLD list)

- Real payOS QR payments / webhooks / bank transfers / refunds — always separate explicit Owner authorization (ACC-08).
- TLS HTTPS public callback URL — internal staging only (P1.5 Owner decision).
- Production vault/KMS/IAM/backup/legal-hold.
- Production deployment or any live database migration. The merged tree's raw-SQL migrations remain rehearsal-only artifacts.
- Deleting or force-pushing any branch.

## 5. Post-merge plan (proposed)

1. PR #10 merge commit lands on `main` via normal GitHub merge (no force, no squash that would rewrite the evidence SHAs — **merge commit or rebase-preserving strategy, Owner chooses**).
2. Re-run local full regression batch once at the post-merge `main` SHA (record in CURRENT_STATE.md).
3. CI evidence retro-filled when billing resolves (matrix section E gains a new "post-merge CI" row).
4. P2 business work (P2.1 → P2.5, P2.6 discovery) starts on follow-up feature branches off `main`; each follows the same fix→push→harness→docs discipline.
5. P3.4 closes; CURRENT_STATE/TASK_BACKLOG record the merge SHA as program baseline.

## 6. Owner authorization

**2026-11-12 update — precondition §3.1 resolved:** the Owner unlocked the GitHub Actions account (no payment method added; free-tier public-repo usage). CI is re-armed: the workflows will run on the next push; CI evidence retro-fill for SEC-06 begins immediately (see CURRENT_STATE.md). The CI-first path (option a) is in effect.

**2026-11-12 (later) update — precondition §3.1 evidence COMPLETE:** CI run 384 at `c63d370` (ID 43240562191) — **all 5 jobs of `payos-foundation-isolated.yml` SUCCESS** (offline EF preview, D04-B three-DB writer/ACK, isolated payOS tests, disposable PG16 schema/concurrency/rollback, EF migration up/down/reapply). Two CI-only workflow/fixture gaps surfaced during re-arming (runs 382–383) and were fixed with locally-verified commits `80e3c00` + `c63d370` before the green run; no acceptance or security gate was weakened. The remaining merge preconditions are §3.2 (P2 deferral decision) and §6 (Owner signature).

**2026-11-12 (final) — Owner authorization EXECUTED:** the Owner approved BOTH remaining decisions in one instruction ("Làm cả 2 đi", 2026-11-12):
- **§3.2 P2 deferral: ACCEPTED** — `main` = tested-staging-scope (P0/P1 + P2 staging evidence); P2.1–P2.6 continue on follow-up feature branches off `main`.
- **§6 merge-to-main: SIGNED** — merge of `feat/local-safe-startup-20261109` → `main` (PR #10) authorized for execution per §5 (merge commit, no force, no squash; local regression re-run at post-merge SHA; CI evidence on `main`).
All four preconditions (§3.1 billing/CI ✅, §3.2 ✅, §3.3 scope-limits carried ✅, §3.4 signature ✅) now hold.

**Merge execution:** ✅ Owner authorized 2026-11-12 ("Làm cả 2 đi" — explicit instruction recorded in conversation)
**P2 deferral acceptance (precondition §3.2):** ✅ Owner accepted 2026-11-12 (same instruction) — main = tested-staging-scope; P2.1–P2.6 on follow-up branches
**CI-evidence path: ☐ resolve billing first ☐ defer with recorded decision** (precondition §3.1) — ✅ resolved 2026-11-12: billing unlock (card-free), CI-first path; run 384 at `c63d370` green 5/5 (ID 43240562191)

---
**Prepared by:** Claude Code (program evidence chain 2026-11-10 → 2026-11-12, commits `0702e0d` → `feb8938`)
**Attribution:** Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
