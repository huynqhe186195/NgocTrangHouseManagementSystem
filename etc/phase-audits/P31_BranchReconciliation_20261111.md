# P3.1 — Branch Reconciliation & Merge Choreography Analysis
**Date:** 2026-11-11
**Analyst:** Claude Code (read-only analysis; no merge performed — every merge below requires explicit Owner approval)
**Base repo state:** `feat/local-safe-startup-20261109` @ `c735639` (== origin)

---

## 1. Lanes under comparison

| Lane | Branch | HEAD | Commits past merge-base `e05049d` | Scope |
|------|--------|------|-----------------------------------|-------|
| PR #10 (P0+P1) | `feat/local-safe-startup-20261109` | `c735639` | **371** | D04-B payOS journal, writer gates, native JWT matrix, vault, restricted roles, P1.5 internal simulated payOS staging |
| PR #5 (Spec Kit) | `integration/spec-kit-h4-security-20261105` | `b3122b14` (origin) | **95** | Spec Kit adoption, D01/D02 calendar, D03, H4 settlement domain, Phase 2H financial foundations |
| 2G2 WIP | `wip/phase-2g2` | `a9519996` (origin) | **9** | H4 domain test tightening + CI |

**Merge-base (confirmed):** `e05049d037a4299ca17c04d2dd3a3c25f9637bfd` — PR #10 ↔ PR #5 ✓ (matches handoff record)

**Containment findings:**
- `origin/wip/phase-2g2` (`a9519996`) is a **full ancestor of PR #5's HEAD** (merge-base PR5↔2g2 = `a9519996` itself). PR #5 already absorbs the entire 2g2 lane.
- Local `wip/phase-2g2` (`e8e4f29`, 49 behind origin) is stale — no unique work vs origin (behind only).
- Legacy lanes `feat/d04b-operational-gates-20261108` (`705a481`), `feat/d04b-protected-bank-evidence-20261108` (`66a62dc`), `feat/phase2h-payos-signature-foundation-20261106` (`7249c9f`) are all **ancestors of PR #10** — already absorbed.

**Conclusion: only TWO independent merge parties exist — PR #5 and PR #10.**

## 2. File-level conflict scan (git merge-tree, read-only real merges)

| Merge direction | Result |
|-----------------|--------|
| PR #10 → PR #5 | **CLEAN** (tree `dd4443b1`, exit 0, zero conflicted files) |
| 2g2 → PR #5 | CLEAN (no-op: ancestor) |
| 2g2 → PR #10 | CLEAN (tree `f3d6d43b`, exit 0) |

- Overlapping changed files PR #10 ↔ PR #5: **0**
- Overlapping changed files PR #10 ↔ 2g2: **0**
- PR #5 ↔ 2g2: 8 files (RoomChanges domain tests + 2 CI workflows + 2 verify scripts) — contained within PR #5's history, not a conflict.

## 3. Migration conflict map

| Lane | New migration(s) since `e05049d` | Tables created | Notes |
|------|----------------------------------|----------------|-------|
| PR #10 | `20261107160000_StagePayOsD04LegacySourceJournal` | `BuildingManagementPayOsD04BankReviews`, `...OwnerApprovals`, `...ReceiptJournal`, `...Outbox` | Raw-SQL migration; no Designer/snapshot file |
| PR #10 | `PayOs/` context: `20261106141311_StagePayOsPaymentIntentsAndWebhookInbox`, `20261111073002_StagePayOsStagingOutbox` (+ snapshot) | Separate `PayOsPaymentMigrationDbContext` context — isolated from the shared context entirely | PR #5 never touches the PayOs folder |
| PR #5 | `20261105150000_Phase2HD04H4SettlementJournal` | `BuildingManagementRoomChangeH4SettlementReviews`, `...Postings` | Raw-SQL migration; no Designer/snapshot file |

**No overlap in:** table names, unique index names (`UX_PayOsD04_*` vs `UX_BM_D04_H4*`), trigger names (`TR_PayOsD04*` vs `TR_BM_D04_H4*`), function names, or migration file names.

**Shared-table interactions (semantic, not conflicts):**
- PR #5's credit-cap triggers (`TR_BM_D04_OfficialReceiptCreditCap` on `BuildingManagementOfficialInvoiceReceiptReviews`, `TR_BM_D04_AdjustmentCreditCap` on `BuildingManagementMonthlyInvoiceAdjustmentDecisions`) target tables created at the shared merge-base (`20261029226000` exists in all three views) — both lanes see identical base schema.
- PR #10's D04 journal FK targets (`Payments`, `Contracts`, `DepositSettlements`) are base tables; PR #5's H4 flow also references `PaymentId` — both are readers/writers of the same base tables but on disjoint column sets and disjoint journals. Interaction is by design (both are append-only financial journals over the same Payment/Invoice spine) and is exactly what P3.2 regression must exercise.

**Entity model snapshot:** neither lane modified any `*ModelSnapshot.cs` or `Designer.cs` in the shared context (both new migrations are raw-SQL). **Zero snapshot conflicts.**

**Migration ordering on a fresh merged DB:** timestamps order naturally — base migrations ≤ `20261104220000` → PR #5's `20261105150000` → PR #10's `20261107160000` → PayOs context runs separately. Both lane migrations are self-contained (all referenced objects exist at base). No re-numbering needed.

## 4. Test documentation overlap

- PR #5 docs: `specs/00{1,2,3}-*`, `.specify/*`, `etc/phase-audits/SpecKit_*`
- PR #10 docs: `CURRENT_STATE.md`, `TASK_BACKLOG.md` (top-level only)
- **Zero overlap.** After merge, `CURRENT_STATE.md`/`TASK_BACKLOG.md` remain the operational truth and `specs/` the Spec Kit truth; no consolidation required beyond cross-references.

**CI workflows:** PR #10 adds `payos-foundation-isolated.yml`; PR #5 modifies three other workflows (`buildingmanagement-safe-regression`, `buildingmanagement-slice-a-postgres`, `phase-2h-h4-disposable-acceptance`). No file overlap; after merge, both sets run.

## 5. Risk matrix

| # | Risk | Likelihood | Severity | Mitigation |
|---|------|-----------|----------|------------|
| R1 | Git merge conflicts | **None observed** (merge-tree clean both directions) | Low | Full-branch merge only (never cherry-pick — preserves migration+code pairing per DoD) |
| R2 | Duplicate/ordered migration failures on fresh DB | Low | High | Both lane migrations self-contained, disjoint names, natural timestamp order; P3.2 must run full migrate → rollback → reapply on scratch PG16 |
| R3 | Semantic interaction: H4 credit-cap triggers vs D04 receipt journal flows over shared Payment/Invoice spine | Medium | Medium | This is designed behavior, not a bug class; P3.2 must run BOTH `verify-phase-2h-financial-foundations.ps1` AND the D04 bundle on the SAME merged DB |
| R4 | Both lanes' CI suites assume exclusive repo state | Medium | Low | Workflows are file-disjoint; run merged-branch CI in P3.2 before any merge-to-main proposal |
| R5 | 2g2 stale local branch confusion | Low | Low | Treat `origin/wip/phase-2g2` as absorbed by PR #5; no action without Owner approval |
| R6 | PR #5's Spec Kit scripts evolve independently from PR #10's verify scripts | Medium | Low | Both script sets coexist under `etc/scripts/`; P3.2 runs both; consolidation is an Owner decision later |

## 6. Proposed merge choreography (for Owner approval — NOT executed)

**Order: merge PR #5 → PR #10 lane.** Rationale: PR #10 is the operational trunk carrying all P0/P1 pushed-SHA evidence and its branch is the one under active verification harnesses; PR #5 arrives as the absorbed payload. This produces a single final PR (PR #10) containing both, and PR #5 becomes closable-after (Owner decision).

Steps (each gated, each at a pushed SHA with TRX + LOG_DIRECTORY evidence per house rules):
1. **Merge** `origin/integration/spec-kit-h4-security-20261105` into `feat/local-safe-startup-20261109` (expected clean per §2). No `main`, no force push.
2. **Push** the merge commit fast-forward.
3. **P3.2 gate (merged-SHA regression):** on the merged HEAD — full migrations on disposable PG16 (both contexts), Domain/Application/EF/HTTP test suites from BOTH lanes, `verify-phase-2h-financial-foundations.ps1`, `verify-phase-2g5-staging.ps1`, D04 isolated bundle, P1.1/P1.4 native JWT matrix re-run (confirms PR #5 additions broke nothing in the P1 evidence chain).
4. **Only after all green:** update `CURRENT_STATE.md`/`TASK_BACKLOG.md`, propose merge-to-main timing to Owner (P3.4). PRs stay non-merged until Owner approval.

**Explicitly NOT doing:** no cherry-picks, no rebase of either lane, no deletion of `wip/phase-2g2` or any branch, no merge to `main`, no force push.

---

**Verdict:** the divergence is far cleaner than the handoff feared — two independent payload lanes, zero file/snapshot/index/trigger-name overlap, merge-tree clean in every direction. The real work is P3.2 regression on the merged tree (R3 is the only substantive risk). Awaiting Owner decision on the choreography in §6.
