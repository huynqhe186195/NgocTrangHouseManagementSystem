# CURRENT STATE — NgocTrangHouseManagementSystem
**Snapshot:** 2026-11-11 (P1.1 native JWT matrix green)  
**Verified by:** Claude Code audit  
**Working Directory:** `D:\Dev\NgocTrangBuilding\Project\NgocTrangHouseManagementSystem`

## Git Status

**Current Branch:** `feat/local-safe-startup-20261109`  
**HEAD SHA:** `6d3ff02` (local only — NOT pushed; push requires explicit Owner authorization)  
**Untracked Files:** `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`, `CURRENT_STATE.md`, `TASK_BACKLOG.md`

### Branch Inventory

| Branch | HEAD SHA | Tracking | Status vs Handoff |
|--------|----------|----------|-------------------|
| `feat/local-safe-startup-20261109` | `6d3ff02` | local ahead of origin by 5 commits (`92076b2`…`6d3ff02`) | ⚠️ NOT PUSHED — evidence below was taken with `-AllowUnpushedCommit` (pre-push mode) |
| `feat/d04b-operational-gates-20261108` | `705a481` | origin/feat/d04b-operational-gates-20261108 | ✅ Matches handoff |
| `feat/d04b-protected-bank-evidence-20261108` | `66a62dc` | origin/feat/d04b-protected-bank-evidence-20261108 | ✅ Matches handoff |
| `feat/phase2h-payos-signature-foundation-20261106` | `7249c9f` | origin (behind 1) | ⚠️ Behind, differs from handoff `2de0f7e` |
| `wip/phase-2g2` | `e8e4f29` | origin (behind 49) | ⚠️ Behind, differs from handoff `a951999` |
| `main` | `3e3636c` | origin/main | ✅ Matches handoff |

### Recent Commits on Current Branch (since handoff)

- `6d3ff02` — fix(d04b): durable-state settlement table is BuildingManagementDepositSettlements
- `95053a3` — fix(d04b): suppress docker cp output in Invoke-Scalar (array, not string)
- `bf193a3` — fix(d04b): single double-quotes in SQL here-strings (PS5.1 does not collapse "")
- `7dbda39` — fix(d04b): R1 must submit IntentA2's own bank reference, not the A-lane form
- `92076b2` — test(d04b): add explicit -AllowUnpushedCommit pre-push verification mode
- `f14745f` — fix(d04b): three real fixes for native OpenIddict UAT matrix blockers
- `7621d8a` — test(d04b): regression-test the Host D04-B startup gate as verify step 18c
- `fc322e8` — test(d04b): cover Host encrypted archive and gate it as verify step 18b
- `6622b3a` — fix(d04b): support Windows PowerShell 5.1 absolute vault path check
- `e246e8c` — test(d04b): add fail-closed native OpenIddict disposable Host launcher
- `0702e0d` — fix(d04b): use supported Convert.FromHexString for archive digest verification **(handoff baseline, last pushed SHA)**

## Implementation Matrix — PASS/PARTIAL/HOLD

### ✅ VERIFIED PASS (with exact SHA evidence)

| Item | Scope | Evidence | SHA | Notes |
|------|-------|----------|-----|-------|
| Identity/OpenIddict localhost | Owner/Reviewer 2 actors, bearer-only, profile HTTP 200, denied 403 | User-confirmed PASS 2026-11-10 | HEAD before `6622b3a` | `AUTH_BEARER_ONLY_PASS`, `IDENTITY_TWO_ACTOR_PASS` |
| D04-B operational gates | Full isolated PASS, forced OS-kill, ACK recovery, restricted DB role | User log `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-205635` | `705a481` (PR #9) | NOT re-verified on PR #10 yet |
| D04 payOS foundation | Real writer D04A_REAL01, 2-DB protocol, isolated bundle | `verify-payos-d04-local.ps1` historical PASS | Earlier SHA on foundation branch | Superseded for the writer-only scope by the `fc322e8` row below |
| D04 writer-only bundle on PR #10 | `-WriterOnly` on `7621d8a`: 19 passed / 0 failed | 5 TRX in `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261110-162113` | `7621d8a` | Synthetic signed test cookies, NOT native OpenIddict; NOT business accepted |
| D04-B Host startup gate | Default OFF, environment/DB/archive fail-closed, 8/8 | TRX `payos-d04-host-startup-gate.trx` in `payos-20261110-162113` / `payos-20261110-162625` | `7621d8a` | Pure extraction of the previously inline gate |
| D04 FULL local bundle on PR #10 | Steps 01–27 all PASS: 8 TRX, 118 passed, 0 failed | `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261110-162625` | `7621d8a` | Synthetic cookies, NOT native OpenIddict |
| **P1.1 Native D04 JWT matrix** | Real OpenIddict password-grant bearer tokens for `reviewer-uat`/`owner-uat`/`outsider-uat` on the real Host (https://localhost:44311, Staging): 25/25 matrix steps PASS — N1 anonymous (401/302), N2 forged bearer (401/302), N3 outsider review 403, N4 owner-as-reviewer 403, N5/N6 reviewer-as-owner 403, N7 cross-building 403, N8 self-approve 403; P1 review 201+reviewId, P2 idempotent re-review same reviewId, P3 approve 201+approvalId, P4 post 201+receiptId+LegacyCommittedAndStagingAcknowledged, P5 replay same receiptId; R1 review A2 201, T1 tampered archive blocks approve 403, R2 approve 201, R4 post after grant revoke 403; durable state psql: LEGACY `2\|1\|1\|1`, STAGING `3\|1\|2`; TRX `payos-native-jwt-provision.trx` 1/1 | **Run 27**: `C:\Users\Huynq\NgocTrang-Test-Logs\payos-native-jwt-20261111-000218` (TRX inside), `SOURCE_COMMIT=6d3ff02271d74e3de1c17252d92938b4954e7fc4`, run log `payos-native-jwt-latest-run.log` | `6d3ff02` | **Pre-push verification** (branch run with `-AllowUnpushedCommit`): MUST re-run after pushing to re-confirm at the pushed SHA. Tokens were real OpenIddict Bearer (not synthetic cookies); token values never echoed/saved. Fix commits for three real source bugs live in `f14745f` (UseUnitOfWork removal, Forbid→403, idempotent re-review reference reuse); four follow-ups fixed harness-only bugs (`7dbda39`, `bf193a3`, `95053a3`, `6d3ff02`). Temp container + worktree removed; 15433/5432 untouched |
| H4 + D01/D02 calendar | 118 ngày lịch, exact-head CI 4/4 PASS | PR #5 CI green at multiple commits | `0b8332e9` on PR #5 | PR #5 NOT in current branch |
| D03/D06 staging | Service pricing per room, reject fake signatures, Owner-only DOCX | PR #5 bounded CI PASS | `0b8332e9` on PR #5 | PR #5 NOT in current branch |

### 🟡 PARTIAL / NEEDS VERIFICATION

| Item | What's Done | What's Missing | Blocker |
|------|-------------|----------------|---------|
| P1.1 evidence at pushed SHA | 25/25 matrix PASS at local `6d3ff02` (pre-push mode) | Push the branch and re-run the harness at the pushed SHA without `-AllowUnpushedCommit` | Owner authorization for push |
| DB financial writer least privilege | Identity lease/ACK role tests exist | Full money-writer permission matrix, SQLSTATE 42501 tests | P1.2 |
| Encrypted bank evidence vault | Disposable rehearsal vault with AES-GCM, tamper detection (T1 PASS) | KMS/production secrets, ACL/IAM, backup/restore, malware scan | P1.3 |
| Crash/recovery regression | ACK recovery covered in operational gates | Repeat crash/recovery in native JWT harness lane | P1.4 |

### 🔴 HOLD / NOT STARTED

| Item | Reason | Dependency |
|------|--------|------------|
| Real payOS QR payment | Safety gates P0/P1 must pass first | All P0 + P1 items, Owner explicit approval |
| Independent bank statement verification | No real money flow yet | Real payOS payment first |
| Spec Kit (PR #5) integration | Branch divergence: PR #10 and `wip/phase-2g2` diverged at `e05049d` | Merge choreography needed |
| Phase 2G.5 move-out end-to-end | Partial staging tests exist | Final bill, debt, E/W meter, 3-sig checklist integration |
| D01/D02 issued invoice correction | Golden candidate tests exist | Apply to actual issued invoices, 28/29/30/31 parity |
| D03 service pricing in official invoices | Staging tests exist | Integration into issued invoices |
| H4 settlement (D04 full business flow) | Top-up obligation tracking code exists | Cash-backed deadline ≤1 month, credit OR refund (exactly once), dual approval |
| UI/Frontend | Unknown — need inventory | Determine what exists in repo |

## Known Issues Fixed (verify on HEAD)

1. ✅ **CS0117 Convert.TryFromHexString** — Fixed at `0702e0db` with `Convert.FromHexString`
2. ✅ **MSB3027/MSB3021 DLL locks** — Fixed with detached worktree in `verify-payos-d04-local.ps1`
3. ✅ **PS5.1 vault path check** — Fixed at `6622b3a`
4. ✅ **P1.1 blockers (run 20–22):** ambient transactional UoW from `app.UseUnitOfWork()` overriding `[UnitOfWork(isTransactional:false)]` → 409; `Forbid()` → 302 login redirect; facade minted a new archive reference per re-review → 403 — all fixed at `f14745f`

## Environment Configuration

- **Test DB (D04):** `127.0.0.1:15432` ✅ Safe to use
- **Identity DB (firstboot):** `127.0.0.1:15433` ⚠️ DO NOT TOUCH
- **Production DB:** `127.0.0.1:5432` 🚫 FORBIDDEN
- **Runtime:** Windows PowerShell, .NET 10
- **Scripts:** `etc/scripts/verify-payos-d04-local.ps1 -WriterOnly`; native JWT matrix: `etc/scripts/verify-payos-d04b-native-jwt-matrix.ps1 [-AllowUnpushedCommit]`

## Critical Rules in Effect

- 🚫 No production migration, no real payOS, no real bank transfer
- 🚫 No merge to `main`, no force push without explicit approval  
- 🚫 No raw JWT/secrets/bank statements in logs or commits
- ✅ Fail-closed for all financial code
- ✅ Detached worktree to avoid DLL locks
- ✅ Owner approval required for destructive operations

## Next Immediate Actions (P1)

1. ✅ P1.1 native D04 financial HTTP auth matrix — 25/25 PASS at `6d3ff02` (pre-push mode, run 27, `payos-20261111-000218`)
2. ⏳ Push `feat/local-safe-startup-20261109` (needs explicit Owner authorization), then re-run the matrix WITHOUT `-AllowUnpushedCommit` to re-confirm evidence at the pushed SHA
3. ⏳ P1.2 DB financial writer least privilege (SQLSTATE 42501)
4. ⏳ P1.4 crash/recovery regression in the native JWT lane
5. ⏳ P1.3 operational vault, P1.5 webhook/merchant staging readiness

---
**Last Updated:** 2026-11-11  
**Verified Against:** local checkout + run 27 log/TRX evidence  
**Confidence:** High for git state, `-WriterOnly` (19/19), FULL bundle (118/118) on `7621d8a`, and the native JWT matrix (25/25) at local `6d3ff02` (pre-push caveat noted above); least-privilege writer and production vault remain HOLD
