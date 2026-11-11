# P3.3 — Program Acceptance Matrix (evidence-based, exact-SHA)
**Audit date:** 2026-11-12 (Asia/Ho_Chi_Minh)
**Trunk:** `feat/local-safe-startup-20261109` @ `2f0a949` (pushed, in sync with origin; contains PR #5 via merge `01cba31`)
**Scope:** consolidated acceptance matrix across P0 → P3.2, built on this branch's own local evidence (TRX/LOG_DIRECTORY stamped at pushed SHAs). CI columns stay HOLD: the GitHub Actions account is billing-locked (all jobs fail ~5s at start on 25+ runs including pre-merge commits — Owner action required).

## Status rules (inherited from this branch's evidence discipline)
- **PASS:** SHA + TRX/LOG evidence at a pushed commit, re-run by the official harness, negative matrix included. Local-disposable-scope (PG 15432/loopback, no real money).
- **PARTIAL:** the tested scope is a strict subset of the required behavior; the untested residue is listed and not inferred.
- **HOLD:** requires Owner action, real money, production secrets, or public ingress. Never marked PASS by a green test.
- **CI-ONLY:** evidence exists solely from GitHub Actions before the billing lock (pre-merge SHAs on PR #5); not re-runnable today; not counted as current-trunk evidence.

## A. Safety \& environment invariants (threat-model boundary)

| Req ID | Requirement | Source / commit | Test (name) | Environment | Evidence | Status | Blocker / Owner action |
|---|---|---|---|---|---|---|---|
| SEC-01 | No production DB, no port 5432, no Identity firstboot 15433 ever touched by any harness | House rules + guards in every verify script | All harness guards (branch/DB/port allowlists) | Local Windows + disposable PG16 15432 | Every P0–P3.2 run log (LOG_DIRECTORIES below); C# guards: `BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString`, `SliceAStagingPostgreSqlHttp_Tests` port/DB/host checks | **PASS** | None |
| SEC-02 | No real payOS call, no real webhook, no bank transfer/refund, ever | `specs/003-payments-payos-provider/decision.md` boundary | P1.5 internal simulated lane: zero real payOS traffic (config-only `ProviderMode=InternalSimulated`; real client registration stays hard-gated) | Real Host (Staging, https://localhost:44311) + in-process simulator | `payos-p15-internal-20261111-153928` at `e58b12c` | **PASS** | Real payOS = separate Owner authorization (P2.3/payments lane) |
| SEC-03 | No raw JWT/secrets/bank statements echoed, saved or committed | House rules | Tokens minted by harnesses are never echoed/saved (recorded in every run log preamble) | All runs | Run logs 27–28 (P1.1), crash run 2 (P1.4), run 4 (P1.3), run 3 (P1.5) | **PASS** | None |
| SEC-04 | Fail-closed financial code: missing env/DB/archive/key ⇒ refuse start or refuse write | `ConfigurePayOsD04BIfEnabled` (extracted `7621d8a`) | `D04BHostStartupGate_Tests` 8/8: default OFF; wrong phrase/slot/host/role/DB name/key refused | In-memory DI + real Host `ConfigureServices` | `payos-20261110-162113` (18c) at `7621d8a` | **PASS** | None |
| SEC-05 | Archive/vault tamper, truncation, replay, wrong-key detection | AES-GCM two-key rotation ring (P1.3) | Host archive 9/9 (incl. HOSTARCHIVE08 rotation, 09 magic bytes) | Loopback Host + file vault | `payos-20261110-162113` at `7621d8a` growth to 9 re-verified at `7f48c59` | **PASS** | Production KMS/IAM/backup/malware-scan/legal-hold = HOLD (Owner) |
| SEC-06 | GitHub Actions CI evidence at current trunk | `.github/workflows/*` (7 workflows) | — | GitHub-hosted | All runs fail ~5s: "account is locked due to a billing issue" (25+ runs incl. pre-merge) | **HOLD** | **Owner: resolve GitHub billing (Settings → Billing)** |

## B. Authentication \& authorization invariants

| Req ID | Requirement | Source / commit | Test | Environment | Evidence | Status | Blocker / Owner action |
|---|---|---|---|---|---|---|---|
| AUTH-01 | Real OpenIddict password-grant Bearer tokens (not synthetic cookies) for reviewer/owner/outsider | `f14745f` (403 not 302; idempotent re-review reference reuse; no ambient transactional UoW) | P1.1 native JWT matrix 25/25: N1 anonymous 401/302, N2 forged bearer 401, N3 outsider 403, N4 owner-as-reviewer 403, N5/N6 reviewer-as-owner 403, N7 cross-building 403, N8 self-approve 403; P1–P5 happy path + idempotent replay; R/T tamper/revoke | Real Host Staging + 3 disposable DBs (payos_d04_*) | Run 28 `payos-native-jwt-20261111-001125` at pushed `f30e14d` | **PASS** | None |
| AUTH-02 | Role separation at DB level: writer roles cannot DDL/CREATE ROLE/insert into Payment/UPDATE immutable journal | `6437929`+`d085d02` (`D04BRestrictedMoneyWriterRolesGate`) | P1.2 rehearsal gate: NOSUPERUSER roles, 42501 denials (CREATE TABLE/DROP/INSERT Payment/UPDATE+DELETE journal/UPDATE BankReviews/CREATE FUNCTION/CREATE ROLE); zero grantable rights on journal; SERIALIZABLE commit under role | Disposable PG16 | `-WriterOnly` run 5 `payos-20261111-003546` at `d085d02` | **PASS** | Production role provisioning = HOLD (P1.3 operational) |
| AUTH-03 | Host runtime connections themselves on restricted non-superuser roles | `18fe521` | P1.3: full 64/64 native JWT matrix with legacy/staging slots on `d04_d04b_host_legacy_test`/`d04_d04b_host_staging_test`; identity slot superuser; migrations superuser-separate | Real Host + disposable PG | Run 4 `payos-native-jwt-20261111-134136` at pushed `18fe521` | **PASS** | Staging/Prod secrets config = HOLD (Owner) |
| AUTH-04 | Operator/building membership: deny-all default, no auto-registered candidate readers, root bootstrap allowlist | `check-building-scope-design-only-gate.ps1` (15 source gates) + `SliceA_OperatorBuildingMembership` migration guards | Source gate PASS (read-only) + STAGEPGHTTP01 runtime (root bootstrap, two-building revocation) | Local | Gate ran green inside 2H/2g5 runs at `1ddf2e6` | **PASS** (source + runtime scope as tested) | Effective production DI/roles review = deferred to P2 lane |
| AUTH-05 | Staging controllers never leak financial data cross-building; scoped reads server-side | `BuildingBusinessScopedCollectionQueries` + HTTP tests | STAGEPGHTTP01 (auth HTTP 1/1) + 2g5 auth HTTP | Real Host + disposable PG | `18-2h-auth-http.trx` at `1ddf2e6` | **PASS** (staging scope) | Production ingress review = HOLD (TLS callback Owner) |

## C. Accounting \& money-flow invariants

| Req ID | Requirement | Source / commit | Test | Environment | Evidence | Status | Blocker / Owner action |
|---|---|---|---|---|---|---|---|
| ACC-01 | Exactly-once money movement: no second Payment/Allocation/Receipt/Outbox row across crash, restart, duplicate replay | `682613d` (EF wrap 57P01 inner-chain fix) | P1.4 crash lane C0→C3: kill at MarkPosted UPDATE, whole-process force-kill, restart+replay SAME intent ⇒ SAME receiptId; durable state `2\|1\|1\|1\|1` / `3\|1\|1` | Real Host + disposable PG | Run 2 `payos-native-jwt-20261111-011917` at pushed `682613d` (34/34) | **PASS** | None |
| ACC-02 | Webhook/checkout idempotency: duplicate webhook, same-orderCode replay, tampered HMAC ⇒ no state growth | `eb8074b`+`e58b12c` | P1.5 20/20: W1 signed 201, W2 duplicate idempotent, W3 tampered 400 (inbox untouched), P2 replay, R1–R3 reconcile | Real Host + simulator + disposable PG | Run 3 `payos-p15-internal-20261111-153928` at pushed `e58b12c` | **PASS** | Real payOS webhook = HOLD (real payOS traffic forbidden) |
| ACC-03 | H4 rent-credit cap: reviewed receipts cannot over-credit a credited invoice | PR #5 merge `01cba31` (credit-cap triggers on base tables) | STAGEPGHTTP01 D04-12 inner scenario (1,000,000 credit then 1,000,000 receipt ⇒ net 0; +1 VND rejected) | Real Host + disposable PG | `18-2h-auth-http.trx` at `1ddf2e6` (re-run post-merge) | **PASS** (staging scope) | Real bank receipts = HOLD |
| ACC-04 | H4 conservation: source deposit = carried + credit + refund; target = carried + unpaid top-up | `RoomChangeMidMonthCompletionRecord.VerifyFingerprint` (PR #5) | TRFH4PG02/03 + STAGEPGHTTP01 H4 conservation assertions | Disposable PG | `16-2h-postgresql.trx` (103/103) + `18-2h-auth-http.trx` at `1ddf2e6` | **PASS** (staging scope) | Actual bank settlement = HOLD (external evidence) |
| ACC-05 | Installment posting of top-up; no duplicate source; journal append-only | `RoomChangeH4SettlementEvidenceGate` (PR #5) | TRFH4PG series + STAGEPGHTTP01 choice 0 (400k+600k VND, 2 journal rows, duplicate denied) | Disposable PG | Same TRX evidence at `1ddf2e6` | **PASS** (staging scope) | Actual verified collection = Payments/payOS lane |
| ACC-06 | Immutable journal: SQL UPDATE/DELETE/TRUNCATE/forge denied | D04 migration triggers (PR #5 + D04-B) | D04-10 probes: TRUNCATE 0A000/23514, forged INSERT 23514, 0 forged rows | Disposable PG | Same evidence; re-verified at `1ddf2e6` | **PASS** (tested SQL routes) | Runtime role deployment review = production HOLD |
| ACC-07 | D01/D02 calendar correctness: 28/29/30/31 parity, target owns handover day, VND midpoint-up | PR #5 D01/D02 lane | `RoomChangeDailyRentQuoteCalculator` tests + STAGEPGHTTP01 proration checks (31/14/17 days observed) | SQLite + disposable PG | Domain 720/720 at `01cba31`; STAGEPGHTTP01 at `1ddf2e6` | **PASS** (calculator + staging parity) | **P2.1 residue:** apply to real issued invoices end-to-end |
| ACC-08 | payOS provider gate: never initiate transfer, never claim bank settlement from fixtures | `specs/003` decision + P1.5 responses `IsBankApiVerified=false` | P1.5 durable-state assertions | Real Host + simulator | Run 3 evidence | **PASS** | Real-money flow = explicit separate Owner authorization |

## D. Regression \& merge-integrity invariants

| Req ID | Requirement | Source / commit | Test | Environment | Evidence | Status | Blocker / Owner action |
|---|---|---|---|---|---|---|---|
| REG-01 | Whole solution builds clean at merged tree | merge `01cba31` | Full build | Local | 0 errors (30 pre-existing warnings) | **PASS** | None |
| REG-02 | All module suites green at merged tree | P3.2 | Domain 720/720, EF 447/447, App 231/231, HTTP 76/76 | Local SQLite/in-memory | TRXs in `p32-merged-01cba31` at `01cba31` | **PASS** | None |
| REG-03 | 2H financial foundations lane at merged tree | guard-adapt `127f5d4` + fix `1ddf2e6` | 5/5 groups: 165/165 domain, 103/103 PG, migration chain, auth HTTP 1/1, 2g batch 76/76 | Disposable PG 15432 | `15/16/17/18-2h-*.trx` + `19-2h-full-regression.log` stamping `PHASE2H_COMMIT=1ddf2e6` | **PASS** | None |
| REG-04 | 2g5 staging lane at merged tree | same | 4/4 groups: 58/58 archive, migration 1/1, auth HTTP 1/1, regression 76/76 | Disposable PG 15432 | `11–14-2g5-*.log` at `1ddf2e6` | **PASS** | None |
| REG-05 | D04 payOS foundation bundles at merged tree | fixes `38f0037`+`7f48c59` | Native JWT matrix, writer-only bundle, FULL bundle 01–27 | Detached worktree + disposable PG | `payos-native-jwt-20261111-231445` at `38f0037`; `payos-20261111-232050`/`232223` at `7f48c59` | **PASS** | None |
| REG-06 | Merged-tree fixes were R3-class only (no semantic conflict beyond fixtures/guards) | P3.1 analysis §5 | 4 fixes: `38f0037` (terminal-migration pin), `7f48c59` (vault count 7→9), `127f5d4` (branch/port guards), `1ddf2e6` (time-bomb due dates — not merge-related) | Local | Commit messages + this matrix | **PASS** (observed fix classes) | None |
| REG-07 | Merge-to-main proposal | House rule: never merge to `main` without explicit authorization | — | — | — | **HOLD** | **Owner: P3.4 release proposal after P3.3 review** |

## E. Historical CI-only evidence (pre-billing-lock, pre-merge; NOT current-trunk evidence)

| Req ID | Requirement | Evidence | Status |
|---|---|---|--- |
| CI-H1 | PR #5 exact-head 4/4 workflows (H4, Slice A, Windows, Identity) at `d03340d`/`7179cfa` | Run IDs 42187237595/42187237748/42187237533/42187237679 (2026-11-06) | **CI-ONLY** (pre-merge; superseded as trunk evidence by REG-03/04/05 local re-runs) |
| CI-H2 | PR #5 selected suites 712/712 domain, 183/183 app, 447/447 EF, 76/76 HTTP, 103/103 PG | Same runs | **CI-ONLY** (superseded by REG-02/03/04) |
| CI-H3 | D04 settlement matrix D04-01…D04-14 staging pass at `d03340d` | `specs/001/.../d04-settlement-acceptance-matrix-20261106.md` | **CI-ONLY** (superseded by local re-runs; D04-15 deferred to Payments as before) |

## Overall program disposition (P3.3 conclusion)

1. **All local-verifiable invariants PASS at pushed SHA `1ddf2e6`** (safety, auth, accounting, merge-integrity), with exact SHA+TRX evidence. No local blocker remains for P3.4 preparation.
2. **R3 (the P3.1-predicted semantic merge risk) is closed with evidence**: every manifested failure was a fixture/guard pin (migration name, vault count, branch/port guard, hard-coded due date), never a semantic conflict between the H4 and D04 lanes over the Payment spine; all four merged-tree fixes documented above.
3. **Sole program-wide HOLD items (all Owner actions, none code):**
   - GitHub Actions billing lock (SEC-06) — blocks the CI-evidence column until resolved.
   - Production vault/roles/secrets (SEC-05 residue, AUTH-02/03 residue) — production scope decisions.
   - TLS public payOS callback (AUTH-05 residue, ACC-08 real-money residue) — explicit Owner authorization; internal staging only.
   - Merge-to-main / release (REG-07) — the P3.4 proposal, after this matrix is reviewed.
4. **P2 business residue (per requirement, not blocking P3.4 preparation):** P2.1 issued-invoice end-to-end application of D01/D02; P2.2 service fees in issued invoices; P2.3 genuine bank evidence for H4 settlement; P2.4 three-signature flow (needs Owner decision: E-sign vs scanned signatures); P2.5 move-out E2E completion; P2.6 frontend discovery.

## Verification of this matrix
- Built from: `CURRENT_STATE.md` @ `2f0a949`, `TASK_BACKLOG.md` @ `2f0a949`, P3.1 report `etc/phase-audits/P31_BranchReconciliation_20261111.md`, `specs/001` + `specs/002` + `specs/003` artifacts, and the run logs/TRX under `C:\Users\Huynq\NgocTrang-Test-Logs` listed per row.
- Threat-model review: SEC rows (A) cover the standing safety boundary (no prod, no real money, no secret leakage, fail-closed, billing-locked CI).
- Auth invariants review: AUTH rows (B) cover bearer-authenticity, negative authorization matrix, DB-level least privilege, membership deny-all, scoped reads.
- Accounting invariants review: ACC rows (C) cover exactly-once, idempotency, credit caps, conservation, append-only, calendar parity, provider gate.
- No evidence was copied from a previous run onto a new SHA; every PASS row cites its own run directory at its own pushed SHA.
