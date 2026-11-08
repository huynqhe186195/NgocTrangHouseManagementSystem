# D04-B manual bank review, separate Owner approval and HTTP -- 2026-11-07

**STATUS: SOURCE IMPLEMENTED, NEW CODE UNVERIFIED on Windows. D04-B NOT ACCEPTED.** Prior D04-A full disposable suite PASS belongs only to SHA 1a82efe4cb31618526a941c3c284b49707ea1284, not later commits.

No real payOS funds, production database, production schema or auto-worker has been activated.

## 2026-11-08 continuation -- integrated disposable three-DB test and controller guard

- The **second** D04-B ContractDeposit test now persists the signed/reconciled
  intent and inbox in `payos_ef_migration_test` using the reviewed isolated
  payOS EF migration. It no longer supplies the second financial writer
  with an in-memory staging reader.
- The same D04-B test still uses only **synthetic** document bytes and mocked
  `ICurrentUser` principals, but checks real PostgreSQL Identity grant/Owner
  role/Building membership locks, actual legacy Payment and settlement, real
  committed receipt/outbox readback, a separate staging `Posted` transaction,
  durable outbox ACK, duplicate-safe replay and coordinator denial for the
  wrong signed-in actor.
- An independent GitHub Actions job
  `d04b-three-database-posting` creates fresh isolated PostgreSQL 16
  `payos_d04_identity_test`, `payos_d04_legacy_test` and
  `payos_ef_migration_test` databases and attempts the same test. Its
  `BM_PAYOS_*` variables are fixed to loopback port 15432 and
  `disposable-only`; it cannot reach an operating database.
- D04-B Controller now separately validates the exact triple-database names,
  credentials and port before dispatch, in addition to the original Host
  DI guard. Regression tests reject a wrong Building DB or enabled payOS
  merchant mode even if a service was injected.
- **Not yet verified**: no successful compile/TRX on this new source SHA is
  available. GitHub Actions reports failures with missing job-step details
  and unavailable job-log blobs, so the signal is **unusable for code-level
  diagnosis**, not evidence that a specific test failed.
- **Still HOLD**: actual ABP-authenticated two-session HTTP end-to-end UAT,
  protected durable banking-document archival and audit readback, independent
  process-kill recovery, and least-privilege DB security acceptance. No new
  bank statement should be uploaded to this disposable pilot.

**One-command full local bundle (after fast-forwarding this exact branch):**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-payos-d04-local.ps1
```

The runner checks the branch/remote SHA and clean tracked source, compiles
before launching its **own** temporary PG16 container and removes the
container after the run. A green TRX for this HEAD is a prerequisite to
claiming even isolated engineering acceptance; it is not authenticated
HTTP business acceptance.

## Implemented source and fail-closed boundaries

1. Two new independent permission constants: BuildingManagementPermissions.Payments.ReviewBankEvidence and ApproveD04Posting, translated in en/vi. No role assignment, seed, or blanket ManageContract/Building authority.
2. PayOsD04StagingDualAuthority implements actual Identity and Building data revalidation. It locks two different live AbpUsers, exact direct ABP permission grants, current Owner role membership and two effective BuildingManagementOperatorBuildingGrants. The Identity transaction stays open until the SERIALIZABLE Building writer commits. Caller-controlled user IDs or cached JWT permission/role claims never satisfy the required SQL checks alone.
3. PayOsD04ManualEvidenceService: the actual signed and reconciled staging source determines the immutable target; authenticated reviewer submits separate inspected bank statement reference, amount and file. It verifies bytes are PDF/PNG/JPEG and SHA256 hashes the bytes ON THE SERVER; requires independent human attestation, exact Payment/Contract/Room/Building and current deposit balance. Then it inserts immutable BankReview with reviewer ID. An independently signed-in different Owner uses a distinct permission and actual Owner role to insert a 15-minute approval. The approved review is immutable and uniquely bound to its original hash and actor.
4. PayOsD04ReviewedPostingCoordinator: requires current signed-in Owner ID to match immutable approver record; invokes existing legacy financial writer (which re-authorizes at commit), then independently checks committed receipt and ACKs staging. Transient post-commit ACK errors return AckPending, NEVER mint another receipt; unexpected proof corruption remains an exception for investigation.
5. PayOsD04StagingReviewController: authenticated staging-only POST review/{intentId}, approve/{bankReviewId}, post/{intentId}. All hidden from API explorer and default OFF without secure DI, with a document size cap.
6. PayOsD04BStagingOrchestrator: actual ABP IDbContextProvider and nontransactional UoW, not new AbpDbContext during audited SaveChanges. Writer owns SERIALIZABLE transaction.
7. Host DI strictly limited to ASPNETCORE_ENVIRONMENT=Staging, enabled=true, separate activation I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY, PayOsStaging merchant disabled and THREE fixed loopback PostgreSQL databases (user postgres, password disposable-only, port 15432): payos_d04_identity_test, payos_d04_legacy_test, payos_ef_migration_test. A normal server config flag cannot enable these endpoints on the production DB; no production migration is registered.

### Manual evidence caveat

This service records server-computed SHA256 plus a protected external archive reference but does NOT itself archive the uploaded bytes. Until protected durable archival and retention policy are verified, use only synthetic documents in the isolated test. A human reviewer attestation is NOT the same as automatic bank API settlement or proven bank funds. No bank API is needed for no-money test-only D04-B acceptance.

## Added verification gates -- ALL NOT YET RUN at new HEAD

- BuildOnly: compile Domain/Application/EF/Host/test assembly before any Docker/migration.
- WriterOnly: creates only two new databases in the dedicated temporary postgres:16 container, plus preexisting single D04A_REAL01 now extended with:
  - a second ContractDeposit created in the actual canonical Building EF schema;
  - authenticated-in-process reviewer attests a synthetic PDF and a different Owner approves the immutable bank review;
  - reject false attestation, incorrect amount, self-approval and missing approval;
  - actual PayOsD04LegacyPostingWriter with real SQL-backed two-actor authority and source journal/settlement/outbox/idempotent replay;
  - deny wrong building and disabled Owner; a held Identity SHARE lease must block concurrent account disable with PostgreSQL error 55P03.
  - Note: Identity tables in this local test are ABP-shaped synthetic fixtures, NOT native Identity migrations and not proof of two actual ABP-authenticated HTTP sessions.
- Full existing isolated bundle: includes WriterOnly plus all prior stage signed webhook/provider, migration rollback, duplicate and two-DB ACK test gates at same commit.
- Source HTTP controller guard tests ensure no accidental production activation.

## Blocking before full D04-B acceptance

- Real ABP-authenticated HTTP tests with separately logged-in reviewer and Owner against isolated migrated Identity, Building and staging databases; current in-process mocked ICurrentUser is not sufficient.
- Secure, durable statement storage/independent audit readback, or separately approved alternative archive procedure. A client-provided reference is not proof by itself.
- Actual cross-DB process termination/ACK replay integration under two coordinators; current SQL rehearsal is not an actual process kill.
- Least-privilege runtime DB roles; recheck permissions/expiring grants just before commit and independent security review.
- Live payOS and tenant invoice payments remain separate D04-C scope and require explicit owner authorization.

D04-A: ACCEPTED at 1a82efe4 only.
D04-B: SOURCE IMPLEMENTED / NEW BUILD + PG + HTTP SECURITY EVIDENCE PENDING.
D04-C: DEFERRED, no production.
PR #7 stays DRAFT; no merge or migration on operating DB.

## 2026-11-08 — owner-executed isolated PASS at d56e324f (verified)

**Source commit:** `d56e324f8274acec2991193ef4e0f245e29ad5be`.
**Local evidence folder (owner's Windows PC, not uploaded):**
`C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-131934`.

The owner supplied the terminal result of the one-command isolated runner:

```text
PASS: 27-legacy-proof-after-repeated-ack
D04 TWO-DB PROTOCOL REHEARSAL PASS (not financial writer acceptance).
=== PAYOS D04 LOCAL ISOLATED TEST BUNDLE PASS ===
SOURCE_COMMIT=d56e324f8274acec2991193ef4e0f245e29ad5be
LOG_DIRECTORY=C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-131934
No production DB, no live payOS payment, no official D04 posting.
D04 BUSINESS ACCEPTANCE remains HOLD pending real HTTP/financial writer evidence.
```

This **supersedes the pre-run test-status wording above** for that precise source commit: isolated bundle PASS and staged three-DB finance writer exercise were invoked by the runner. It does **not** demonstrate full ABP-authenticated reviewer + distinct Owner sessions over HTTP, secure durable statement archive, a real independently verified bank settlement, forced process-kill replay across two workers, least-privilege runtime roles, or CI success. Those items remain OPEN. In particular the D04-B writer test currently uses synthetic in-process current actors and the rehearsed DB identity fixtures, so no business acceptance or operational money posting is authorized.

**Disposition:** D04-A isolated PASS; D04-B disposable engineering checks PASS / BUSINESS HOLD; D04-C deferred. PR #7 stays draft, no production DB, no live payOS, no merge.

## 2026-11-08 — D04-B authenticated ASP.NET HTTP boundary regression (SOURCE ONLY; NEW HEAD NOT YET TESTED)

The latest branch changes add an in-memory `Microsoft.AspNetCore.TestHost` server, actual ASP.NET cookie authentication middleware with separate reviewer/Owner/unrelated synthetic **test** sessions, and HTTP POST calls to the real `PayOsD04StagingReviewController` actions. It asserts that unauthenticated requests are challenged; reviewer-only bank-review route succeeds; Owner may approve and post; reviewer self-approval, reviewer post and unrelated user post are forbidden; changing Building database to a non-disposable name shuts the endpoint with HTTP 404. An explicit TRX assertion in `verify-payos-d04-local.ps1` requires test `D04B_HTTP01_Distinct_authenticated_sessions_reach_only_their_allowed_routes` to PASS, not merely a green unrelated suite.

**Strict scope:** the server is an isolated test-only cookie issuer, and the handler is backed by purpose-built fake downstream services. It neither logs into real ABP Identity nor creates an actual payment. The preceding `d56e324f` PASS remains valid **only for that earlier source commit**. The new HEAD must be compiled and tested with the same owner-executed isolated one-command suite. A pass of HTTP01 proves only controller routing/auth/session/flag behavior; it CANNOT close the required *native ABP-login + real 3-DB financial writer over HTTP* acceptance gate. A secured durable bank document store, process-kill ACK recovery, least privilege and independent bank verification also remain blocking. PR stays draft.
