# TASK BACKLOG — NgocTrangHouseManagementSystem
**Created:** 2026-11-10  
**Priority Order:** P0 → P1 → P2 → P3  
**Rule:** Only mark DONE when SHA + TRX evidence exists

---

## P0 — Nghiệm thu an toàn code hiện tại (KHÔNG đụng tiền thật)

### P0.1 ✅ Audit Git State
**Status:** DONE  
**Evidence:** Git commands executed, CURRENT_STATE.md created  
**Result:** HEAD at `6622b3a` when audited; branch now at `fc322e8` (P0.3 re-run) and `7621d8a` (P0.5), 5 commits ahead of handoff baseline `0702e0d`

### P0.2 ✅ Verify Test Script Safety
**Status:** DONE  
**Evidence:** Script read; guards observed in two real runs: refuses a SHA that origin does not have or a dirty tracked tree, builds from a detached worktree under the run's log directory, creates only its own 15432 container and removes only that one. After both runs the containers on 15433 and 5432 were still listed and the temporary worktree was gone.  
**Definition of Done:**
- Read `etc/scripts/verify-payos-d04-local.ps1`
- Confirm guards for DB 15432 only (not 15433/5432)
- Confirm detached worktree to avoid DLL locks
- Confirm `-WriterOnly` flag exists and works
- Confirm no auto-kill of active Host PID

**Blocker:** None  
**Dependency:** None

### P0.3 ✅ Run D04 Isolated Writer-Only Test
**Status:** DONE  
**Evidence:** SOURCE_COMMIT `fc322e8236c9d444f7b021251a923222c4521765`, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261110-143141`, 4 TRX: http-preflight 1/1, protected-archive (18a) 2/2, host-archive (18b) 7/7, real-writer (19a) 1/1 — 11 passed, 0 failed, 0 not executed. Baseline on `6622b3a`: `payos-20261110-141911`, 4 passed.  
**Scope note:** this is the `-WriterOnly` bundle with synthetic signed test cookies; the full bundle and native OpenIddict HTTP auth are not covered here.  
**Command:** `powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-payos-d04-local.ps1 -WriterOnly`  
**Definition of Done:**
- TRX file generated with pass/fail/skip counts
- Log includes SOURCE_COMMIT matching `6622b3a` or later
- Log includes LOG_DIRECTORY path
- All tests in scope: auth HTTP preflight, archive 18a/19a, writer permissions, negative matrix, idempotency, rollback
- Capture exact first failure if any

**Blocker:** Must complete P0.2 first  
**Dependency:** P0.2

### P0.4 ✅ Fix First Reproducible Failure
**Status:** NOT NEEDED — no failure reproduced in the two `-WriterOnly` runs (`6622b3a`, `fc322e8`)  
**Definition of Done:**
- Identify root cause of first failing test
- Fix code, add negative regression test
- Re-run full suite
- Update CURRENT_STATE with new SHA and evidence

**Blocker:** P0.3 must run first  
**Dependency:** P0.3

### P0.5 ✅ Verify Encrypted Archive Gate
**Status:** DONE  
**Evidence:** SOURCE_COMMIT `7621d8a0407d035faf52278cd71abb3661eb23ff`, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261110-162113`, 5 TRX — http-preflight 1/1, protected-archive (18a) 2/2, host-archive (18b) 7/7, **host-startup-gate (18c) 8/8**, real-writer (19a) 1/1 = 19 passed, 0 failed, 0 not executed. Cleanup verified: temp container and worktree removed; 15433 and 5432 untouched.  
**What 18c covers (`D04BHostStartupGate_Tests`, commit `7621d8a`):** Host D04-B gate extracted into `ConfigurePayOsD04BIfEnabled` (pure extraction) and tested by reflection + in-memory DI — default OFF registers nothing; Production/Development/wrong-or-missing activation phrase/merchant-on all throw `D04-B requires dedicated no-merchant, three-DB disposable staging.`; every slot must be loopback 15432 + exact DB name + postgres + disposable-only (5432, user DB name, swapped slot, remote host, wrong role refused); archive root must be absolute and key a valid base64 256-bit value (malformed and 16-byte keys refused, no directory created); DENIED without a registered archive; success path registers exactly archive singleton + orchestrator/manual-evidence/coordinator scoped + migration DbContext and no hosted service; real Host `ConfigureServices` still delegates to the gate with the same messages. Also covers the archive requirements from `fc322e8`: triple-DB-only activation, fail-closed default, tamper/truncation/replay detection, digest verification, restart with same/wrong key.  
**Definition of Done:**
- ✅ Confirm archive only activates in triple-DB isolated staging (18c)
- ✅ Confirm fail-closed by default (18c GATE02/03/06)
- ✅ Confirm tamper/truncation/replay detection (18b)
- ✅ Confirm role scope enforcement (18c GATE01/04 disposable role gate + 19a runtime privileges)
- ✅ Confirm digest verification (18b)
- ✅ Test shutdown/restart persistence (18b)
**Command:** `powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-payos-d04-local.ps1 -WriterOnly`

**Blocker:** P0.4 must pass  
**Dependency:** P0.4

### P0.6 ✅ Run Full D04 Local Bundle
**Status:** DONE  
**Evidence:** SOURCE_COMMIT `7621d8a0407d035faf52278cd71abb3661eb23ff`, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261110-162625` — full script (no `-WriterOnly`), steps 01–27 all PASS, 8 TRX / 118 passed / 0 failed / 0 not executed (domain 8, application 48, pg 43, http-preflight 1, protected-archive 2, host-archive 7, startup-gate 8, real-writer 1). Temp container and worktree removed; 15433 and 5432 untouched.  
**Scope note:** synthetic signed cookies; native OpenIddict login and business acceptance remain HOLD.

---

## P1 — D04 Native Financial Security (UAT Prerequisites)

### P1.1 ✅ Native Financial HTTP Auth with Real JWT
**Status:** DONE — official evidence at pushed SHA `f30e14d`  
**Evidence:** Run 28 (no pre-push mode, SHA verified on origin), SOURCE_COMMIT `f30e14d17a0859d35b7548c359b1db116efeea3b`, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\payos-native-jwt-20261111-001125` — 25/25 matrix steps PASS + TRX `payos-native-jwt-provision.trx` 1/1 + durable psql state (LEGACY `2|1|1|1`, STAGING `3|1|2`). Earlier pre-push run 27 at `6d3ff02`: `payos-native-jwt-20261111-000218`. Real Host on https://localhost:44311 in Staging, real OpenIddict password-grant Bearer tokens for `reviewer-uat`/`owner-uat`/`outsider-uat` (token values never echoed/saved). Temp container + worktree removed; 15433/5432 untouched.  
**What was fixed (source, commit `f14745f`):** (1) removed `app.UseUnitOfWork()` whose reserved ambient transactional UoW silently overrode `[UnitOfWork(isTransactional:false)]` and made every review 409; (2) `Forbid()` → `StatusCode(403)` so denied actors get 403, not a 302 login redirect; (3) archive-backed facade reuses the persisted `ExternalBankProofReference` on idempotent re-review instead of minting a new file that could never match.  
**Harness fixes:** `92076b2` `-AllowUnpushedCommit` mode, `7dbda39` R1 A2-reference, `bf193a3` here-string quotes, `95053a3` Invoke-Scalar array, `6d3ff02` settlement table name.  
**Matrix covered:** N1 anonymous 401/302, N2 forged bearer 401/302, N3 outsider review 403, N4 owner-as-reviewer 403, N5/N6 reviewer-as-owner approve/post 403, N7 cross-building 403, N8 self-approve 403; P1 review 201, P2 idempotent re-review, P3 approve 201, P4 post 201+acknowledged, P5 replay no double money; R1 review A2, T1 tampered archive blocks approve, R2 approve, R4 post after mid-run grant revoke 403.  
**Definition of Done:**
- ✅ D04 isolated DBs: `payos_d04_identity_test`, `payos_d04_legacy_test`, `payos_ef_migration_test` on 15432
- ✅ 3 OpenIddict accounts provisioned (reviewer/owner/outsider) with real password grants
- ✅ `ReviewD04BankEvidence` → Reviewer, `ApproveD04Posting` → Owner
- ✅ Positive: reviewer review/approve/post chain 201 with ids
- ✅ Negative: cross-building, owner-as-reviewer, reviewer-as-owner, self-approve, forged bearer, revoked grant mid-run — all denied
- ✅ Real Host + real Bearer (NOT synthetic cookies)
- ✅ Re-confirmed at pushed SHA (run 28, branch == origin at `f30e14d`)

**Blocker:** None  
**Dependency:** P0.5 ✅

### P1.2 ⏳ DB Least Privilege for Money Writer
**Status:** TODO  
**Definition of Done:**
- Extend beyond Identity lease/ACK tests
- Validate app cannot bypass approval or update SQL financial tables outside intended permissions
- Test PostgreSQL permission enforcement with SQLSTATE 42501
- Verify transactional commit behavior under restricted role
- Keep migration role separate from runtime role

**Blocker:** P0 must be clean  
**Dependency:** P0.5

### P1.3 ⏳ Operational Bank Evidence Vault
**Status:** TODO (currently disposable rehearsal only)  
**Definition of Done:**
- KMS integration for encryption keys
- Secret rotation mechanism
- Encrypted durable immutable file storage
- Reviewer-scoped ACL/IAM
- File validation: PDF/JPG/PNG, size limits, malware scanning
- Backup/restore/retention policy
- Legal-hold support
- Tamper detection
- Auditable independent bank credit verification
- Refuse D04-B startup if vault provider missing
- Never send raw bank statements to LLM

**Blocker:** Architecture decision needed  
**Dependency:** P1.1, P1.2

### P1.4 ⏳ Crash/Recovery Regression (Maintain PR #9 PASS)
**Status:** TODO  
**Definition of Done:**
- Re-run kill-after-legacy-commit-before-ACK test
- Test restart/duplicate webhook/replay
- Verify no double Payment/Allocation/Receipt/Outbox
- Confirm results still PASS after PR #10 code additions

**Blocker:** P0 must be clean  
**Dependency:** P0.5

### P1.5 ⏳ Webhook/Merchant Staging Readiness
**Status:** TODO  
**Definition of Done:**
- QR checkout creates intent with correct permissions
- Signature verification on raw body
- `paymentLinkId` and `orderCode` handling
- Idempotent webhook inbox/reconciliation
- Timeout handling with outbox pattern
- TLS HTTPS public callback URL
- Server-side secrets (never client-side)
- Staging test WITHOUT calling real payOS

**Blocker:** Architecture decision needed  
**Dependency:** P1.1, P1.4

---

## P2 — Complete Phase 2H/2G Business Logic (Still HOLD)

### P2.1 ⏳ D01/D02 Invoice Correction
**Status:** TODO (golden candidates exist on PR #5, not integrated)  
**Definition of Done:**
- Apply "target owns handover day" policy
- HTTP/PG parity for 28/29/30/31 days, leap year
- VND rounding per line (0.5 → up for non-negative)
- Signed pricing artifact, append-only adjustments
- Receipt reconciliation
- Concurrency/rollback/SQL bypass protection
- Test against **issued invoices**, not just staging

**Blocker:** PR #5 integration needed  
**Dependency:** P3.1 (branch merge)

### P2.2 ⏳ D03 Service Pricing in Official Invoices
**Status:** TODO (staging tests exist on PR #5)  
**Definition of Done:**
- Pricing by `RoomId + ServiceId + effective version + unit`
- Actual occupant count from verified residency list
- E/W meter actual readings
- Service fees appear in **official issued invoice**
- Immutable when room/pricing changes (snapshot)

**Blocker:** PR #5 integration needed  
**Dependency:** P3.1

### P2.3 ⏳ H4 Settlement (D04 Business Flow)
**Status:** TODO  
**Definition of Done:**
- Cash-backed top-up obligation with deadline ≤1 calendar month from move date
- Track partial payment / missed deadline
- Credit to next month rent **OR** bank refund — exactly one, never both
- Refund/debt dispute reconciliation
- Dual approval: Owner + Reviewer
- Genuine bank evidence required
- Prevent false receipts

**Blocker:** P1.1, P1.2, P1.3 must pass  
**Dependency:** P1.3, P2.1

### P2.4 ⏳ D06/H6 Three-Signature Process
**Status:** TODO (bounded DOCX test exists on PR #5)  
**Definition of Done:**
- 3 signatures: Owner, Staff, Tenant/Representative
- Scan physical documents (accept scanned handwritten signatures)
- Permission and handover evidence verification
- Owner exports new contract DOCX/PDF with audit trail
- Download access control with audit

**Blocker:** PR #5 integration + E-sign or scan flow decision  
**Dependency:** P3.1

### P2.5 ⏳ Phase 2G.5 Ordinary Move-Out End-to-End
**Status:** TODO (partial staging tests exist)  
**Definition of Done:**
- Final bill generation
- Debt settlement
- Deposit offset (OI-04 rules)
- E/W meter final reading
- Checklist completion
- Room disposition
- Future reservation handling
- **Never** allow direct `Contract.End` bypass
- Cross-check OI-01 through OI-06 policy compliance

**Blocker:** Multiple dependencies  
**Dependency:** P2.1, P2.2, P2.3, P2.4

### P2.6 ⏳ UI/Frontend Inventory and Integration
**Status:** TODO  
**Definition of Done:**
- Inventory existing frontend projects in repo
- Identify tech stack (React/Angular/Vue/Blazor?)
- Map coverage: Tenant/Staff/Owner portals
- Identify missing flows: transfer, invoice, meter, contract, payment, proof upload
- Do NOT change framework or rewrite without audit

**Blocker:** Discovery needed  
**Dependency:** None

---

## P3 — Integration, Full Regression, Review/Merge

### P3.1 ⏳ Branch Reconciliation and Merge Choreography
**Status:** TODO (HIGH RISK)  
**Definition of Done:**
- Compare `wip/phase-2g2`, `integration/spec-kit-h4-security-20261105` (PR #5), and `feat/local-safe-startup-20261109` (PR #10)
- Identify merge-base: `e05049d037a4299ca17c04d2dd3a3c25f9637bfd`
- Map overlapping/duplicate migrations
- Map entity model snapshot conflicts
- Map overlapping test documentation
- Design merge order with risk matrix
- Keep PRs Draft until Owner approves
- **Do NOT** cherry-pick writer code without security migrations

**Blocker:** Manual review required  
**Dependency:** P0 and P1 should be clean first

### P3.2 ⏳ Full Regression Test Suite
**Status:** TODO  
**Definition of Done:**
- Per-SHA backend: Domain, Application, EF, HTTP tests
- `verify-2g-security-batch.ps1 -Mode Full`
- PostgreSQL scratch: full migrations → rollback → reapply
- `verify-phase-2h-financial-foundations.ps1`
- `verify-phase-2g5-staging.ps1`
- D04 isolated bundle
- Run on correct branches per test scope
- Read all script guards before running

**Blocker:** P3.1 merge must be planned  
**Dependency:** P3.1

### P3.3 ⏳ Acceptance Matrix
**Status:** TODO  
**Definition of Done:**
- Create table: Requirement ID | Source File/Commit | Test Name | Environment | Evidence Log/URL | PASS/PARTIAL/HOLD | Blocker | Owner Action Needed
- Review threat model
- Review auth invariants
- Review accounting invariants

**Blocker:** P3.2 must complete  
**Dependency:** P3.2

### P3.4 ⏳ Final Review and Release Proposal
**Status:** TODO  
**Definition of Done:**
- Exact-final-SHA regression all green
- Business acceptance from Owner
- Threat model review complete
- Propose merge/release ONLY after all HOLD items resolved or explicitly deferred by Owner

**Blocker:** Everything above  
**Dependency:** P3.3

---

## NOT IN BACKLOG (Already Fixed — Do Not Repeat)

- ❌ CS0117 Convert.TryFromHexString → Fixed at `0702e0db`
- ❌ MSB3027 DLL locks → Fixed with worktree isolation
- ❌ PS5.1 vault path check → Fixed at `6622b3a`

---

## FORBIDDEN ACTIONS (Never Add to Backlog)

- 🚫 Real payOS QR payment
- 🚫 Real bank transfer/refund
- 🚫 Production DB migration
- 🚫 Merge to `main` without approval
- 🚫 Force push
- 🚫 Enable real payOS webhook
- 🚫 Echo/log/commit raw secrets or bank statements
- 🚫 Delete Docker containers not created by test
- 🚫 Disturb DB 15433 or 5432

---

**Backlog Status Summary:**
- P0: 6/6 closed (P0.1–P0.3 done, P0.4 not needed, P0.5 startup gate at `7621d8a`, P0.6 full bundle at `7621d8a`)
- P1: 1/5 done (P1.1 official evidence at pushed `f30e14d`, run 28)
- P2: 0/6 done
- P3: 0/4 done

**Current Focus:** P1.2 — DB financial writer least privilege (SQLSTATE 42501)
