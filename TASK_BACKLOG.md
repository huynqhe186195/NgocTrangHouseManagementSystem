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

### P1.2 ✅ DB Least Privilege for Money Writer (rehearsal gate)
**Status:** DONE — evidence at pushed SHA `d085d02` (run 5, `payos-20261111-003546`)  
**Evidence:** `-WriterOnly` bundle 6 TRX / 20 passed / 0 failed; `payos-d04-real-writer.trx` 1/1 includes the new `D04BRestrictedMoneyWriterRolesGate` (commit `6437929`, fixes `b3d418f` + `d085d02`).  
**What the gate proves (in the disposable `payos_d04_legacy_test`/`payos_ef_migration_test` DBs):**
- Two runtime roles `d04_money_writer_legacy_test` / `d04_money_writer_staging_test`: NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT
- Grants match the canonical writer SQL exactly: SELECT on locked financial tables (contracts/payments/settlements/reviews/approvals/journal/outbox/rooms/floors), INSERT on journal/outbox/settlements, UPDATE on Payment/Settlement + the two outbox ACK columns, staging intent Status UPDATE only
- Positive: a SERIALIZABLE transaction with the writer's exact `SELECT … FOR UPDATE` pattern COMMITS under the restricted role
- Negative (SQLSTATE 42501): CREATE TABLE, DROP journal, INSERT invented Payment, UPDATE/DELETE immutable journal, UPDATE BankReviews, CREATE FUNCTION, CREATE ROLE (PG16: 42501)
- `information_schema.role_table_grants`: zero is_grantable=YES rights on the immutable journal (a GRANT without options only warns, so this is asserted via catalog)
- Staging role: read-back allowed; webhook inbox + intent INSERT denied 42501
- Migration/seeding remain on the separate superuser connection, never handed to the application writer  
**Definition of Done:**
- ✅ Extend beyond Identity lease/ACK tests
- ✅ Validate app cannot bypass approval or update SQL financial tables outside intended permissions
- ✅ Test PostgreSQL permission enforcement with SQLSTATE 42501
- ✅ Verify transactional commit behavior under restricted role
- ✅ Keep migration role separate from runtime role (separate connections; runtime roles created with no DDL/role-creation rights)

**Known scope limit (recorded, not hidden):** the P1.1 native-JWT Host harness and this rehearsal still connect the running writer through the disposable postgres superuser account; switching the D04-B Host connection strings to the restricted runtime roles is an operational configuration change (Staging/Production secrets), deferred to P1.3 operational-vault work where the vault/role provisioning is decided.

**Blocker:** None  
**Dependency:** P0.5 ✅

### P1.3 ✅ Operational Bank Evidence Vault (LOCAL EXTENDED REHEARSAL + RESTRICTED HOST ROLES)
**Status:** DONE — official evidence at pushed SHA `18fe521` (run 4, `payos-native-jwt-20261111-134136`, 64/64 matrix PASS, exit 0, TRX `payos-native-jwt-provision.trx` 1/1). Owner decisions recorded 2026-11-11: LOCAL rehearsal scope (production KMS / cloud malware scan / legal-hold = HOLD); restricted Host roles moved INTO P1.3 and switched NOW.  
**Evidence:** the FULL native JWT matrix (auth negatives, happy path, replay, revoke/re-grant, tamper, crash/kill/restart C0–C3) ran with the real Host's legacy/staging connections on the restricted non-superuser roles `d04_d04b_host_legacy_test`/`d04_d04b_host_staging_test`; identity slot superuser; migrations/seeding on separate superuser connections. D04BHostRuntimeRolesGate (positive locks under the role; 42501 DDL/escalation/tamper; 23514 immutable-row; cross-slot table access denial) green inside the real-writer acceptance test; archive tests 9/9 (two-key rotation gen1→gen2→gen3, magic-byte rejection, boundaries).  
**What the roles prove:**
- read-all-in-schema legacy role: the integrity triggers (BM_GuardClosedContractSource, BM_Freeze*, BM_PayOsD04ValidateReceipt, BM_ClaimGlobalBankEvidence…) query cross-domain tables under the INVOKING role privileges (no SECURITY DEFINER), so an enumerated read list cannot work — read is not the privilege boundary; INSERT/UPDATE stay narrow and DDL/role-creation is denied
- Narrow INSERT: BankReviews/OwnerApprovals (dual-actor evidence), DepositSettlements/ReceiptJournal/Outbox (writer), BankEvidenceClaims (trigger-inserted global bank-reference claims under invoking role; rows immutable 23514)
- Narrow UPDATE: Payments/DepositSettlements (EF Complete), BankReviews/OwnerApprovals/OperatorBuildingGrants/Buildings/Contracts/Rooms/Floors (PG16: SELECT FOR SHARE/FOR UPDATE requires UPDATE privilege — probed empirically; mutation of immutable evidence stays 23514-blocked, privilege is not mutation), Outbox ACK columns only (AcknowledgedAtUtc/AttemptCount)
- Staging role: SELECT intents/inbox; UPDATE (Status,ConcurrencyStamp,ExtraProperties) on intents only (EF MarkPosted under ConfigureByConvention); inbox read-only
**Empirical PG16 findings recorded (both discovered by this task):**
- SELECT FOR SHARE / FOR UPDATE requires the UPDATE privilege (SELECT alone → 42501)
- Trigger-executed SQL runs under the invoking role's privileges; trigger INSERTs (bank-reference claims) need INSERT granted, trigger SELECTs (closed-contract guards) need read access
**Definition of Done (local rehearsal scope, per Owner decision):**
- ✅ Secret rotation mechanism (two-key ring)
- ✅ Encrypted durable immutable file storage (AES-GCM, readback verified)
- ✅ File validation: PNG/JPEG/PDF magic + size limits (built-in; production malware scan = HOLD)
- ✅ Tamper detection (binding + SHA256 + FixedTimeEquals)
- ✅ Refuse D04-B startup if vault provider missing (fail-closed)
- ✅ Restricted Host runtime roles (moved into P1.3 by Owner decision)
- ✅ Never send raw bank statements to LLM
- HOLD: KMS integration, reviewer-scoped IAM ACL, backup/restore/retention, legal hold, cloud malware scanning, auditable independent bank credit verification, production role provisioning (production vault)

**Blocker:** None  
**Dependency:** P1.1 ✅, P1.2 ✅

### P1.4 ✅ Crash/Recovery Regression (Maintain PR #9 PASS)
**Status:** DONE — evidence at pushed SHA `682613d` (run 2, `payos-native-jwt-20261111-011917`)  
**Evidence:** 34/34 matrix steps PASS + TRX `payos-native-jwt-provision.trx` 1/1, `SOURCE_COMMIT=682613d3c74ede9a36e19181aa75e09199c0003a` (== origin, no pre-push warning). Real Host killed and restarted in the native JWT lane (IntentB, Building B). Temp container + worktree removed; 15433/5432 untouched.  
**What the crash lane proves:**
- C0: reviewer+owner granted to Building B mid-run (after N7 proved it grants nobody) → full review → approve chain 201
- C1: staging trigger terminates the ACK backend exactly at the MarkPosted UPDATE (2→3): the post returns `LegacyCommittedAckPendingRetrySameIntent` (retriable) with Payment+settlement+journal+outbox already committed; durable crash state `2|1|1|0` = completed Payment, exactly one settlement, exactly one journal, zero acknowledged outbox; staging intent still ConfirmedPendingPosting
- C1b: whole Host process tree force-killed (taskkill /T /F) — genuine dead process with a committed, unacknowledged receipt
- C2: real Host restarted; replay of the SAME intent returns the SAME receiptId with `LegacyCommittedAndStagingAcknowledged`; recovered state `2|1|1|1|1` (payment|settlement|journal|ackedOutbox|outboxRows) and staging `3|1|1` (intent Posted, inbox reconciled, exactly one inbox row)
- C3: duplicate replay idempotent — same receiptId, no state growth, webhook inbox untouched
- No second Payment/Allocation/Receipt/Outbox row ever appears across crash, restart and duplicate replay
**Source bug found and fixed (commit `682613d`):** EF Core (NpgsqlExecutionStrategy) wraps PostgresException 57P01 and other transport failures in `InvalidOperationException("...transient failure")` with the transport exception only in the inner chain; the coordinator's direct-type catch filter missed it and the designed retriable AckPending outcome surfaced as 409. The filter now walks the inner exception chain; integrity mismatches still propagate.
**Definition of Done:**
- ✅ Re-run kill-after-legacy-commit-before-ACK test (C1/C1b, native JWT HTTP lane)
- ✅ Test restart/duplicate webhook/replay (C2/C3)
- ✅ Verify no double Payment/Allocation/Receipt/Outbox (durable-state psql assertions)
- ✅ Confirm results still PASS after PR #10 code additions (all previous P1.1 steps re-passed in the same run)

**Blocker:** None  
**Dependency:** P0.5 ✅

### P1.5 ✅ Webhook/Merchant Staging Readiness (internal simulated lane)
**Status:** DONE (internal staging scope per Owner decisions; TLS public callback = HOLD)  
**Evidence:** Official run 3 at pushed SHA `e58b12c` (== origin): 20/20 matrix steps PASS, TRX `payos-p15-internal-provision.trx` 1/1, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\payos-p15-internal-20261111-153928`. Real Host (Staging, https://localhost:44311) with native OpenIddict password-grant tokens, config-only `ProviderMode=InternalSimulated`, loopback checkout → signed webhook → reconcile, ZERO real payOS traffic. Durable state: CHECKOUT `1|1|t|1`, WEBHOOK `0|1`, FINAL `2|1|2|2`, legacy Payment stays Pending.
**Owner decisions:** (a) internal lane provider = in-process simulator (deterministic `SIM-P15-<orderCode>` links; the real payOS client stays the default registration and remains hard-gated); (b) timeout/outbox DoD item closed with a new staging outbox table (`BuildingManagementPayOsStagingOutbox`, dispatcher with bounded backoff → RequiresReview); (c) internal staging only — no public callback URL.
**Fixes exposed by the verification runs:** `eb8074b` (.NET config arrays bind only from indexed child keys — `BuildingManagement__Bootstrap__RootUserIds__0`, not a comma-separated scalar), `e58b12c` (payOS staging controller: `AbpAuthorizationException` → 403, never an opaque 500; same fix class as `f14745f`).
**Definition of Done:**
- ✅ QR checkout creates intent with correct permissions (P1/P2 via real Host; N3 outsider 403, N4 cross-building 403)
- ✅ Signature verification on raw body (W3 tampered HMAC → 400, inbox unchanged)
- ✅ `paymentLinkId` and `orderCode` handling (deterministic `SIM-P15-<orderCode>` link id, same-orderCode replay idempotent, orderCode-scoped webhook→intent matching)
- ✅ Idempotent webhook inbox/reconciliation (W2 duplicate, R2 replay — exactly one inbox row, no state growth)
- ✅ Timeout handling with outbox pattern (new staging outbox table; attach + reconcile kinds, `2|1|2|2` final state; dispatcher backoff → RequiresReview covered by unit tests)
- ⏸️ TLS HTTPS public callback URL — **HOLD per Owner decision** (internal staging only, no public callback; loopback URLs on the isolated Host)
- ✅ Server-side secrets (never client-side) (checksum/API keys server-side env only; synthetic values, tokens never echoed/saved)
- ✅ Staging test WITHOUT calling real payOS (in-process simulator; zero real payOS traffic — loopback only)

**Blocker:** None (TLS public callback remains HOLD until Owner authorizes public ingress)
**Dependency:** P1.1 ✅, P1.4 ✅

---

## P2 — Complete Phase 2H/2G Business Logic (Still HOLD)

### P2.1 ✅ D01/D02 Invoice Correction — COMPLETE (`d36edb5`, pushed)
**Status:** DONE on `feat/p2-1-issued-invoice-correction`. Domain planner, PG concurrency/rollback proof and per-month-length HTTP parity all green.

**Delivered (`24216e2`)**
- `RoomChangeIssuedInvoiceCorrectionPlanner` (`modules/buildingmanagement/src/BuildingManagement.Domain/RoomChanges/`): binds an approved D01/D02 quote to REAL `MonthlyIssuedInvoiceRecord`s and produces the ordered append-only `MonthlyInvoiceAdjustmentRecord` proposals (source credit → target debit).
  - Recalculates the quote from its own inputs; rejects any record whose outputs disagree (no caller arithmetic, no silent default).
  - D01 target-owns-handover-day partition, month coverage asserted.
  - Whole-VND per line; source rent below captured receipts refused.
  - Never rewrites issued invoice / meter evidence / receipts.
  - Deterministic adjustment ids → replay yields identical proposals, not a second correction.
  - `VerifyAgainst()` detects drift (reissued invoice, new receipt).
  - `CanIssueInvoice` / `CanPostOfficialReceivable` / `CanExecuteRoomChange` all `false`.
- 12 domain Facts `RICC01–RICC12`, incl. 118-scenario parity against an independent exact-rational oracle over 28/29/30/31-day months and leap year, the 0.5 away-from-zero boundary, full-month and day-1 edges, replay identity, and every fail-closed path.

**Delivered (`f2665e0`, doc-closed at `d36edb5`)**
- `RoomChangeIssuedInvoiceCorrectionPostgreSql_Tests` (6 Facts `RICCPG01–RICCPG06`): mounts the real `20261029223000_Phase2G3AuditedMonthlyAdjustments` migration into a throwaway schema, so the production trigger definitions are what is exercised.
  - Two independent sessions proposing the same correction on one issued invoice serialize to exactly one row (`23505` for the loser), leaving no orphan decision journal.
  - Replaying an identical correction cannot double-post.
  - `UPDATE` / `DELETE` / `TRUNCATE … CASCADE` / hash-mismatched `INSERT` all refused with `23514`.
  - The proposer cannot review their own correction; a credit that would drop the invoice below captured receipts is refused.
  - Migration `Down()` is a clean, complete reversal with no history, and refuses with `23514` once a correction exists.
- Month-length parity matrix inside the authenticated HTTP Fact: the D01 target-owns-handover-day quote is verified against calendar arithmetic alone across 28-day (2027-02), leap 29-day (2028-02), 30-day (2027-04) and 31-day (2027-01) transfer months at day 11, including day split, per-line VND, and a re-read that returns the same stored figures.

**Evidence (pushed `d36edb5`, full acceptance harness at that exact SHA)**
- `verify-phase-2h-financial-foundations.ps1` at `d36edb5`: **all five groups PASS**, exit 0 — `=== QUALITY GATE PASS: ALL REQUESTED TEST RUNS SUCCEEDED ===`, `COMMIT=d36edb5c04f84668f0044ab5bbae3526da136cdc`
- Group 1 — Domain 2H (incl. `RICC01–RICC12`): 177/177 PASS — `~/NgocTrang-Test-Logs/15-2h-financial-domain.log` (+ `.trx`, `Assert-Phase2HTrx.ps1` verified); log line 1 stamped `PHASE2H_COMMIT=d36edb5…`
- Group 2 — PG 2H concurrency (incl. `RICCPG01–RICCPG06`): 109/109 PASS — `16-2h-postgresql.log` / `.trx`; stamped `d36edb5`
- Group 3 — PG fresh disposable migration chain: 1/1 PASS (`PGMIG01`) — `17-2h-full-migration.log` / `.trx`; stamped `d36edb5`
- Group 4 — Authenticated two-PG HTTP + month-length matrix: 1/1 PASS (2m01s) — `18-2h-auth-http.log` / `.trx`; stamped `d36edb5`
- Group 5 — Full regression: 732 Domain / 231 Application / 447 SQLite EF / 76 HTTP, 0 failed, 0 skipped — `19-2h-full-regression.log`
- Concurrency Fact repeated 5× standalone before the push: 5/5 PASS (no flakiness)
- Solution build: 0 errors
- Harness updated: 6 source gates + 2 verify bundles accept the new branch; 2H domain count 165 → 177, 2H PG count 103 → 109

**Definition of Done status**
- ✅ Apply "target owns handover day" policy
- ✅ VND rounding per line (0.5 → up for non-negative)
- ✅ Signed pricing artifact, append-only adjustments
- ✅ Receipt reconciliation (refuses to correct below captured receipts)
- ✅ SQL bypass protection — DB triggers on `BuildingManagementMonthlyInvoiceAdjustments` (append-only, no-truncate, independent reviewer, credit-vs-receipts ceiling), now proven against the shipped migration DDL
- ✅ Test against **issued invoices**, not just staging
- ✅ HTTP/PG parity for 28/29/30/31 days, leap year — per-month-length HTTP matrix + exhaustive domain parity
- ✅ Concurrency/rollback — dedicated concurrent-correction scenario, SQL-bypass refusals, and guarded migration rollback

**Blocker:** none — P3.1 merged, PR #5 integrated.  
**Shared spine:** `MonthlyInvoiceAdjustmentRecord` + DB append-only triggers (migration `20261029223000`); HTTP surface `contracts/{contractId}/monthly-issued/{issuedId}/adjustments` (+ `/review`) in `SliceBStagingFinalInvoiceController`.

### P2.2 ✅ D03 Service Pricing in Official Invoices — COMPLETE (`b18644b`, pushed)
**Status:** DONE  
**Definition of Done:**
- Pricing by `RoomId + ServiceId + effective version + unit`
- Actual occupant count from verified residency list
- E/W meter actual readings
- Service fees appear in **official issued invoice**
- Immutable when room/pricing changes (snapshot)

**Delivered (`b18644b`)**
- `OfficialServicePricingSchedule` (`modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/`): the missing production gate. `Bind(issued, candidate, sourceBindings, roomTariffHistory, verifiedResidency)` reconstructs the whole service schedule from frozen evidence and `VerifySchedule(candidate)` re-checks it.
  - Every line recomputed from the **room's own** tariff version — `PreviewCharge` must equal the frozen binding amount and the fee line amount, so caller arithmetic is never trusted.
  - Tariff selection is anchored to `candidate.ReviewedAtUtc` (private `SelectFrozenVersion`), **not** `SelectEffectiveForDate`/“latest today”. A version approved after the review instant can never rewrite an issued invoice.
  - Refuses superseded rates, missing/foreign bindings, one-binding-per-service-code violations, per-person lines whose headcount ≠ verified residency, and `VerifiedOccupantCount` 0 with a meter quantity (and vice versa).
  - `VerifyResidency` requires 1–100 unique tenants with exactly one `PrimaryTenant`; an empty or double-primary roster is refused.
  - `EvidenceSha256` computed in the private constructor (`Fingerprint(this)`), so get-only snapshots cannot drift; `VerifyAgainst` throws `InvalidDataException` on drift.
  - `IsOfficialReceivable` / `CanPostMoney` are hard `false` — pricing evidence never posts money.
- Wired into production issuance: `IssueMonthlyAsync` now calls `VerifyOfficialServicePricingAsync(db, archived, issued)` **before** `db.MonthlyIssuedInvoices.Add(issued)`. Residency and tariff history are loaded server-side from `ContractTenant` / `RoomScopedServiceTariffVersions`; nothing comes from the client. The existing `BM_2HVerifyRoomServiceInvoiceBinding` trigger stays as the last line of defence.
- 11 domain Facts `DOSVC01–DOSVC11`: per-person 150k×2, target room's own rate rather than a foreign rate, metered unit quantity, empty no-fee schedule, future-appended version ignored, superseded-before-review refused, headcount under/overstatement refused, missing/foreign binding refused, cross-candidate issuance refused, malformed residency refused, schedule refused for a candidate it was not bound to.
- Authenticated HTTP month-10 cycle in `SliceAStagingPostgreSqlHttp_Tests`: 2-occupant contract, 150,000 VND/person tariff, itemized fee candidate → independent review by `clientB` → issuance by `clientRoot`. Asserts the issued `amount` equals the frozen candidate amount, the bound line is exactly 2×150,000 with `TariffVersionId` matching, and that appending a 500,000 VND November version afterwards leaves the issued schedule unchanged.

**Evidence (pushed `b18644b`, full acceptance harness at that exact SHA)**
- `verify-phase-2h-financial-foundations.ps1` at `b18644b`: **all five groups PASS** — `=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===`, `COMMIT=b18644b9610ba2ee8b6b7357736a5328f4d4f78d`
- Group 1 — Domain 2H (incl. `DOSVC01–DOSVC11`): **188/188** PASS (`TRX VERIFIED: passed=188/188; failed=0; skipped=0`) — `~/NgocTrang-Test-Logs/15-2h-financial-domain.log` (+ `.trx`); log line 1 stamped `PHASE2H_COMMIT=b18644b…`
- Group 2 — PG 2H concurrency (incl. `TRFBINDPG01–TRFBINDPG10`): **109/109** PASS (`TRX VERIFIED: passed=109/109`) — `16-2h-postgresql.log` / `.trx`; stamped `b18644b`
- Group 3 — PG fresh disposable migration chain: 1/1 PASS (`PGMIG01`) — `17-2h-full-migration.log` / `.trx`; stamped `b18644b`
- Group 4 — Authenticated two-PG HTTP + D03 service-pricing cycle: 1/1 PASS (2m30s) — `18-2h-auth-http.log` / `.trx`; stamped `b18644b`
- Group 5 — Full regression: 743 Domain / 231 Application / 447 SQLite EF / 76 HTTP, 0 failed, 0 skipped — `19-2h-full-regression.log`
- Solution build: 0 errors
- Harness updated: 6 source gates + 2 verify bundles accept `feat/p2-2-service-pricing-official-invoice`; 2H domain count 177 → 188

**Definition of Done status**
- ✅ Pricing by `RoomId + ServiceId + effective version + unit` — `Bind` matches binding↔tariff version↔room and refuses any other combination
- ✅ Actual occupant count from verified residency list — per-person multiplier equals `ContractTenant` count; mismatch refuses issuance
- ✅ E/W meter actual readings — metered-unit path requires `VerifiedMeterQuantity`; a per-room fee cannot disguise one
- ✅ Service fees appear in **official issued invoice** — proven over authenticated HTTP, issued amount includes the service line
- ✅ Immutable when room/pricing changes (snapshot) — anchored to `candidate.ReviewedAtUtc`; later appended versions proven not to rewrite the issued schedule

**Blocker:** none — P3.1 merged (PR #5 / `c499d89`); this supersedes the "staging tests only" state.  
**Dependency:** P3.1

### P2.3 ✅ H4 Settlement (D04 Business Flow) — COMPLETE (`aa3b9bb`, pushed)
**Status:** DONE  
**Definition of Done:**
- Cash-backed top-up obligation with deadline ≤1 calendar month from move date
- Track partial payment / missed deadline
- Credit to next month rent **OR** bank refund — exactly one, never both
- Refund/debt dispute reconciliation
- Dual approval: Owner + Reviewer
- Genuine bank evidence required
- Prevent false receipts

**Already in place before this phase (audited, unchanged)**
- Obligation creation is atomic with the signed H4 completion: `BuildingManagementRoomChangeH4Obligations` is insert-only under `BM_2HGuardH4Obligation`, which re-derives kind/amount/due from the completion and refuses any drift. `CK_BM_2H_H4MoneyShape` enforces `top-up XOR (credit XOR refund)` on the completion itself, so "exactly one, never both" is a database fact, not a service-layer convention.
- Partial installments accumulate through append-only `...H4SettlementPostings`; overpayment and replay are refused by `BM_D04GuardH4SettlementPosting`.
- Dual approval: `ReviewH4BankEvidenceAsync` requires a scoped reviewer and forbids the Owner as reviewer; `PostH4SettlementAsync` requires the founding Owner plus `BootstrapOwner` and re-reads the live Identity record at commit.
- False receipts: a receipt may be claimed once; a self-reviewed evidence row is refused by `BM_D04GuardH4SettlementReview` even from a privileged SQL session.
- **Deadline ≤1 month:** `TRFH4PG03` proves a 2026-12-16 due date on a 2026-11-15 transfer is refused with `23514`.

**Delivered (`aa3b9bb`)**
- `RoomChangeH4ObligationAging` + `RoomChangeH4ObligationAgingEvaluator` (`modules/buildingmanagement/src/BuildingManagement.Domain/RoomChanges/`): read-only aging of an already committed obligation.
  - Only `DepositTopUp` may carry a due date; a `NextMonthIssuedRentCredit` or `IndependentBankRefund` carrying one is refused, and those two kinds can never report overdue.
  - Lateness starts the day **after** the due date; the outstanding remainder is what ages, so a partial installment shrinks the aging exposure; a fully settled obligation is never overdue.
  - Rejects an empty obligation id, an undefined kind, a non-positive or fractional original amount, and a settled amount outside `[0, original]`.
  - Surfaced as `isOverdue` / `daysPastDue` / `agedAsOfVietnam` on both `SliceBRoomChangeH4ObligationRead` and `SliceBRoomChangeD04BalanceRead`; the obligation read also carries `CanPostMoney => false`. Aging is presentation-only: it never creates a fee, penalty or new receivable.
- Migration `20261113100000_Phase2HH4ObligationDisputes`: H4-scoped dispute lane. The existing move-out dispute lane is keyed to an `OfficialPostingId` **and** a `MoveOutRequestId`, so it structurally cannot represent an H4 obligation.
  - `BuildingManagementRoomChangeH4ObligationDisputes` (one per obligation) and `...H4ObligationDisputeResolutions` (one per dispute), both append-only via `BM_GuardH4ObligationDispute`, which requires `transaction_isolation = serializable`, rechecks that the obligation's **source contract** matches, refuses a `OpenedAtUtc` in the future, and refuses a dispute once any settlement posting exists.
  - `BM_DenyH4DisputeMutation` blocks `TRUNCATE` on both tables; `Down()` refuses to drop either table while evidence remains.
- `SliceBStagingRoomChangeH4Disputes.cs` + 3 controller routes: `POST .../h4-obligation-disputes`, `POST .../h4-obligation-disputes/{disputeId}/resolve`, `GET .../h4-obligation-dispute-status` (`state` 0 none / 1 open / 2 accepted / 3 rejected; `isSettlementBlocked` true for 1 and 3).
  - The resolver must be a **different** authenticated person from the opener; a second dispute of the same obligation and a second resolution of the same dispute are both refused.
- `PostH4SettlementAsync` now refuses to post while a dispute is open or was rejected: a contest can never be silently settled in the building's favour.

**Evidence (pushed `aa3b9bb`, full acceptance harness at that exact SHA)**
- `verify-phase-2h-financial-foundations.ps1` at `aa3b9bb`: **all five groups PASS** — `=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===`, `COMMIT=aa3b9bb578a1012a96d13204cc94d1a7f81a6809`
- Group 1 — Domain 2H (incl. `H4AGE01–H4AGE14`, `DOSVC01–DOSVC11`): **202/202** PASS — `~/NgocTrang-Test-Logs/15-2h-financial-domain.log` (+ `.trx`); line 1 stamped `PHASE2H_COMMIT=aa3b9bb…`
- Group 2 — PG 2H concurrency (incl. `TRFH4DSPG01–TRFH4DSPG10`): **119/119** PASS — `16-2h-postgresql.log` / `.trx`; stamped `aa3b9bb`
- Group 3 — PG fresh disposable migration chain (`ExpectedLatestMigration = 20261113100000_Phase2HH4ObligationDisputes`): 1/1 PASS — `17-2h-full-migration.log` / `.trx`; stamped `aa3b9bb`
- Group 4 — Authenticated two-PG HTTP + H4 dispute cycle + overdue aging under a moved fixture clock: 1/1 PASS (2m10s) — `18-2h-auth-http.log` / `.trx`; stamped `aa3b9bb`
- Group 5 — Full regression: 757 Domain / 231 Application / 447 SQLite EF / 76 HTTP, 0 failed, 0 skipped — `19-2h-full-regression.log`
- `LOG_DIRECTORY=C:\Users\Huynq\NgocTrang-Test-Logs`; solution build 0 errors
- Harness updated: all 6 scripts with a branch allowlist accept `feat/p2-3-h4-settlement-d04` (3 `check-*-gate.ps1`, `verify-2g-security-batch.ps1`, `verify-phase-2g5-staging.ps1`, `verify-phase-2h-financial-foundations.ps1`); 2H domain count 188 → 202, PG count 109 → 119

**Definition of Done status**
- ✅ Cash-backed top-up obligation with deadline ≤1 calendar month — enforced by the signed completion cascade; over-long due date refused (`TRFH4PG03`)
- ✅ Track partial payment / missed deadline — postings accumulate the settled amount; `RoomChangeH4ObligationAging` reports `isOverdue` / `daysPastDue` on both reads, proven over HTTP with a moved clock (35 days past due)
- ✅ Credit to next month rent **OR** bank refund, exactly one — `CK_BM_2H_H4MoneyShape` (top-up XOR (credit XOR refund)) plus `BM_D04GuardH4SettlementPosting`
- ✅ Refund/debt dispute reconciliation — new H4-scoped append-only dispute lane with independent resolution and a settlement block while contested
- ✅ Dual approval: Owner + Reviewer — reviewer ≠ Owner on review, Owner + `BootstrapOwner` on posting
- ✅ Genuine bank evidence required — manual independent attestation is mandatory and `IsBankApiVerified` is permanently `false`; real bank verification remains HOLD under the standing no-real-bank-transfer constraint
- ✅ Prevent false receipts — one-claim-per-receipt, self-review refused at the trigger, append-only journal

**Blocker:** none — P3.1 merged (PR #5 / `c499d89`); real bank settlement remains HOLD.  
**Dependency:** P1.3, P2.1

### P2.4 ✅ D06/H6 Three-Signature Process — COMPLETE (`950610a`, pushed)
**Status:** DONE  
**Definition of Done:**
- 3 signatures: Owner, Staff, Tenant/Representative
- Scan physical documents (accept scanned handwritten signatures)
- Permission and handover evidence verification
- Owner exports new contract DOCX/PDF with audit trail
- Download access control with audit

**Already in place before this phase (audited, unchanged)**
- The three-signature record itself — `RoomChangeHandoverThreePartySignoff.Sign` (PR #5 lane, tested by `RoomChangeFinalIndependentSignoffs_Tests`) — enforces distinct Owner / handover-staff / recorded primary-tenant ids, roster binding, the physical-handover evidence fingerprint, `CanExecuteTransfer => false` and `IsCryptographicallyAuthenticatedSignature => false`.
- The signoff is archived append-only by `RoomChangeEvidenceArchiveRecord.CaptureThreePartyHandover` (Kind 4) and exposed over authenticated HTTP by `POST .../three-party-handover-archive` with the blank-signature and wrong-representative denials proven in the two-PG fixture.
- The Owner's signed-document export (`GET .../export.docx`) with two-building scoping, live `BootstrapOwner` revocation and a non-caching response already exists (PR #5 lane).

**Delivered (`950610a`)**
- `RoomChangeSignedHandoverScanRecord` + `RoomChangeContractDocumentAccessRecord` (`modules/buildingmanagement/src/BuildingManagement.Domain/RoomChanges/`). The scan stores **only** the SHA-256 of the uploaded sheet, its media type/byte length and an opaque 32-hex vault handle — never the bytes. It must bind to the archived `ThreePartyPhysicalHandover` of the same request (`HandoverEvidenceSha256 = archive.RecordSha256`), accept only `application/pdf` / `image/png` / `image/jpeg`, 256 B – 5 MB, be uploaded at/after the archive time by a non-empty user, and stay one-per-handover and one-per-request. `ProvesSignerIdentity => false`, `CanExecuteRoomChange => false`. The access trail records actor, scope, purpose (12–300 chars), delivered digest and delivered size; `GrantsAccess => false`.
- Migration `20261113150000_Phase2HD06SignedScanAndDownloadAudit`: `CK_BM_D06ScanShape` / `CK_BM_DocAccessShape`, unique `UX_BM_D06Scan_Handover` / `UX_BM_D06Scan_Request`, `BM_GuardD06SignedHandoverScan` (append-only + SERIALIZABLE + re-derives the archive binding), `BM_GuardContractDocumentAccess` (append-only + SERIALIZABLE/REPEATABLE READ + live non-deleted contract), TRUNCATE blockers, evidence-retaining `Down()`.
- `D06DisposableEncryptedScanArchive` (`src/.../HttpApi.Host/Security/`): AES-256-GCM vault with its **own key ring and directory** (`*.d06scan`), separate from the D04 bank-evidence vault; magic-byte type verification; store-then-readback digest proof before the row is written. Fail-closed: without `BuildingManagement:SliceAStaging:D06ScanArchive:Root` + `:KeyBase64` (validated 32-byte key, absolute path) the routes stay unavailable.
- `SliceBStagingRoomChangeD06SignedScan.cs` + 2 controller routes (`POST`/`GET `{requestId}/d06-signed-handover-scan``; 35 → 37 in the boundary inventory). The vault handle is bound to the uploader via AES-GCM associated data, so the Owner download opens it with the **original uploader identity** while the downloader is separately verified by the scoped Owner path. `GET .../export.docx` now takes an optional `purpose` and appends exactly one access-trail row per successful export inside the same transaction.

**Evidence (pushed `950610a`, full acceptance harness at that exact SHA)**
- `verify-phase-2h-financial-foundations.ps1` at `950610a`: **all five groups PASS** — `=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===`, `COMMIT=950610a00d519d4b49b97b294cf94fcd0d427904`
- Group 1 — Domain 2H (incl. `TRFD06S01–TRFD06S10`): **212/212** PASS — `~/NgocTrang-Test-Logs/15-2h-financial-domain.log` (+ `.trx`); stamped `PHASE2H_COMMIT=950610a…`
- Group 2 — PG 2H concurrency (incl. `TRFD06PG01–TRFD06PG10`): **129/129** PASS — `16-2h-postgresql.log` / `.trx`; stamped `950610a`
- Group 3 — PG fresh disposable migration chain (`ExpectedLatestMigration = 20261113150000_Phase2HD06SignedScanAndDownloadAudit`): 1/1 PASS — `17-2h-full-migration.log` / `.trx`; stamped `950610a`
- Group 4 — Authenticated two-PG HTTP + D06 upload/download cycle (wrong media type 403, forged PDF magic bytes 409, duplicate scan 403, staff download 403, Owner download byte-exact + no-store + nosniff, stored row = digest + opaque handle, encrypted file on disk): 1/1 PASS — `18-2h-auth-http.log` / `.trx`; stamped `950610a`
- Group 5 — Full regression: 767 Domain / 231 Application / 447 SQLite EF / 76 HTTP, 0 failed, 0 skipped — `19-2h-full-regression.log`
- `LOG_DIRECTORY=C:\Users\Huynq\NgocTrang-Test-Logs`; solution build 0 errors
- Harness updated: all 6 scripts with a branch allowlist accept `feat/p2-4-d06-three-signature`; 2H domain count 202 → 212, PG count 119 → 129

**Definition of Done status**
- ✅ 3 signatures: Owner, Staff, Tenant/Representative — pre-existing `RoomChangeHandoverThreePartySignoff` + `CaptureThreePartyHandover`, unchanged, re-verified
- ✅ Scan physical documents (accept scanned handwritten signatures) — the 2026-11-04 Owner decision (scan accepted, cryptographic e-signing explicitly NOT required); scanned sheet archived in the encrypted D06 vault with digest binding
- ✅ Permission and handover evidence verification — scoped `Contracts.Manage` upload, vault bound to the archived three-party handover, download requires the scoped Owner bootstrap
- ✅ Owner exports new contract DOCX with audit trail — every successful `export.docx` appends one immutable `ContractDocumentAccess` row (actor, scope, purpose, delivered digest)
- ✅ Download access control with audit — staff download 403; Owner download byte-exact, `no-store`/`nosniff`; access trail append-only with shape CHECK + trigger guards
- PDF export remains DOCX-only by existing design (the DOCX is the Owner's working document; a PDF conversion lane is a separate future decision, not part of the D06 evidence chain)

**Blocker:** none — P3.1 merged; the E-sign-vs-scan decision was closed by the Owner on 2026-11-04 (scan accepted, e-sign not required).  
**Dependency:** P3.1

### P2.5 ✅ Phase 2G.5 Ordinary Move-Out End-to-End — COMPLETE (`2d01952`, pushed)
**Status:** DONE  
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

**Already in place before this phase (audited, unchanged)**
- The whole ordinary move-out lane already exists and was already proven in `STAGEPGHTTP01`: `CompleteMoveOutAsync` single SERIALIZABLE UoW (BootstrapOwner) with contract/request locks, the 5-task checklist gate (only `FinalMeterReading` must be pre-completed), final-invoice `VerifyStoredTerms`, official-receivables zero-balance, exact-match deposit decision, refund destination + independent bank review, refund/forfeit `DepositSettlement` records, `ContractDepositCloseoutApprovalRecord`, `MoveOutRoomDispositionRecord`, `Contract.End(Normal|EarlyTermination)` and `Room.ChangeStatus` — plus the `check-move-out-lifecycle-gate.ps1` source gate that blocks any direct `Contract.End` call outside the audited closeout.
- Domain tests `MoveOutRoomDisposition_Tests` (CMOUT51–63) already covered the Reserved/PendingPayment/Signed disposition matrix at the domain level.

**Delivered (`2d01952`) — closing the two genuine HTTP evidence gaps**
- **Final-meter signoff lane (E/W meter final reading):** the finance fixture no longer pre-completes the `FinalMeterReading` checklist task. The E2E now drives the real authenticated `POST {contractId}/move-out-requests/{requestId}/verify-final-meters` route (scoped building B client) with a physically continuous reading history — August zero-usage anchors + September final readings on both E and W meters (without them the coverage assessor correctly fail-closes with `MissingPreviousMonthReading`). Asserts: 200 with `type == FinalMeterReading`, `isCompleted == true`, `completedAt != null`; replay is idempotent (200, still completed); a foreign contract's route stays 403.
- **Booking-retention lane (future reservation handling, OI-06):** a second full scenario on new room B112 with a `Reserved` future occupancy: the authenticated closeout asserts `roomStatus == Reserved`, `hasFutureCommitment == true`, the stored `MoveOutRoomDispositionRecord` `ResultRoomStatus == Reserved` with `FutureReservationId` bound, the contract `Ended`, the retained reservation still `Reserved`, and the scoped `GET room-disposition` reporting the same. The scenario runs the complete genuine financial lane — prepare → independent review → root issue-candidate → manual receipt → post-receivable → journal review → deposit decision → refund destination → bank review — so the closeout gate is satisfied by real evidence, never bypassed.
- The second scenario reuses the finance lane's `UtilityRate` rows (same building/type/effective date) instead of seeding duplicates that violate the unique rate index.
- All six gate/verify scripts allowlist `feat/p2-5-move-out-e2e`.

**Evidence (pushed `2d01952`, full acceptance harness at that exact SHA)**
- `verify-phase-2h-financial-foundations.ps1` at `2d01952`: **all five groups PASS** — `=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===`, `COMMIT=2d01952dbea3e81fc5641665831e59e0b7df5921`
- Group 1 — Domain 2H: **212/212** PASS — `~/NgocTrang-Test-Logs/15-2h-financial-domain.log` (+ `.trx`, `passed="212" failed="0"`); stamped `PHASE2H_COMMIT=2d01952…`
- Group 2 — PG 2H concurrency: **129/129** PASS — `16-2h-postgresql.log` / `.trx` (`passed="129" failed="0"`); stamped `2d01952`
- Group 3 — PG fresh disposable migration chain: **1/1** PASS — `17-2h-full-migration.log` / `.trx` (`passed="1" failed="0"`); stamped `2d01952`
- Group 4 — Authenticated two-PG HTTP (incl. the new final-meter signoff + booking-retention lanes): **1/1** PASS — `18-2h-auth-http.log` / `.trx` (`passed="1" failed="0"`); stamped `2d01952`
- Group 5 — Full regression: 767 Domain / 231 Application / 447 SQLite EF / 76 HTTP, 0 failed, 0 skipped — `19-2h-full-regression.log` (`QUALITY GATE PASS`)
- `LOG_DIRECTORY=C:\Users\Huynq\NgocTrang-Test-Logs`; solution build 0 errors

**Definition of Done status**
- ✅ Final bill generation — authenticated prepare → review → issue-candidate → receipt → posting lane in both scenarios
- ✅ Debt settlement — official-receivables zero-balance gate at closeout + journal review
- ✅ Deposit offset (OI-04 rules) — exact-match `ContractDepositSettlementDecisionRecord.RequireForCloseout` + refund/forfeit settlement records + approval record
- ✅ E/W meter final reading — authenticated `/verify-final-meters` with continuous reading history; idempotent; foreign contract 403
- ✅ Checklist completion — the 5-task gate; 4 tasks evidence-verified by the closeout, `FinalMeterReading` completed by the real signoff route
- ✅ Room disposition — `MoveOutRoomDispositionRecord` over HTTP; room status asserted in DB and via scoped GET
- ✅ Future reservation handling — Reserved retention + `hasFutureCommitment` proven over HTTP with the retained booking re-verified
- ✅ **Never** allow direct `Contract.End` bypass — `check-move-out-lifecycle-gate.ps1` source gate (in the full-regression group) + closeout is the only writer
- ✅ Cross-check OI-01 → OI-06 — each policy anchor covered by the same E2E lanes (issuance terms, meter evidence, deposit decision, destination/bank review, disposition retention)

**Blocker:** none — all dependencies DONE (P2.1 `d36edb5`, P2.2 `b18644b`, P2.3 `aa3b9bb`, P2.4 `950610a`)  
**Dependency:** P2.1, P2.2, P2.3, P2.4

### P2.6 ✅ UI/Frontend Inventory and Integration — COMPLETE (`6c9852f`, pushed)
**Status:** DONE (discovery audit; no integration to build against — see verdict)  
**Definition of Done:**
- Inventory existing frontend projects in repo
- Identify tech stack (React/Angular/Vue/Blazor?)
- Map coverage: Tenant/Staff/Owner portals
- Identify missing flows: transfer, invoice, meter, contract, payment, proof upload
- Do NOT change framework or rewrite without audit

**Verdict:** **there is no frontend in the repository — by design.** The solution was generated with `uiFramework: "no-ui"` (ABP Studio `.abpstudio/state.json`, verbatim). The only UI is the ABP template's Razor landing page (login/logout + language switch + OpenIddict app cards) rendered by the headless API host; `wwwroot` scripts/styles are empty template placeholders; `package.json` holds only the MVC Basic-theme packages. There is no SPA/Blazor/Angular/Vue/React project anywhere to inventory, so there is also nothing to migrate or rewrite.

**Delivered (`6c9852f`) — the audit document `etc/phase-audits/P26_FrontendInventory_20261113.md`**
- Full inventory of everything UI-shaped (landing page, theme packages, wwwroot, resource mapping, the 2 seeded OpenIddict clients — the `_App` client id has no RootUrl configured, i.e. no real SPA registered)
- API-surface map a frontend would consume: 22 conventional AppServices under `/api/building-management/...` + 137 HTTP-verb attributes across 10 authenticated slice/payOS controllers
- Portal coverage matrix: Tenant / Staff / Owner — **all NONE**; Swagger UI is the current de-facto frontend
- The seven missing flows (transfer, invoice, meter, contract, payment, proof upload, move-out E2E) — all exist as authenticated, E2E-tested backend routes (P2.1–P2.5) with zero UI
- Security constraints any future portal must inherit (no direct `Contract.End`, append-only evidence, two-building scoping, owner-route live revocation, payOS money HOLD, Vietnamese default language)
- Recommended (not executed) next step: an Owner decision on the greenfield portal stack; per the P2.6 rule this phase changed no framework and started no rewrite

**Evidence (pushed `6c9852f`)**
- Read-only inspection of the working tree at `43c70dd`/`6c9852f`; no files modified beyond the audit document, no application started, no database touched — docs-only phase, no test TRX applicable
- Branch pushed ff-only: `origin/feat/p2-6-frontend-discovery` = `6c9852f`

**Definition of Done status**
- ✅ Inventory existing frontend projects in repo — complete: none exist (by design, with the authoritative template record cited)
- ✅ Identify tech stack — server-rendered Razor (ABP Basic theme) on the API host only; no SPA framework present
- ✅ Map coverage: Tenant/Staff/Owner portals — all NONE; Swagger is the de-facto frontend
- ✅ Identify missing flows — all seven mapped to the exact controllers/routes that already implement them
- ✅ Do NOT change framework or rewrite without audit — production code untouched (docs-only commit)

**Blocker:** none — the follow-up (building a portal) is a new phase requiring an Owner stack decision  
**Dependency:** none

---

## P3 — Integration, Full Regression, Review/Merge

### P3.1 ✅ Branch Reconciliation and Merge Choreography
**Status:** DONE (Owner-approved merge executed)
**Evidence:** Analysis report `etc/phase-audits/P31_BranchReconciliation_20261111.md`; merge commit `01cba31` (pushed, PR #10 trunk now contains PR #5 + absorbed 2g2). Only two merge parties existed; merge-tree clean; disjoint migrations/snapshots/docs. Findings: only TWO independent merge parties (PR #5 `integration/spec-kit-h4-security-20261105` @ `b3122b14`, 95 commits; PR #10 @ `c735639`, 371 commits — both from merge-base `e05049d`); `origin/wip/phase-2g2` fully absorbed by PR #5; legacy d04b + phase2h lanes fully absorbed by PR #10; `git merge-tree` clean in ALL directions (0 conflicted files); zero overlap in migrations (disjoint table/index/trigger names, PR #5 never touches the PayOs context), zero ModelSnapshot/Designer changes on either lane, zero docs overlap. Proposed order: merge PR #5 INTO PR #10 (trunk), then P3.2 full merged-SHA regression before any merge-to-main proposal. R3 (semantic: H4 credit-cap triggers vs D04 journal over the shared Payment spine) is the only substantive risk and is P3.2's job to prove.
**Definition of Done:**
- ✅ Compare `wip/phase-2g2`, `integration/spec-kit-h4-security-20261105` (PR #5), and `feat/local-safe-startup-20261109` (PR #10)
- ✅ Identify merge-base: `e05049d037a4299ca17c04d2dd3a3c25f9637bfd`
- ✅ Map overlapping/duplicate migrations (none; disjoint table/index/trigger names; natural timestamp ordering on fresh DB)
- ✅ Map entity model snapshot conflicts (none; both lanes' new migrations are raw-SQL, no Designer/snapshot touched)
- ✅ Map overlapping test documentation (none; `specs/` + `.specify/` vs top-level `CURRENT_STATE.md`/`TASK_BACKLOG.md`)
- ✅ Design merge order with risk matrix (see report §5-§6: PR #5 into PR #10, then P3.2)
- ⏸️ Keep PRs Draft/non-merged until Owner approves (in force)
- ✅ **Do NOT** cherry-pick writer code without security migrations (rule honored — full-branch merges only)

**Blocker:** Owner approval of merge execution (choreography proposal in report §6)
**Dependency:** P0 and P1 clean ✅ (all P0 6/6, P1 5/5 with pushed-SHA evidence)

### P3.2 ✅ DONE (Full Regression Test Suite — local evidence complete at merged tree; CI evidence GREEN as of 2026-11-12)
**Status:** DONE at the P3.1-merged tree, HEAD `1ddf2e6` (pushed; branch in sync). Owner choice (a) executed: guard-adapt the 2H/2g/2g5 verify lanes and run them on the merged trunk locally. ~~Sole open external item: GitHub Actions billing lock~~ **RESOLVED 2026-11-12:** Owner unlocked billing (card-free); CI re-armed; runs 382/383 surfaced two workflow/fixture gaps fixed at `80e3c00` + `c63d370` (locally verified first); **run 384 at `c63d370` green 5/5** — CI evidence column of P3.3 retro-filled PASS
**Evidence (all at pushed SHAs, local disposable 15432, 15433/5432 untouched):**
- Build whole solution (both lanes): 0 errors at merge commit `01cba31` (30 pre-existing style warnings)
- Module suites at `01cba31`: Domain **720/720**, EF (SQLite) **447/447**, Application **231/231**, Host HTTP **76/76** — TRXs in `C:\Users\Huynq\NgocTrang-Test-Logs\p32-merged-01cba31`
- P1.5 internal staging harness: 20/20 at `01cba31`, LOG_DIRECTORY `payos-p15-internal-20261111-230926`
- D04-B native JWT matrix: PASS at `38f0037`, LOG_DIRECTORY `payos-native-jwt-20261111-231445`
- D04 writer-only bundle: PASS at `7f48c59`, LOG_DIRECTORY `payos-20261111-232050`
- D04 FULL bundle (steps 01–27): PASS at `7f48c59`, LOG_DIRECTORY `payos-20261111-232223`
- 2H financial foundations: **5/5 groups PASS** at `1ddf2e6` — domain **165/165** (`15-2h-financial-domain.trx`), PG **103/103** (`16-2h-postgresql.trx`), full migration chain (`17-2h-full-migration.trx`), auth HTTP **1/1** (`18-2h-auth-http.trx`, STAGEPGHTTP01), group 5 = full 2g regression batch **76/76** (`19-2h-full-regression.log`); all TRXs stamp `PHASE2H_COMMIT=1ddf2e6...`; LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs`
- 2g5 staging: **4/4 groups PASS** at `1ddf2e6` — PG archive **58/58** (`11-2g5-archive-pg.log`), full migration chain **1/1** (`12-2g5-full-migration.log`), auth HTTP **1/1** (`13-2g5-http-pg.log`), full regression **76/76** (`14-2g5-full-regression.log`); script banner: ALL FOUR GROUPS PASSED
- 2g security batch: run inside both 2H group 5 and 2g5 group 4 (both 76/76, incl. 3 source gates + compile-only PG lane)
- **Four real merged-tree fixes:** `38f0037` (D04 fixtures asserted the pre-merge terminal EF migration; the P3.1 merge legitimately adds `20261105150000_Phase2HD04H4SettlementJournal` — R3 predicted class), `7f48c59` (D04 bundle pinned host-archive count 7; P1.3 had grown the vault suite to 9), `127f5d4` (guard-adapt: 2-branch allowlists in 6 verify scripts + C# port guards widened to 5432 (CI) or 15432 (local disposable); DB name `ngoctrang_integration_test` and loopback-host checks unchanged), `1ddf2e6` (four STAGEPGHTTP fixtures hard-coded due dates 2026-11-07/12-07 that the wall clock (2026-11-12) had expired — domain `MonthlyInvoiceCandidateLedgerRecord.Validate` requires DueDate ≥ the VN-local review day, so the freeze legitimately rejected them; time-bomb fixture, NOT a merge defect; replaced with the file's established VN-local today+7 pattern)
**Definition of Done:**
- ✅ Per-SHA backend: Domain, Application, EF, HTTP tests (1474/1474 green at the merge commit)
- ✅ `verify-2g-security-batch.ps1 -Mode Full` — ran green at `1ddf2e6` inside both the 2H lane (group 5) and the 2g5 lane (group 4), 76/76 both times, after Owner-approved guard-adapt `127f5d4` (branch allowlist now accepts the merged trunk)
- ✅ PostgreSQL scratch: full migrations → rollback → reapply (covered per-lane: D04 FULL bundle 01–27 incl. migration chain; 2H/2g5 full migration chain groups; P1.5 harness chain at `01cba31`; PayOs EF context up/down/reapply CI lane remains billing-blocked)
- ✅ `verify-phase-2h-financial-foundations.ps1` — 5/5 groups PASS at `1ddf2e6` on disposable PG 15432 (guard-adapt `127f5d4`; port 5432 stays allowed for CI only)
- ✅ `verify-phase-2g5-staging.ps1` — 4/4 groups PASS at `1ddf2e6` on disposable PG 15432
- ✅ D04 isolated bundle (writer-only + FULL, both green at `7f48c59`)
- ✅ Run on correct branches per test scope (all harnesses ran on the merged trunk at pushed SHAs)
- ✅ Read all script guards before running (guards read; the guard-adapt commit `127f5d4` was the Owner-approved path, executed and re-verified)

**Blocker:** None. ~~Remaining HOLD: GitHub Actions billing lock~~ **RESOLVED 2026-11-12:** billing unlocked (card-free); CI runs 382→383→384 at `bd2a11e`→`80e3c00`→`c63d370`: two workflow/fixture gaps fixed with locally-verified commits (probe env + full EF chain + extended acceptance SQL; port guard-adapt), **run 384 green 5/5 (ID 43240562191)**. P3.3 CI-evidence column retro-filled PASS.
**Dependency:** P3.1 ✅ (merged at `01cba31`, Owner-approved)

### P3.3 ✅ Acceptance Matrix
**Status:** DONE — matrix at `etc/phase-audits/P33_AcceptanceMatrix_20261112.md`  
**Evidence:** 26 requirement rows across 5 sections (A safety, B auth, C accounting, D merge-integrity, E historical CI-only): every local-verifiable invariant PASS at pushed SHA `1ddf2e6` with per-row SHA+TRX/LOG citations; R3 closed with the four documented fix classes; 4 Owner HOLD groups (GitHub billing, production vault/roles/secrets, TLS public callback, merge-to-main). No previous-run PASS copied onto a new SHA.  
**Definition of Done:**
- ✅ Create table: Requirement ID | Source File/Commit | Test Name | Environment | Evidence Log/URL | PASS/PARTIAL/HOLD | Blocker | Owner Action Needed (sections A–E + overall disposition)
- ✅ Review threat model (section A: prod-DB/real-money/secret-leakage/fail-closed/billing boundary)
- ✅ Review auth invariants (section B: bearer authenticity, negative matrix, DB least privilege, deny-all membership, scoped reads)
- ✅ Review accounting invariants (section C: exactly-once, idempotency, credit caps, conservation, append-only journal, calendar parity, provider gate)

**Blocker:** None for P3.4 preparation; the matrix's HOLD items (GitHub billing, production vault/roles, TLS public callback, merge-to-main authorization) are all Owner actions  
**Dependency:** P3.2 ✅ (local evidence complete at `1ddf2e6`)

### P3.4 ✅ DONE (Final Review, Release Proposal — MERGE EXECUTED 2026-11-12)
**Status:** DONE — proposal `etc/phase-audits/P34_FinalReview_ReleaseProposal_20261112.md` signed and EXECUTED. Owner approved both remaining decisions in one instruction ("Làm cả 2 đi", 2026-11-12): §3.2 P2 deferral ACCEPTED (main = tested-staging-scope) + §6 merge SIGNED. PR #10 base re-targeted to `main` (was chain-segment `feat/d04b-operational-gates-20261108`), draft→ready, **merged as merge commit `c499d89`** (no squash, no force). Post-merge verification: local full regression at main exit 0 (QUALITY GATE PASS: 720/720 + 231/231 + 447/447 + 76/76 + 3 source gates; guard-adapt for main at `2149210`, same class as `127f5d4`) and **CI on main green 5/5 at the merge commit** (workflow_dispatch run 43247648066). **Program baseline = `c499d89`.**
**Evidence:**
- ✅ Exact-final-SHA regression all green: `verify-2g-security-batch.ps1 -Mode Full` at `feb8938` — 3 source gates, 720/720 + 231/231 + 447/447 + 76/76, exit 0; `feb8938` verified docs-only over `1ddf2e6` (where 2H 5/5 + 2g5 4/4 + payOS harness chain ran)
- ✅ Threat model review: P3.3 matrix section A (SEC-01…06)
- ✅ Auth + accounting invariants review: P3.3 sections B/C
- ✅ Proposal document: preconditions (§3), non-authorization list (§4), post-merge plan (§5), Owner signature block (§6)  
**Definition of Done:**
- ✅ Exact-final-SHA regression all green (at `feb8938`, recorded in proposal §2.1)
- ✅ Business acceptance from Owner — **§6 SIGNED 2026-11-12** ("Làm cả 2 đi": §6 merge + §3.2 P2 deferral both approved)
- ✅ Threat model review complete (P3.3 §A)
- ✅ Propose merge/release ONLY after all HOLD items resolved or explicitly deferred — proposal §3/§4 makes every deferral explicit; **merge EXECUTED** per §5

**Blocker:** None — merge executed; post-merge regression + CI green at `c499d89`  
**Dependency:** P3.3 ✅

---

## P4 — Greenfield Portal (React 19, độc lập backend)

### P4.1 ✅ Auth Portal Slice (login + 3-step reset) — COMPLETE (`dc60a7d`, pushed)
**Status:** DONE — React 19 + Vite 6 + TypeScript + Tailwind v4 portal at `frontend/`, structurally independent from the backend (dev: Vite proxy → `https://localhost:44311`; deploy: `VITE_API_BASE`). Auth only per approved plan: OpenIddict ROPC password grant on the seeded public client `NgocTrangHouseManagementSystem_App`, ABP 3-step password reset against `/api/account/*`, tokens in memory + sessionStorage (never localStorage, never logged), `RequireAuth` gate, navy app shell with honest fail-closed states (deny-all building grants shown as "chưa được cấp quyền"; dev has no SMTP → reset flow states it).
**Evidence:**
- ✅ Commit `dc60a7d` pushed to `origin/main` (fast-forward `abb3097..dc60a7d`, no force)
- ✅ Vitest 20/20 (3 files: http envelope/401/token tests, tokenStore, authClient) re-run at the pushed SHA, exit 0
- ✅ `tsc --noEmit` + `vite build` PASS re-run at the pushed SHA, exit 0
- ✅ E2E harness `etc/scripts/verify-p41-frontend-auth-e2e.ps1` (committed in `dc60a7d`): real HttpApi.Host in Staging at `https://localhost:44311` + real Vite dev server on 5173 + two throwaway PG16 databases (`bm_p41_identity_test`, `bm_p41_legacy_test`) recreated per run on the existing `bm-p21-pg-15432` container (127.0.0.1:15432; 15433 and 5432 untouched) — matrix 4/4 PASS at `dc60a7d`: direct password grant 200 (access token), same grant through the Vite proxy 200, `/api/abp/application-configuration` through the proxy with Bearer → currentUser, wrong password through the proxy → 400 `invalid_grant`
- ✅ LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p41-frontend-auth-e2e-20261113-204601` (pushed-SHA confirmation; pre-push bundle `...-20261113-204154` recorded the local run at `abb3097`)
- ✅ Admin password / tokens generated in-process, never echoed, never persisted (SEC-03 discipline)
**Definition of Done:**
- ✅ React 19 scaffold at `frontend/` independent from backend structure
- ✅ Login + forgot-password 3 steps wired to real backend endpoints
- ✅ Unit tests + build + real e2e through the proxy at the pushed SHA
- ✅ No backend C# touched; UI honestly reflects fail-closed backend states

**Blocker:** None for this slice
**Dependency:** P2.6 ✅ (frontend discovery)

### P4.1b ✅ Forgot-Password FE_1/FE_2/FE_3 Restyle — COMPLETE (`46f4dee`, pushed)
**Status:** DONE — three reset screens restyled to the Owner-provided mockups (`Docs/front-end/authentication & authorization/forgot_password_FE_1..3`): horizontal 3-step connector stepper (`ResetStepper`), step-specific left panels (solid teal security artwork / blurred building photo ×2), FE_1 identifier form with clear button + SSL/hotline footer, FE_2 masked-email chip + badge/key inputs + shield caution callout, FE_3 verified banner + 3-segment strength bar + 4-rule checklist + confirm-match badge. Honest deviations kept visible: ABP userId+resetToken link (not 6-digit SMS OTP), no SMTP in dev. Old `Stepper`/`Field`/`Button` auth usage replaced; components remain for future screens.
**Evidence:**
- ✅ Commit `46f4dee` pushed to `origin/main` (fast-forward `576072b..46f4dee`, no force)
- ✅ Vitest 20/20 (3 files) re-run at the pushed SHA, exit 0
- ✅ `tsc --noEmit` + `vite build` PASS re-run at the pushed SHA (`dist/assets/index-D5Cd1SPu.js` 343.66 kB, exit 0)
- ✅ No backend C# touched; untracked strays (`package-lock.json`, `etc/test-results/`, `etc/phase-audits/…Generated_Review.sql`, `etc/scripts/verify-p41-frontend-auth-e2e.ps1`) left uncommitted

**Blocker:** None
**Dependency:** P4.1 ✅

### P4.2 ✅ Buildings & Rooms Screens (list / detail / floor-plan / rooms) — COMPLETE (`aea363c`, pushed)
**Status:** DONE — four screens per the Owner-provided mockups (`Docs/front-end/building_rooms`): building card grid, building detail with gradient placeholder card + KPI `StatTile` row + floor table with miniature floor-plan thumbnails, floor-plan view (schematic top/bottom blocks around a shared-corridor strip, status-coloured, area-proportional) with room side panel, and the rooms listing with status filter chips + text search + summary tiles joining room→floor→building client-side. New UI primitives (`PageHeader`, `StatTile`, `StatusBadge`, `EmptyState`), API layer (`lib/api/types.ts`, `buildingApi.ts`, `buildingQueries.ts` react-query hooks + `useBuildingSnapshot` aggregation). The http client recognizes the building-scope gate's real denial shape — a 302 to `/Error?httpStatusCode=403` that `fetch()` follows silently — plus a non-JSON 200, and maps both to `ApiError` 403 so `GateNotice` shows the honest access-denied notice (with traceId) instead of an empty list. All mutating buttons disabled with explanatory tooltips (gate deny-all today); no price/contract/equipment data invented (no public API yet). Nav only lists routes that exist.
**Evidence:**
- ✅ Commit `aea363c` pushed to `origin/main` (fast-forward `613ffed..aea363c`, no force)
- ✅ `tsc --noEmit` + `vite build` PASS re-run at the pushed SHA (`dist/assets/index-pXt_bXz9.js` 384.49 kB, exit 0)
- ✅ Vitest 28/28 (4 files: http envelope/401/403-gate/redirect-trail tests, tokenStore, authClient, buildingSnapshot aggregation) re-run at the pushed SHA, exit 0
- ✅ E2E harness `etc/scripts/verify-p42-building-reads.ps1` (committed in `aea363c`): second Staging Host on 44321 + Vite on 5273 (the Owner's dev instance on 44311/5173 untouched), Host built into an isolated `host-bin` output so the dev instance's locked `bin\Release` DLLs are never written to, two throwaway PG16 databases (`bm_p42_identity_test`, `bm_p42_legacy_test`) recreated per run on `bm-p21-pg-15432` (15433 and 5432 untouched) — ALL STEPS PASSED at the pushed SHA: grant through proxy 200, and all three `GET /api/building-management/{building,floor,room}` answer the scope-gate denial (raw 302 → `/Error?httpStatusCode=403`; browser-followed 404), which is exactly the state the screens render
- ✅ LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p42-building-reads-20261113-230615` (pushed-SHA run; earlier iterations `...-223419`/`...-224129` recorded the diagnosis of the 302→`/Error` shape)
- ✅ Admin password / token generated in-process, never echoed, never persisted (SEC-03 discipline)
**Definition of Done:**
- ✅ Four screens wired to the real read endpoints through the Vite proxy
- ✅ Unit tests + build + e2e probe of all three endpoints at the pushed SHA
- ✅ Gate denial rendered honestly (no empty-list masquerade), mutations disabled
- ✅ No backend C# touched; untracked strays left uncommitted

**Blocker:** None (data appears when the operator-grant store is approved server-side)
**Dependency:** P4.1 ✅ (shell, auth, http client)

---

### P4.3 ✅ Slice-A Staging Grant Gate (open deny-all with real data) — COMPLETE (`b769028`, pushed)
**Status:** DONE — opened the building-scope grant gate on an isolated staging lane **with zero backend code change**: the Host's own Slice-A opt-in (`NgocTrangHouseManagementSystemHttpApiHostModule`, Staging + `I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES` + two `bm_slicea_staging_*` databases + exactly one indexed `RootUserIds:0`) swaps `DenyAllBuildingOperatorGrantReader` for `SliceAStagingGrantReader`, `SliceAStagingFirstOwnerWriter` (POST building = atomic Building + first operator grant to the root), and `SliceAStagingWriteBoundary`. Harness `etc/scripts/verify-p43-slicea-staging-gate.ps1` proves the full path on a disposable lane (Host 44321 + Vite 5273; the Owner's dev instance 44311/5173 untouched): identity DB migrated by DbMigrator, **building DB migrated by `dotnet ef database update` on `BuildingManagementDbContext`** (DbMigrator only migrates the Identity context — key discovery), seeded admin id read back (GuidGenerator), 9 user-level BM permissions INSERTed into `AbpPermissionGrants` (`ProviderName='U'`, `gen_random_uuid()::uuid` for the default-less `Id` column), root bootstraps a building, creates floor + room, then **all three `GET /{building,floor,room}` return 200 with the real created rows** through the same Vite proxy the browser uses — and a random building id stays denied (302→403), proving the scope gate is open but still tight. Deliverable (b): self-service guide `docs/dev-staging-grant-lane.md` so the Owner can enable the same lane on their own 44311 instance (Claude does not touch that instance).
**Evidence:**
- ✅ Commit `b769028` pushed to `origin/main` (fast-forward `0cd518c..b769028`, no force)
- ✅ E2E harness `verify-p43-slicea-staging-gate.ps1` — **ALL STEPS PASSED at the pushed SHA** (`b769028dda32876592c9ce9d4abe21bd8dd3da1b`): two disposable `bm_slicea_staging_*` databases on `bm-p21-pg-15432`, token via password grant through the proxy, POST building/floor/room 200/201, GET all three 200 with the created rows, negative control denied
- ✅ LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p43-slicea-staging-gate-20261113-235605` (pushed-SHA run; earlier iterations `...-233646`/`...-234220`/`...-234930`/`...-235156` record the RootUserIds binding, BM-migration and PS 5.1 array-unwrap fixes)
- ✅ Admin password / token generated in-process, never echoed, never persisted; 15433, 5432, production DB, payOS untouched
**Definition of Done:**
- ✅ Gate opens through the server's own reviewed opt-in (no backend edit)
- ✅ Real data returned and verified at the pushed SHA (200 + created rows on all three read endpoints)
- ✅ Self-service guide for the Owner's 44311 dev instance committed
- ✅ Untracked strays left uncommitted

**Blocker:** None
**Dependency:** P4.2 ✅ (screens + denial rendering)

---

### P4.4 ✅ Buildings / Floors / Rooms Create-Edit-Delete Screens — COMPLETE (`5427d4a`, pushed)
**Status:** DONE — the six buttons that P4.2 deliberately left hard-`disabled` are now live and drive the nine existing REST endpoints. New modal-based UI primitives (`Dialog`, `ConfirmDialog`, `SelectField`, `TextareaField`), a pure room-number rule module shared by the form and its unit tests, and three form dialogs (Building / Floor / Room). "Thêm tòa nhà" and "Chỉnh sửa tòa nhà" reopen on the buildings screens; floors get Sửa/Xoá per row plus a building-level Xoá; the Rooms page gains "Thêm phòng" with a building→floor cascader plus Sửa/Xoá per card; the floor-plan page gains "Thêm phòng" for the floor in view ("Sửa sơ đồ" stays disabled per `Docs/front-end/DESIGN_REVIEW.md` HOLD). Buttons gate on `useAuth().hasPermission()` with an explicit tooltip when the account lacks the policy, so the deny-all dev instance explains itself instead of failing at 403. Delete flows use `ConfirmDialog`, and a room whose history makes the server flip it to `Ngừng dùng (5)` instead of removing it is reported as such after a refetch rather than silently claimed as deleted.
**Backend change (one line, Owner-authorized during this slice):** `BuildingAppService.DeleteAsync` flushed the soft delete with `autoSave: true` **before** the write fence revalidated membership; that revalidation reads live `Buildings` rows, so the just-flushed row removed the grant that authorized the delete — the guard denied, the transaction rolled back, and the building survived while `ApiResponseFilter`'s DELETE branch rewrote the resulting challenge into `200 {success:true}`. Leaving the change tracked (`autoSave: false`) lets the fence's own `SaveChangesAsync` persist it after revalidation passes. Floor and Room deletes were unaffected because their parent building still exists.
**Evidence:**
- ✅ Commits `68a1c7d` (feature), `cfa7d77`/`a0d43a6`/`5877687`/`5427d4a` (harness), `7b80ce3` (backend fix) — all pushed to `origin/main`, fast-forward only
- ✅ E2E harness `etc/scripts/verify-p44-crud-screens.ps1` — **ALL STEPS PASSED at the pushed SHA** (`5427d4a4f1ba4a9de1caa3c931d596261ae2c457`): POST building/floor/room 201, PUT building + PUT room 200 with GET-verified persistence, all four business guards returning the exact expected status + code (400 `RoomNumberDoesNotMatchFloor`, 409 `RoomNumberAlreadyExists`, 409 `FloorHasRooms`, 409 `BuildingHasFloors`), negative control still denied, and DELETE room/floor/building 200 each
- ✅ **DB-level truth asserted, not just the HTTP status:** after each DELETE the harness reads the row straight from PostgreSQL — `BuildingManagementRooms.IsDeleted=t`, `BuildingManagementFloors.IsDeleted=t`, `Buildings.IsDeleted=t`. This is what exposed the defect above: the pre-fix run logged `Buildings.IsDeleted=f` behind a 200
- ✅ LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p44-crud-screens-20261114-214439` (pushed-SHA run; earlier iterations `...-210718`/`...-211221`/`...-213217`/`...-213728`/`...-214128` record the PS 5.1 exception unwrap, the `MethodInvocationException` catch, the real table names, and the pre-fix `IsDeleted=f` diagnosis)
- ✅ Frontend re-run at the pushed SHA: `tsc --noEmit` exit 0, Vitest **47/47** (6 files, incl. 8 `roomNumberRules` + 11 `mutationErrors`), `vite build` exit 0 (126 modules, `index-DbPL9XvH.js` 413.72 kB)
- ✅ Disposable lane only: Host 44321 + Vite 5273, two `bm_slicea_staging_*` DBs on `bm-p21-pg-15432`, both dropped in `finally`; 44311/5173, 15433, 5432, production DB, payOS untouched; admin password generated in-process, never echoed
**Known limitations (unchanged, documented in `summary.json`):** a soft-deleted floor/room number still occupies the unfiltered unique index, so re-creating the same number fails rather than returning 409 — the form surfaces the generic reuse message; and a room with contract/meter history is flipped to `Inactive`, never removed.
**Definition of Done:**
- ✅ All three resources create / edit / delete from the screens (no backend API added)
- ✅ Every server guard surfaced with a Vietnamese message, verified end-to-end
- ✅ Delete proven at the DB level, not just by HTTP status
- ✅ Untracked strays left uncommitted

**Blocker:** None
**Dependency:** P4.3 ✅ (slice-A staging grant gate — writes only run on the staging lane)

---

### P4.5 ⏳ Five Figma Mockups — PHASE 1 DONE (`3adf6f6`), PHASE 2 DONE (`a9f424a`), PHASE 3 DONE (`3c3e2ea`), PHASE 4 DONE (`0620fd4`), PHASE 5 DONE (`5adbf42`), DESIGNER REVISIONS DONE (`75d0488` + harness `944de2f`)
**Status:** ALL FIVE PHASES DONE. The five mockups in `D:\Dev\NgocTrangBuilding\Docs\front-end\building_rooms\` assume data the backend had nowhere to put: rent, equipment per room, photos, and room/object geometry. Phase 1 added exactly that layer; Phase 2 gave the frontend the typed client, query hooks and primitives the screens are built from; Phase 3 built screens 1, 2 and 4 on top of them; Phase 4 built screen 3, the floor-plan designer; Phase 5 built screen 5, the tenant room search.

**What Phase 1 added:**
- `Room` gains `MonthlyRent` plus `LayoutX/Y/Width/Height/Shape/RotationDegrees/Points`, written by two dedicated methods (`UpdateRent`, `UpdateLayout`) so the designer can move a room without resending its whole profile. `Room.Update(...)` is untouched.
- Three new aggregates, all `FullAuditedAggregateRoot<Guid>`: `RoomEquipment`, `RoomPhoto` (+ `RoomPhotoContent` sibling table for bytes), `FloorLayoutItem`.
- Fourteen new endpoints (10 on `RoomAppService`, 4 on `FloorAppService`), **all on the three services the controller convention whitelists** (`BuildingAppService`/`FloorAppService`/`RoomAppService`); every one opens with `_access.EnsureResourceAsync(...)` and wraps its mutation in `_writes.ExecuteBuildingAsync(...)`. No new app service, no new permission.

**Two design corrections forced by the codebase:**
- Photo bytes cannot use ABP `IBlobContainer`: `AbpBlobs` lives in the **Identity** database, so a photo write would need a cross-database transaction. Bytes therefore live in `BuildingManagementRoomPhotoContents`, in the same database, one-to-one on the photo id with `ON DELETE CASCADE`.
- The migration is **hand-written raw SQL**. The committed `BuildingManagementDbContextModelSnapshot.cs` is deliberately stale (42 entities vs 112 in the model), so `dotnet ef migrations add` emits a 2165-line migration recreating ~70 tables. Everything after `20261021085624` follows the raw-SQL, no-Designer convention.

**Photo safety:** acceptance is decided from the payload's magic bytes (PNG/JPEG/WebP) and never from the client-declared type; SVG is rejected by omission so stored content can never execute as a document; file names are rebuilt from the sniffed type after `Path.GetFileName`, so a client path cannot reach storage; a 5 MB cap is enforced in the application and again by a `CHECK (octet_length(...) BETWEEN 1 AND 5242880)` in the schema.

**Two defects the harness caught (both fixed before the commit):**
- `RoomPhotoContent` inherited `Entity<Guid>`, so EF mapped the key to a column named `Id`, while the migration created `PhotoId` — the first upload failed with `42703: column "Id" of relation "BuildingManagementRoomPhotoContents" does not exist`. Fixed by mapping the property to `PhotoId`.
- The new business error codes had no `AbpExceptionHttpStatusCodeOptions` mapping, so a rejected upload answered **403** (ABP's default) instead of 400. Ten mappings were added in `NgocTrangHouseManagementSystemHttpApiHostModule`.

**Evidence:**
- ✅ Commit `3adf6f6` pushed to `origin/main`, fast-forward only
- ✅ E2E harness `etc/scripts/verify-p45-mockup-screens.ps1` — **ALL STEPS PASSED at the pushed SHA** (`3adf6f68eb7a2270326664c9f62489a8046c9fd0`)
- ✅ Schema proven in PostgreSQL, not just by exit code: the four P45 tables exist and `BuildingManagementRooms` has all 8 new columns
- ✅ Rent `3500000` round-trips through `PUT /room/{id}/rent` and agrees with `BuildingManagementRooms.MonthlyRent`
- ✅ Equipment: 2 created, 1 updated (quantity 3, condition 1), 1 deleted with `IsDeleted=true` read from the DB; only 1 row remains
- ✅ Photo: a 1×1 PNG built in-process uploads as `image/png` with `FileName` sanitised from `../../etc/evil.png` to `evil.png`; `GET .../photo-content/{id}` returns **byte-for-byte identical** bytes; a text payload named `fake.png` is rejected **400 `BuildingManagement:InvalidRoomPhotoContent` and writes no row**
- ✅ `PUT /room/{id}/layout` round-trips (2.5, 3, 6, 4.5) and `EquipmentCount`/`PhotoCount` are accurate on the same read
- ✅ `POST`/`GET` floor layout item round-trips; negative control on a random building id still denied
- ✅ Cleanup verified row-by-row in PostgreSQL: equipment/photo/layout-item `IsDeleted=t`, the bytes row **hard-deleted**, rooms/floor/building soft-deleted
- ✅ LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p45-mockup-screens-20261115-134953` (earlier iterations `...-131531`/`...-132051`/`...-132313`/`...-134056` record the `::text` boolean spelling, the `PhotoId` column mismatch, and the 403-before-mapping diagnosis)
- ✅ `dotnet build -c Release`: 0 errors (34 pre-existing warnings); host test suite **76/76**
- ✅ Disposable lane only: Host 44321 + Vite 5273, two `bm_slicea_staging_*` DBs on `bm-p21-pg-15432`, both dropped in `finally`; 44311/5173, 15433, 5432, production DB, payOS untouched; admin password generated in-process, never echoed

**Known limitations (recorded in `summary.json`):** photo bytes come back base64-encoded inside the JSON envelope via `RoomPhotoFileDto` rather than as a binary stream, so the frontend phase must decode them client-side (a real stream endpoint is deferred and would need its own probe through the conventional controller). Floor objects support only the 7 kinds in `FloorLayoutItemKind`; free-form shapes in the mockup (trees, balconies) are out of scope.

**Definition of Done (Phase 1):**
- ✅ Rent, equipment, photos, room geometry and floor objects persist and read back
- ✅ Every new endpoint sits on an already-whitelisted app service and reuses existing permissions
- ✅ Photo acceptance proven from magic bytes, with the fake-`.png` rejection asserted end-to-end
- ✅ Schema and deletes proven in PostgreSQL, not by HTTP status
- ✅ Harness run at the pushed SHA

---

#### Phase 2 — frontend data layer (DONE at `a9f424a`)

**What Phase 2 added (11 files, +969/−3):**
- `frontend/src/lib/api/types.ts` — DTOs for `RoomEquipment`, `RoomPhoto` (+ `RoomPhotoFileDto` for the base64 bytes), `FloorLayoutItem`, the matching create/update inputs and `UpdateRoomLayoutInput`; three enum unions (`RoomLayoutShape`, `RoomEquipmentCondition`, `FloorLayoutItemKind`); two metadata maps the screens render from (`equipmentConditionMeta`, `floorLayoutKindMeta`). `RoomDto` gains the ten P4.5 fields as **required** properties.
- `frontend/src/lib/api/buildingApi.ts` — 14 new calls + `fileToBase64()`. The route map in the header comment was read off the running host during Phase 1 rather than guessed from action names.
- `frontend/src/lib/api/buildingQueries.ts` — four scoped query keys (`room-equipment`, `room-photos`, `room-photo-content`, `floor-layout`), 14 hooks, and two invalidation helpers. Every room write refreshes the room's equipment, photo list **and** photo bytes; every floor-layout write refreshes the floor's layout items.
- `frontend/src/lib/api/mutationErrors.ts` — `MutationEntity` widened with `equipment | photo | layout | rent`; new codes mapped to fields (`quantity`, `monthlyRent`, `layout`, `photo`) and eight new Vietnamese explanations.
- `frontend/src/lib/format.ts` + 12 tests — `formatNumber`, `formatVnd`, `parseVndInput`, `formatMillions`.
- `frontend/src/components/ui/Tabs.tsx` (WAI-ARIA tablist + `TabPanel`), `PhotoGallery.tsx` (large frame + thumbnail strip + "+N"), `MoneyInput.tsx` (grouped VND box).

**Decisions worth recording:**
- The ten new `RoomDto` fields are **required, not optional**. Making them optional would let a server-side DTO regression render blanks instead of failing to compile; the snapshot test fixture was updated instead.
- `parseVndInput` accepts the grouped form `formatNumber` itself emits (`3.500.000`) and a trailing currency word, so a value can be pasted back into the box without being rejected — the round-trip is the property that matters, not strictness.
- The soft-deleted-number 500 explanation is now scoped to `floor`/`room` create only. Previously any non-building create got it; an equipment failure would have been mislabelled as a reused room number.
- Photo bytes are fetched per-photo (`useRoomPhotoContent`, `staleTime` 5 min) rather than all in the gallery query, so a room with many photos does not pull megabytes before the first paint.

**Evidence:**
- ✅ Commit `a9f424a` pushed to `origin/main`, fast-forward only (`907f0da..a9f424a`), no `Co-Authored-By` trailer
- ✅ `npm run test`: **64/64 passing** (7 files) — the 47 pre-existing, 12 new `format.test.ts`, 5 new `mutationErrors` cases
- ✅ `npm run build` (`tsc --noEmit && vite build`): exit 0, 126 modules, 415.86 kB JS / 52.19 kB CSS
- ✅ Phase 1 harness re-run **at the pushed SHA `a9f424a`**: **ALL STEPS PASSED**, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p45-mockup-screens-20261115-140935` (background run, exit code 0) — proving the frontend slice touched nothing on the server side
- ✅ Two TS defects caught by `tsc` and fixed before the commit (`Tabs.tsx` arrow-key cursor typed `number | null` when `enabled[]` can yield `undefined`, and an unchecked index into `items[]`)
- ✅ No file outside `frontend/` touched; the untracked strays (`etc/phase-audits/…sql`, `etc/scripts/start-p43-dev-lane.ps1`, `etc/scripts/verify-p41-frontend-auth-e2e.ps1`, `etc/test-results/`, root `package-lock.json`) left uncommitted

**Definition of Done (Phase 2):**
- ✅ Every Phase 1 endpoint has a typed client call and a query/mutation hook
- ✅ Writes invalidate the scoped keys, not just the three flat lists
- ✅ New error codes surface as Vietnamese messages on the right field
- ✅ Vitest + `tsc` + production build green at the pushed SHA
- ✅ Phase 1 harness still passes at the Phase 2 SHA

#### Phase 3 — screens 1, 2 and 4 (DONE at `3c3e2ea`)

**What Phase 3 added (9 files, +1743/−41):**

- `frontend/src/lib/floorGeometry.ts` + 15 tests — the drawing maths, kept out of the component so it can be tested without a DOM. `hasGeometry` / `unplacedRooms` decide what can be drawn; `buildPlanGeometry` returns `null` when no room on the floor has coordinates (which is how the caller knows to fall back); `clampZoom`/`zoomIn`/`zoomOut`/`formatZoom`/`viewBoxFor` are the whole zoom model, clamped to 50–300 % in 25 % steps.
- `frontend/src/features/buildings/FloorPlan.tsx` — rewritten. Draws an SVG from `buildPlanGeometry` with a metre grid, polygon support (`layoutPoints`), rotation (`transform="rotate(...)"` about the room centre) and floor objects layered underneath. Falls back to the old area-proportional schematic when no room is placed, and the two states are labelled differently so the owner can tell which drawing they are looking at.
- `frontend/src/features/buildings/FloorPlanPage.tsx` — added the building and floor dropdowns from mockup 2, wired the floor's layout objects, added rent / equipment count / photo count to the room panel, and a "Xem chi tiết phòng" link. The "Sửa sơ đồ" control is a **disabled button with a tooltip**, not a link: the designer route does not exist until Phase 4 and a link would 404.
- `frontend/src/features/rooms/RoomDetailPage.tsx` — new route `/rooms/:roomId`. Photo gallery on the left (bytes fetched only for the four photos it can show), info panel on the right, rent editable in place through `MoneyInput`, a compact locator plan, and four tabs: the equipment tab is real (list / add / edit / delete through `EquipmentFormDialog`), while contracts, invoices and maintenance show a gate notice naming the reason.
- `frontend/src/features/rooms/EquipmentFormDialog.tsx` — create/edit dialog; quantity must be a positive integer, `installedDate` is preserved from the initial record rather than silently dropped.
- `frontend/src/features/rooms/RoomsPage.tsx` — room number is now a link to the detail page, and each card shows the rent.
- `frontend/src/features/buildings/BuildingsPage.tsx` — new "Giá thuê từ" column on the floor table, computed from the floor's rooms (em dash when no room has a price).
- `frontend/src/App.tsx` — the `/rooms/:roomId` route.

**Decisions worth recording:**
- **Honest fallback over fabricated geometry.** Showing the schematic when coordinates are missing, and saying how many rooms are unplaced when only some are, beats silently drawing a shorter plan.
- **Gate notices, not stub data.** Contracts, invoices and maintenance are denied 403 by the controller convention. Rendering invented rows would make the screen look finished while lying about the system; the notice names the actual reason.
- **No dead links.** "Sửa sơ đồ" is disabled with a tooltip until Phase 4 ships the route.
- **Zoom is a viewBox change, not a CSS transform**, so stroke widths and text stay crisp at any level.

**Evidence:**
- ✅ Commit `3c3e2ea` pushed to `origin/main`, fast-forward only (`a09afed..3c3e2ea`), no `Co-Authored-By` trailer
- ✅ `npm run test`: **79/79 passing** (8 files) — the 64 from Phase 2 plus 15 new `floorGeometry` cases
- ✅ `npm run build` (`tsc --noEmit && vite build`): exit 0, 133 modules, 456.65 kB JS / 53.24 kB CSS
- ✅ Phase 1 harness re-run **at the pushed SHA `3c3e2ea`**: **ALL STEPS PASSED**, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p45-mockup-screens-20261115-142412` — a frontend-only commit is confirmed to change nothing server-side
- ✅ No file outside `frontend/` touched; the untracked strays left uncommitted as before

**Definition of Done (Phase 3):**
- ✅ Mockup 2 draws real saved geometry when it exists and says so when it does not
- ✅ Mockup 4's equipment tab works end-to-end; the three unavailable tabs state why
- ✅ Mockup 1's floor table carries a rent figure derived from real rooms
- ✅ Vitest + `tsc` + production build green, harness green at the pushed SHA

**Not verified by this phase:** the screens were type-checked and built, but not driven in a browser — the Owner opens the dev lane and clicks through by hand.

**Blocker:** None. Phase 5 supersedes this note — see below.
**Dependency:** P4.4 ✅ (the CRUD screens whose resources this extends); Phase 1 ✅ (`3adf6f6`); Phase 2 ✅ (`a9f424a`)

#### Phase 4 — screen 3, the floor-plan designer (DONE at `0620fd4`)

**What Phase 4 added (8 files, +1959/−11):**

- `frontend/src/lib/floorGeometry.ts` — extended with the designer's maths: `GRID_STEP_METRES = 0.1`, `MIN_SIZE_METRES = 0.5`, `snapToGrid`, `clampToLimits`, `pointToMetres`, `moveBox`, `resizeBox` (opposite corner fixed, minimum-size floor), `clampBoxToBounds`, `defaultRoomBox`, `boxesEqual`, `formatMetres`, and `viewBoxRectFor` split out of `viewBoxFor` so panning is testable.
- `frontend/src/lib/floorGeometry.test.ts` — 37 tests (22 new) covering the snap, all four resize corners, the corner-past-the-opposite-edge guard, clamping to the drawing, and pan.
- `frontend/src/features/buildings/FloorDesignerCanvas.tsx` — the drawing surface. Hand-written SVG, no new dependency. Renders a 1 m minor grid and a 5 m major grid, draws rooms and objects with the same status colours as `FloorPlan`, and handles `move` / `resize` / `pan` drags through one `DragState` union. Pointer positions are converted with `getScreenCTM().inverse()`, so the maths is identical at any zoom or pan. Live feedback is local preview state; exactly one write is issued when the pointer is released.
- `frontend/src/features/buildings/FloorDesignerPage.tsx` — new route `/buildings/:buildingId/floors/:floorId/design`. Left palette lists the floor's rooms (placed vs unplaced) and the seven object kinds, all draggable onto the canvas. Right panel edits the selection numerically (label for objects; X/Y/width/height for both, plus room number, area, status, rent read-out). "Lưu sơ đồ" saves only the dirty records, sequentially, and names the record that failed.
- `frontend/src/features/buildings/FloorDesignerPage.test.tsx` — 5 component tests: the unplaced/placed counter, a drop reaching the layout endpoint with the expected payload, a palette drop creating the right object kind, a rejected create keeping its draft so a retry does not duplicate it, and a partial failure dropping only the drafts that reached the server.
- `frontend/src/lib/api/buildingQueries.ts` — `useSaveRoomLayouts`, a mutation that takes the room id with each call because the designer saves a whole floor at once.
- `frontend/src/features/buildings/FloorPlanPage.tsx` — "Sửa sơ đồ" is now a `Link` to the designer route.
- `frontend/src/App.tsx` — the designer route.

**Four defects caught in this phase's own review (all fixed before the commit):**
- **Bounds computed from drafts made the box run away from the cursor.** Growing the viewBox mid-drag rescales the drawing under the pointer. Bounds now follow saved geometry only, so the drawing grows when the change is saved.
- **A partially failed save could duplicate a created object.** The first draft cleared every draft on success and kept every draft on the first failure, so a new object that *had* been created stayed in the draft list, drew twice, and was posted again on the next save — creating a duplicate. Each record is now dropped the moment its own write succeeds.
- **Saving a room would have erased its polygon.** The payload hard-coded `shape: 1, rotationDegrees: 0, points: null`, which flattens any room already drawn as a `Polygon`. The existing shape, rotation and points are now carried through unchanged.
- **`getScreenCTM` was assumed to exist.** jsdom (and some embedded webviews) do not implement it, so the first drop threw instead of returning a point. Now called optionally with an origin fallback.

**Evidence:**
- ✅ Commit `0620fd4` pushed to `origin/main`, fast-forward only (`ae9f461..0620fd4`), no `Co-Authored-By` trailer
- ✅ `npm run test`: **106/106 passing** (9 files) — the 79 from Phase 3 plus 22 `floorGeometry` designer cases and 5 designer-page cases
- ✅ `npm run build` (`tsc --noEmit && vite build`): exit 0, 135 modules, 480.90 kB JS / 53.62 kB CSS
- ✅ Phase 1 harness re-run **at the pushed SHA `0620fd4`**: **ALL STEPS PASSED**, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p45-mockup-screens-20261115-144211` — the designer saves through the same `PUT /room/{id}/layout` and floor-layout-item routes the harness proves
- ✅ No file outside `frontend/` touched; no backend or migration change

**Definition of Done (Phase 4):**
- ✅ Drag to move, corner handles to resize, 0.1 m snap, pan, zoom, metre grid
- ✅ Left palette drops both rooms and the seven object kinds
- ✅ Right panel edits geometry numerically and can unplace a room / delete an object
- ✅ Save sends only dirty records and reports per-record failures
- ✅ Vitest + `tsc` + production build green, harness green at the pushed SHA

**Not verified by this phase:** the designer was driven by component tests and the harness, **not clicked through in a real browser** — pointer capture and `getScreenCTM` behave differently under jsdom, so dragging must be confirmed by hand on the dev lane. Polygon editing on canvas is also not implemented (the designer edits the bounding rectangle and preserves the polygon fields).

**Known limitations stated in the UI:**
- Only the seven `FloorLayoutItemKind` values can be added; free-form shapes from mockup 3 (trees, balconies) are not supported.
- No optimistic locking: two tabs editing one floor resolve last-write-wins.
- The declared area (`room.area`) and the drawn size are independent; the designer does not rewrite the area.

---

#### Phase 5 — screen 5, the tenant room search (DONE at `5adbf42`)

**What Phase 5 added (4 files, +662/−1):**

- `frontend/src/features/rooms/RoomSearchPage.tsx` (new) — route `/search`. Lists **only** rooms whose `status === 1` (Trống), filtered by an optional building picker and a free-text box matching room number, room name or building name. The room card/pane reuses what earlier phases already built rather than re-implementing it: `PhotoGallery` (bytes fetched only for the four thumbnails it paints, through `useQueries`), `FloorPlan` in compact mode for "Vị trí phòng trên mặt bằng", `useRoomEquipment` for "Tiện ích phòng" (only items whose condition is not Good get a badge), and `formatVnd` / `roomStatusMeta` for the labels.
- `estimateFirstPayment(monthlyRent, months)` — exported, pure, and the whole pricing model: one month of deposit plus `months` of rent, computed in the browser. The pane states plainly that the figure is an estimate and not an invoice.
- "Đặt phòng ngay" is a **disabled** button whose `title` is exactly `Chức năng đặt phòng chưa được mở trên máy chủ.` — `RoomReservationAppService` is denied 403 by the controller convention, so a live button would be a lie.
- `frontend/src/features/rooms/RoomSearchPage.test.tsx` (new, 4 tests) — the estimate arithmetic, the available-only filter (occupied and maintenance rooms asserted absent), the disabled button and its tooltip, and the estimate recomputing when the lease length changes (6 → 12 months), plus the empty state when every room is taken.
- `frontend/src/App.tsx`, `frontend/src/features/shell/AppShell.tsx` — the `/search` route and a "Tìm phòng" nav entry next to "Tổng quan".

**Decisions worth recording:**
- **The "Quy định nhà trọ" and "Thông tin khác" blocks from mockup 5 are not rendered.** `BuildingManagementSettings` is an empty constants class with no setting names defined, so there is no electricity price, water price, service fee or house rule to read. Rather than invent 3.500 đ/kWh, the pane says those figures are not published on the server. Same rule as the three gate notices in Phase 3.
- **No "Lưu" / "Chia sẻ" buttons from the mockup.** Both imply persistence and sharing that this screen has no API for; a disabled copy of the booking button already carries the message, so adding two more dead controls would only add noise.
- **The estimate is per-room and client-side by design**, because there is no pricing endpoint. When the reservation service opens, the estimate becomes a server quote and this function is the thing to replace.
- **The selected room falls back to the first result** when a filter removes it, so the detail pane never renders against a dangling id.

**Evidence:**
- ✅ Commit `5adbf42` pushed to `origin/main`, fast-forward only (`ce3924e..5adbf42`), no `Co-Authored-By` trailer
- ✅ `npm run test`: **110/110 passing** (10 files) — the 106 from Phase 4 plus 4 new `RoomSearchPage` cases
- ✅ `npm run build` (`tsc --noEmit && vite build`): exit 0, 136 modules, 493.24 kB JS / 54.04 kB CSS
- ✅ Phase 1 harness re-run **at the pushed SHA `5adbf42`**: **ALL STEPS PASSED**, exit code 0, LOG_DIRECTORY `C:\Users\Huynq\NgocTrang-Test-Logs\p45-mockup-screens-20261115-145728` — a frontend-only commit is confirmed to change nothing server-side
- ✅ No file outside `frontend/` touched; the untracked strays left uncommitted as before (root `package-lock.json`, `etc/phase-audits/…sql`, `etc/scripts/start-p43-dev-lane.ps1`, `etc/scripts/verify-p41-frontend-auth-e2e.ps1`, `etc/test-results/`)

**Definition of Done (Phase 5):**
- ✅ Only available rooms are listed, with photo, rent, area, capacity and equipment
- ✅ The estimate is client-side, labelled as an estimate, and updates with the lease length
- ✅ "Đặt phòng ngay" is disabled and names the server-side reason
- ✅ Blocks with no server data are hidden and explained, not fabricated
- ✅ Vitest + `tsc` + production build green, harness green at the pushed SHA

**Not verified by this phase:** the screen was component-tested and harness-tested, **not clicked through in a browser** — the Owner opens the dev lane and confirms it by hand, same caveat as Phases 3 and 4.

---

### P4.6 ✅ Building Screen Rebuilt Against Figma — COMPLETE (`3553f0e` + `0960b71`, pushed)
**Status:** DONE — the Owner supplied the fidelity master prompt (`NgocTrang_Figma_Fidelity_Repair_Master_Prompt.md`) and five reference images, and the binding rule is that a screen is DONE only with rendered screenshots compared against the mockup, never on a green build. The building screen was rebuilt to mockup 1's structure, and every toolbar control the mockup shows now does real work rather than being decoration.
**Evidence:** commits `3553f0e` and `0960b71` pushed to `main`, fast-forward only, no `Co-Authored-By` trailer.
**Blocker:** None.

---

### P4.7 ✅ Real Walls, One Storey Envelope, Stable Pan — COMPLETE (`cda83a7` + `9715605`, pushed)
**Status:** DONE — two rounds of Owner feedback, both correct against the mockup:
- `cda83a7` — rooms stroke a real 0.12–0.30 m masonry band with a door opening and swing arc; the pan shake was a feedback loop (`toMetres()` inverted a CTM derived from the offset the pan itself wrote, so every pointer move fed back into the next — the scale is now sampled once at drag start and the pan works in client pixels); every palette kind landed as a 1.6 m cube and now has its own shape.
- `9715605` — the Owner's point was that the wall belongs to the **storey**, not to each room. Stroking each room's own edges fragments the envelope because the corridor ends, the stairwell and the slots between room blocks belong to no room. `PlanEnvelope` rasterises the outline of everything the floor occupies, keeps every cell edge facing open space, merges collinear runs, and bridges the designed 0.4 m slots before merging so they do not cut sawtooth notches.
**Evidence:** both commits pushed to `main`; `tsc` clean, 134 tests, `vite build` exit 0, screenshots at four viewports.
**Blocker:** None.

---

### P4.8 ✅ Walls Bend, Curve and Carry Their Own Thickness — COMPLETE (`84520bb`, pushed)
**Status:** DONE — the Owner asked whether a wall could be curved, because walls are not always dead straight. A wall was a rectangle and could only ever be straight; the mockup's storey outline is chamfered at the top corners, cut back at the bottom right and projects over the stair bay.
**What shipped:** `Points`/`Thickness`/`CornerRadius` on `FloorLayoutItem`, all nullable so a wall saved before the change draws from its box unchanged. Double-click a wall to insert a vertex (projected onto the nearest segment), drag each vertex in metres, move the wall to translate the whole run. Side panel gains thickness, a radius slider between the bevel and the rounded corner, the vertex count, and "Về đường thẳng". `roundedPolyline` clamps each fillet to half the shorter leg so it cannot swallow a segment and picks the arc sweep from the turn's cross product (SVG's y axis points down). The body is a thick miter stroke — which closes every joint without hand-building offset polygons — with a light core like a drawing's hatch line. Vertex handles were sized from the box, which for a 26 m wall painted discs over the drawing; they now use the fixed handle size. A bent wall goes unlabelled because its box centre is not on the wall. Validation moved to `FloorLayoutItemRules` so it is tested without an app service.
**Evidence:** `dotnet build` 0 errors, **247** backend tests, `tsc` clean, **170** frontend tests, `vite build` exit 0, **10** rendered screenshots at 1448×1086 / 1280×800 / 1024×768 / 390×844, plus a real-browser probe that double-clicks the outer wall at metre (12, 4) and reads **13 vertices** back out of the API after saving.
**Blocker:** None.

---

### P4.9 ✅ Room Detail Screen Rebuilt Against Mockup 1 — COMMITTED `544e7be`
**Status:** DONE in the working tree (not yet committed). Compared against `Anh_1_nguyen_goc.png`, the screen was the largest remaining gap: two columns with three stacked cards and the equipment table on its own row, while the mockup sets **photos | facts | locator side by side** over a full-width tab strip with the note panel beside the equipment table.
**What changed (3 modified + 1 new file):**
- `RoomDetailPage.tsx` — `lg:grid-cols-2 xl:grid-cols-[1.05fr_0.95fr_1fr]`; the locator card spans both columns between 1024 and 1280 (`lg:col-span-2 xl:col-span-1`) rather than leaving a third of the screen empty. The status badge sits beside the title. The locator gets a building-wide floor picker. The chip count on the equipment tab is the real row count. "Chỉnh sửa" opens the same `RoomFormDialog` the room list uses.
- `PageHeader.tsx` — new `titleAddon` slot for that badge.
- `planDrawing.tsx` — partitions measured off the mockup at **0.22–0.28 m** (its plan is roughly 26 px per metre and a partition is a 5 px band ≈ 0.2 m, one brick wythe with plaster both faces); the previous 0.1–0.2 m ramp shrank walls on small rooms so a 2 m bathroom drew a pencil line beside a 5 m bedroom's band. `WALLED_KINDS` walls corridor/stair/lift/WC/service/laundry/storage at partition weight, since the mockup walls them exactly like the rooms they serve.
- `RoomDetailPage.test.tsx` (new, 6 tests) — status beside the title, three separate panels, the floor picker, note panel sharing the tabpanel with the table, the edit dialog opening on `A203`, and both buttons disabled with a 403 tooltip when the permission is off.
**Three responsive defects found only by reading the rendered PNGs** (no test would have caught any of them):
- 1024×768 collapsed to a single **2655 px** column — the grid only had an `xl:` breakpoint. Fixed with `lg:grid-cols-2` + the locator spanning.
- 390×844 clipped the equipment table's action column and pushed the note panel past the right edge — the grid children had default `min-width: auto`. Fixed with `min-w-0` on both panes and `grid-cols-1` on the container.
- 390×844 still overflowed because `min-w-[420px]` was unconditional. The table now takes `sm:min-w-[420px]`, hides STT, and drops the button captions to icons below `sm`, with `aria-label` kept for screen readers.
**Evidence:**
- ✅ `tsc --noEmit` exit 0; `npx vitest run` **176/176 in 16 files**
- ✅ Four rendered captures at 1448×1086 / 1280×800 / 1024×768 / 390×844 in `etc/captures/p49/`, produced by `etc/scripts/capture-p49-room-detail.mjs` which seeds a real building, floor 3, eight rooms, an outer wall polyline, a corridor, a stair and a lift on the disposable lane, then signs in and screenshots the room page at each viewport
- ✅ Each capture was opened and compared against `Anh_1_nguyen_goc.png`; the three defects above were found that way and re-captured after each fix
- ✅ The capture script reads the admin password from `LANE_ADMIN_PASSWORD` and never writes it down
**Blocker:** None — awaiting the Owner's word to commit.
**Dependency:** P4.8 ✅ (wall thickness and the polyline wall the locator draws), P4.5 ✅ (the room page this rebuilds)

---

### P4.10 ✅ Tenant Room Screen Rebuilt Against Mockup 2 — CODE DONE, AWAITING COMMIT
**Status:** DONE in the working tree (not yet committed). Compared against `Anh_2_nguyen_goc.png`, `/search` was still a search list with a detail card bolted on — a filter band across the top, three stacked cards in the main column and an "available rooms" list in the rail — while the mockup is a room page: breadcrumb, title with the building/floor/room-code line under it and Lưu / Chia sẻ beside it, then **photos | facts | booking** in one row, then **floor locator | amenity chips**, then **house rules | other prices**.
**What changed (3 modified + 1 new file):**
- `RoomSearchPage.tsx` — the mockup's grid; the building/query filters moved into a bottom "Phòng đang trống khác" strip so the page opens on the room, not on a filter form; the locator gets the mockup's floor dropdown and follows the room's own floor by default; amenities are the mockup's icon chips; the info card follows the mockup's field order with Giá thuê in the primary colour and Tình trạng as a chip.
- `RoomSaveShare.tsx` — takes `href`, so a shared link points at `/search?room=<id>` and not at the admin room page.
- `SelectField.tsx` — `hideLabel`, for the inline floor dropdown that the mockup draws without a visible caption (the label stays for screen readers).
- `equipmentIcon.ts` (new) — the per-item glyph rule moved out of `RoomDetailPage.tsx` so the admin table and the tenant chips cannot drift apart; it also learned wifi, balcony and bathroom.
- `RoomSearchPage.test.tsx` — 5 → 6 tests: the subtitle line, the two blank fields, every section heading, the "not published" wording, and the deep link opening the room named in the URL.
**A silent Tailwind v4 trap:** `min-[1400px]:grid-cols-[...]` **sorts before** `md:grid-cols-2` in the generated sheet, so the three-column template never applied at 1448 px and the page rendered as two 577 px columns. Only the rendered PNG showed it; the fix is to make the ranges disjoint (`sm:max-[1399px]:grid-cols-2` + `min-[1400px]:grid-cols-[...]`).
**Honest gaps (BLOCKED, not faked):**
- **Hướng (direction) and Loại phòng (room type)** — `RoomDto` has no such column. The rows render "Chưa cập nhật" with a note under the card; adding them is a schema change and §9 forbids that without approval.
- **Quy định nhà trọ and Thông tin khác (electricity / water / service prices)** — `BuildingManagementSettings` defines no setting names, so both cards render an explicit "chưa được công bố trên máy chủ" instead of invented rules or prices.
- **Đặt phòng ngay** — the booking app service is not exposed (403); the button stays disabled with that reason, as §8 requires.
- **Room code prefix** — the server accepts digits only, so the mockup's "A203" can only come from the room's `name`; the title now uses the name and the subtitle keeps "Mã phòng: 203".
- **Photos** — the repository holds no room photography; the captures use six generated gradient PNGs uploaded through the real endpoint, so the gallery layout is real but the pictures are placeholders.
- **Bottom strip** — "Phòng đang trống khác" is not in the mockup; it is kept because without it a tenant cannot move between rooms on this screen.
**Evidence:**
- ✅ `tsc --noEmit` exit 0; `npx vitest run` **178/178 in 16 files**
- ✅ Four rendered captures at 1448×1086 / 1280×800 / 1024×768 / 390×844 in `etc/captures/p50/`, produced by `etc/scripts/capture-p50-tenant-room.mjs` (seeds a building with three floors, eight placed rooms on floor 2, an outer wall polyline, corridor, stair, lift, eight amenities and six photos) and re-shot by `recapture-p50-tenant-room.mjs` after each fix
- ✅ Each capture opened and compared against `Anh_2_nguyen_goc.png`; the 1280 squeeze, the 1024 photo height and the truncated chip label were found that way and re-captured after fixing
- ✅ Both scripts read the admin password from `LANE_ADMIN_PASSWORD` and never write it down
**Blocker:** None for the layout; the five BLOCKED items above need either an Owner decision or a backend change.
**Dependency:** P4.9 ✅ (the shared equipment-icon and room-detail conventions), P4.5 ✅ (the tenant screen this rebuilds)

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
- P1: 5/5 done (P1.1 official at `f30e14d`; P1.2 rehearsal gate at `d085d02`; P1.4 crash/recovery at `682613d`; P1.3 vault + restricted roles at `18fe521`, 64/64; P1.5 internal simulated payOS staging E2E at `e58b12c`, 20/20)
- P2: 6/6 **MERGED INTO `main`** (PR #11 merge commit `72a0721`, 2026-11-13, no squash — P2.1 `d36edb5` / P2.2 `b18644b` / P2.3 `aa3b9bb` / P2.4 `950610a` / P2.5 `2d01952` / P2.6 `6c9852f`; local 5-group harness ALL PASSED at `72a0721`; post-merge CI stale-assertion fix `d364ee1` with CI run 43423912631 green 5/5)
- P3: 4/4 done + **MERGED to main** (P3.1 merge `01cba31`; P3.2 DONE `1ddf2e6`; P3.3 matrix; **P3.4 CLOSED 2026-11-12: PR #10 → main merge commit `c499d89`, Owner §6 signed + §3.2 P2 deferral accepted; post-merge local regression + CI green 5/5 at the merge commit**)

**Current Focus:** **P4.10 TENANT ROOM SCREEN DONE IN THE TREE (2026-11-17), awaiting the word to commit** — against Anh_2_nguyen_goc.png the tenant room page was still a search list: a filter band across the top, three stacked cards and an "available rooms" list in the rail. It is now the mockup's page: breadcrumb, title with "Tòa nhà · Tầng · Mã phòng" under it and Lưu / Chia sẻ beside it, photos | facts | booking in one row, floor locator | amenity chips, house rules | other prices, and the room switcher moved into a bottom strip with the building and text filters. The room id lives in ?room= now, so Chia sẻ copies a link that actually opens the same room. Direction and room type have no server column and the house rules / electricity prices no source, so those read "Chưa cập nhật" and "chưa được công bố" rather than invented values — that is the honest PARTIAL, not a missing screen. One silent defect only the rendered PNG could show: Tailwind v4 sorts min-[1400px]: before md:, so the three-column template never applied at 1448 px and the page fell back to two 577 px columns; the ranges are disjoint now. tsc --noEmit clean, Vitest 178/178, four captures at 1448x1086 / 1280x800 / 1024x768 / 390x844 in etc/captures/p50/.

**Earlier (2026-11-15 to 2026-11-17):** P4.8 84520bb made a wall a centreline that bends and curves with its own thickness and corner radius; P4.7 9715605 + cda83a7 drew one storey-wide wall instead of per-room strokes and fixed the pan feedback loop; P4.6 3553f0e + 0960b71 rebuilt the building screen against the mockup with every toolbar control wired. Still open: the tenant screen house-rules and electricity/water/service prices are BLOCKED — BuildingManagementSettings defines no setting names; real payOS money flow, TLS public callback, production vault/roles/secrets and production deploy stay HOLD.

**Previous focus, kept for provenance:** **P4.5 DESIGNER REVISIONS DONE (2026-11-15): the palette carries the mockup's eleven object kinds and the canvas fills the screen, at `75d0488` + harness `944de2f` pushed to `main`** — the Owner reported that a room was made of too few components and that the diagram background was too small, so the designer was re-checked against mockup 3 rather than patched blind. `FloorLayoutItemKind` gained `Laundry`/`Storage`/`Balcony`/`Tree` (the mockup's palette lists eleven objects; the first cut shipped seven), and because `CK_BM_FloorLayoutItem_Kind` pinned `Kind` to 1..7 in the database, a companion hand-written migration re-creates it as `BETWEEN 1 AND 11` — without it the new kinds would have been accepted by the C# enum and rejected by PostgreSQL. The canvas previously used `style={{ aspectRatio }}` with `h-auto`, so a shallow floor painted as a thin strip with empty space below it; it now uses `h-[clamp(480px,calc(100vh-310px),900px)]` with `preserveAspectRatio="xMidYMid meet"`, so the drawing fills the available height and centres the plan while metres stay square. Rooms now draw as thick walls with a door arc (`wallPath` + a `walled` prop, walls capped in metres so high zoom cannot swallow a small room), `MeasureRulers` puts metre ticks along the top and left edges with an adaptive 1/2/5/10/20/50 m step, and the grid strokes are darker so the plan reads against them. The page widened to `max-w-[1720px]` with narrower side rails, and the room panel now shows a read-only profile — name, area, rent, capacity, shape — above the geometry editor, matching the mockup's field list. `npm run test` **110/110**, `npm run build` exit 0 (136 modules, 496.10 kB JS), `dotnet build -c Release` on the whole `.slnx` **0 errors**, and the Phase 1 harness re-run green at the pushed SHA `944de2f` — ALL STEPS PASSED, exit code 0, LOG_DIRECTORY `p45-mockup-screens-20261115-171507` — including a new step that posts a kind-9 (Storage) object, reads `Kind` back out of PostgreSQL and deletes it, so a future re-narrowing of the CHECK fails loudly. Still **not verified in a browser**: pointer drag, the rulers' legibility at real screen sizes and the door arcs want the Owner's eyes. P4.5 PHASE 5 DONE (2026-11-15): the tenant room-search screen ships at `5adbf42` pushed to `main`, completing all five mockups** — `/search` lists only rooms the server reports as available, filtered by an optional building picker and a text box that matches room number, room name or building name; the detail pane reuses what earlier phases built instead of re-implementing it (`PhotoGallery` with bytes fetched only for the four thumbnails it paints, `FloorPlan` in compact mode for the locator, `useRoomEquipment` for the amenity grid, `formatVnd`/`roomStatusMeta` for labels); the first-payment estimate is a pure exported function (`estimateFirstPayment`) that adds one month of deposit to the lease total **entirely in the browser** and is labelled an estimate, not an invoice; "Đặt phòng ngay" is a **disabled** button whose tooltip says exactly *"Chức năng đặt phòng chưa được mở trên máy chủ."* because `RoomReservationAppService` is denied 403 by the controller convention; the "Quy định nhà trọ" and electricity/water/fee blocks from mockup 5 are **not rendered** — `BuildingManagementSettings` defines no setting names, so there is nothing to read and nothing is invented; the two "Lưu"/"Chia sẻ" buttons were left out as dead controls. `npm run test` **110/110** (4 new search-page cases), `npm run build` exit 0 (136 modules), and the Phase 1 harness re-run **at the pushed SHA `5adbf42`** passed every step, exit 0 (LOG_DIRECTORY `p45-mockup-screens-20261115-145728`). The screen is component-tested and harness-tested but **not clicked through in a browser** — the Owner confirms on the dev lane, same caveat as Phases 3 and 4. P4.5 PHASE 4 DONE (2026-11-15): the floor-plan designer ships at `0620fd4` pushed to `main`** — `/buildings/:buildingId/floors/:floorId/design` draws a hand-written SVG canvas with a 1 m/5 m grid, drag to move, four corner handles to resize, 0.1 m snapping and pan/zoom, all in the metre coordinates the server already stores, so a gesture commits exactly the numbers the owner saw; the left palette drops the floor's rooms (marked placed/unplaced) and the seven `FloorLayoutItemKind` values, the right panel edits label / X / Y / width / height numerically and can unplace a room or delete an object, and "Lưu sơ đồ" sends only the dirty records sequentially and names the record that failed; each draft is dropped the moment its own write lands, so a retry cannot duplicate a created object; bounds follow saved geometry only, because growing them mid-drag rescaled the viewBox and made the box run away from the cursor; the room payload carries the existing `shape`/`rotationDegrees`/`points` through, so saving a designer edit no longer flattens a polygon; "Sửa sơ đồ" on the plan page is now a link, which lifts the floor-plan HOLD in `Docs/front-end/DESIGN_REVIEW.md` (its remaining items — wall/door constraints, version history, conflict handling, file import, canvas polygon editing, mobile zoom/pan — are listed there explicitly). `npm run test` **106/106** (37 geometry cases + 5 designer-page cases), `npm run build` exit 0, and the Phase 1 harness re-run **at the pushed SHA `0620fd4`** passed every step (LOG_DIRECTORY `p45-mockup-screens-20261115-144211`). The designer is component-tested and harness-tested but **not dragged through in a real browser** — pointer capture and `getScreenCTM` behave differently under jsdom, so the Owner confirms dragging by hand. P4.5 PHASE 3 DONE (2026-11-15): mockups 1, 2 and 4 now run on real data at `3c3e2ea` pushed to `main`** — the floor map draws the saved metre coordinates with a metre grid, polygon and rotation support, floor objects underneath, and a 50–300 % zoom control, and falls back to the old area-proportional schematic **with a different caption** when a floor has no coordinates yet, so the owner can always tell which drawing is on screen; `FloorPlanPage` gained the building/floor dropdowns, the floor's layout objects, and rent / equipment / photo counts in the room panel; the new `/rooms/:roomId` detail screen fetches photo bytes only for the four photos it can show, edits rent in place, and carries a compact locator plan, with a **working** equipment tab (list/add/edit/delete) beside three gate notices for contracts, invoices and maintenance — those app services are still denied 403 by the controller convention, and no placeholder rows are invented for them. "Sửa sơ đồ" stays a **disabled button with a tooltip** rather than a link, because the designer route does not ship until Phase 4 and a link would 404. `npm run test` **79/79** (15 new geometry cases), `npm run build` exit 0, and the Phase 1 harness re-run **at the pushed SHA `3c3e2ea`** passed every step (LOG_DIRECTORY `p45-mockup-screens-20261115-142412`). Screens are type-checked and built but **not yet clicked through in a browser** — the Owner does that on the dev lane. At the time of that phase, Phases 4–5 (designer, tenant search) were not started, awaiting Owner approval; Phase 4 has since shipped at `0620fd4`. P4.5 PHASE 2 DONE (2026-11-15): the frontend data layer for the five Figma screens at `a9f424a` pushed to `main` — `types.ts` mirrors all four new server surfaces plus the two enum unions and metadata maps, with the ten new `RoomDto` fields **required** so DTO drift fails to compile rather than rendering blanks; `buildingApi.ts` targets the routes read off the running host in Phase 1 (`upload-photo`, `photo-content/{id}`, `photo/{id}`, `layout`, `rent`, `layout-items`, `layout-item`) with photos travelling as base64 in the JSON envelope because the conventional controller has no multipart pipeline; `buildingQueries.ts` adds four scoped query keys, 14 hooks and two invalidation helpers, so a room write refreshes that room's equipment, photo list and photo bytes, and a layout write refreshes the floor's items; `mutationErrors.ts` learns the new codes and now scopes the soft-deleted-number 500 explanation to floor/room create only; new `format.ts` (grouped VND, paste-tolerant parser) and the `Tabs`/`PhotoGallery`/`MoneyInput` primitives. `npm run test` 64/64, `npm run build` exit 0, and the Phase 1 harness re-run **at the pushed SHA `a9f424a`** passed every step (LOG_DIRECTORY `p45-mockup-screens-20261115-140935`), proving the frontend slice changed nothing server-side. **Phases 3–5 (the screens) are NOT started — awaiting Owner approval.** P4.5 PHASE 1 DONE (2026-11-15): backend data layer at `3adf6f6` — rent, equipment, photos, room geometry and floor-plan objects on the three whitelisted app services, hand-written raw-SQL migration (the committed model snapshot is deliberately stale), magic-byte photo validation, `PhotoId`/`Id` dialect and 403-fallthrough defects caught by the harness (LOG_DIRECTORY `p45-mockup-screens-20261115-134953`). P4.4 CLOSED (2026-11-14): create/edit/delete screens at `5427d4a`; its harness caught the `autoSave: false` soft-delete defect fixed at `7b80ce3`. P4.3 gate at `b769028`, P4.2 screens at `aea363c`, P4.1 auth portal at `46f4dee`. Real payOS money flow, TLS public callback, production vault/roles/secrets, production deploy stay HOLD.
