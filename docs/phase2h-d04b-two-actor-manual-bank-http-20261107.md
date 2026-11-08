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

## 2026-11-08 — owner-executed isolated PASS at adab9fcc

**Source commit:** `adab9fcc558c9fad51c349c85530cbd2c43f22fc`.
**Owner's Windows log folder:** `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-140919` (not uploaded to this PR).

Owner reported the terminal acceptance marker:

```text
PASS: 27-legacy-proof-after-repeated-ack
D04 TWO-DB PROTOCOL REHEARSAL PASS (not financial writer acceptance).
=== PAYOS D04 LOCAL ISOLATED TEST BUNDLE PASS ===
SOURCE_COMMIT=adab9fcc558c9fad51c349c85530cbd2c43f22fc
LOG_DIRECTORY=C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-140919
No production DB, no live payOS payment, no official D04 posting.
D04 BUSINESS ACCEPTANCE remains HOLD pending real HTTP/financial writer evidence.
```

This newer PASS supersedes the preceding *new HEAD not yet tested* statement for **that precise commit**: the early `04b-d04-authenticated-http-preflight` and the subsequent required D04B_HTTP01 TRX assertion are inside the script that completed. This is ASP.NET TestServer cookie-authenticated **synthetic downstream** evidence, not a production ABP password/OpenIddict login nor an authenticated HTTP request reaching the actual financial writer on three databases. The independently tested SQL-backed writer and real acknowledgement flow remain in separate tests. Combining the two suites into real multi-actor ABP HTTP/SQL UAT, secure bank archive, process-kill recovery and least-privilege roles remains OPEN.

**Gate decision:** isolated engineering test bundle PASS for adab9fcc; D04-B BUSINESS HOLD, D04-C not activated, no real money, production migration or PR merge.

## 2026-11-08 — D04B_HTTP_SQL01: HTTP -> real three-database financial writer (UNTESTED HEAD)

New `D04BHttpToRealPostgreSqlGateway.cs` is invoked directly by the existing opt-in `D04RealLegacyWriterAcceptance_Tests.D04A_REAL01...` test in **the third, independently funded synthetic ContractDeposit**. The test uses `Microsoft.AspNetCore.TestHost`, cookie authentication middleware with distinct reviewer, Owner, unrelated and anonymous sessions, and the **actual** `PayOsD04StagingReviewController` routes. After cookie identity extraction, the downstream adapter calls real `PayOsD04ManualEvidenceService`, `PayOsD04StagingDualAuthority`, `PayOsD04LegacyPostingWriter`, `PayOsD04CommittedReceiptStore`, `PayOsD04StagingReceiptAcknowledger` and `PayOsD04ReviewedPostingCoordinator`, operating on the three existing disposable PostgreSQL databases.

Acceptance assertions: anonymous write is HTTP 401; unrelated reviewer, Owner acting as reviewer, premature post and reviewer self-approval/post are HTTP 403; a signed-in independent reviewer can submit a synthetic PDF and record an immutable SQL bank review; different Owner approves; Owner HTTP post causes a real canonical Payment completion, deposit settlement, immutable journal/outbox, staging PostgreSQL Posted, legacy outbox ACK; a repeated Owner HTTP post returns the same receipt, with one settlement and no additional money entry.

**Still NOT full D04-B business acceptance:** the log-in issuer is an isolated ASP.NET test cookie endpoint, not native ABP password/OpenIddict login. The Identity database uses previously established **ABP-shaped disposable SQL fixtures**, not the actual ABP Identity EF migration chain. Those distinctions are explicit in source and no production route/DI authentication is enabled. This test also does not verify bank-source funds, encrypted durable archive, process-kill recovery or least-privilege runtime database roles. Those remain HOLD.

**Verification pending:** the new source has not yet been compiled or run. Owner should run `git pull --ff-only` and the existing one-command `verify-payos-d04-local.ps1`; its `19a-real-legacy-writer-acceptance` step now requires this HTTP-to-real-SQL test as well as the preceding flows. On any error retain the first test failure + `LOG_DIRECTORY`. Do not accept a PASS from the prior `adab9fcc` for this new HEAD. PR #7 stays Draft.

## 2026-11-08 — native ABP Identity EF schema pilot (new code untested)

**Owner-verified last green source:** `0118727a77aa7b29ed96670c37828c187147fe95`. Local bundle reported `=== PAYOS D04 LOCAL ISOLATED TEST BUNDLE PASS ===`, with `LOG_DIRECTORY=C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-143435`; no production DB, live payOS or official D04 payment posting. The HTTP -> real three-DB writer/ACK test was included in this passing isolated bundle. No new CI evidence has been obtained.

**New change at `acfe1be2` (NOT TESTED yet):** replace the four handcrafted `AbpUsers`, `AbpRoles`, `AbpUserRoles` and `AbpPermissionGrants` test tables with the exact native `NgocTrangHouseManagementSystemDbContext` EF model in the independently named disposable `payos_d04_identity_test` database. The test module now includes the application's EF module, binds Default to the isolated Identity database and BuildingManagement to the isolated canonical-legacy database, and uses `IdentityUserManager` / `IdentityRoleManager` to create live ABP user aggregates and an actual Owner role membership. Direct financial permission grants are inserted into the real EF-generated `AbpPermissionGrants` table, and the existing SQL row-lease assertions, HTTP-to-ledger test, posting and ACK must continue to pass without weakening checks.

**Scope warning:** this validates ABP Identity **persistence model** and managers, not an OpenIddict/ABP login transaction. The TestServer still uses isolated signed test cookies, not username/password verification by the application's real AuthServer. Further requirements: native authentication acceptance, secure durable independent bank document archive, process-kill replay, least-privilege runtime PostgreSQL role and real bank verification. Keep D04-B business HOLD, PR draft, production blocked.

**Run gate:** previous `0118727a` PASS must not be reused for new source. Use the existing one-command local isolated script once after fast-forwarding; the first relevant newly introduced failure should appear under `19a-real-legacy-writer-acceptance` if native ABP EF/Identity model setup is incompatible. The script's exact 3-database names/ports remain unchanged.

## 2026-11-08 — 19a ABP module initialization failure and isolated EF fix (NEW HEAD UNTESTED)

The owner ran the previously unverified native-Identity-manager candidate at the `2bb8c3e8` source (failure log `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-150030\19a-real-legacy-writer-acceptance.log`). The 19a test **failed BEFORE any D04 posting scenario executed**. Root exception: `AbpInitializationException` while initializing the full `AbpPermissionManagementDomainModule`, its OpenIddict application permission manager and Autofac's missing `AbpAsyncDeterminationInterceptor<UnitOfWorkInterceptor>`. The 19a status is FAIL, not a writer/ledger assertion failure. No real funds or production DB were used.

**Correction at `6ff15b42575e3f7efc7064f584b57996e988f42a`:** restore the proven narrow `[DependsOn(typeof(BuildingManagementEntityFrameworkCoreModule))]` boundary and register the application's real `NgocTrangHouseManagementSystemDbContext` EF model directly with `AddAbpDbContext`. Avoid inadvertently starting the *whole* application OpenIddict/PermissionManagement background initializers in the narrow D04 writer fixture before its disposable Identity database exists. The test still calls `EnsureCreatedAsync` against the exact disposable `payos_d04_identity_test` PostgreSQL database, creates native `IdentityUser`, `IdentityRole` and `PermissionGrant` EF entities with a true Owner role relationship, asserts that five Identity/role/permission rows persisted, and then exercises the existing real SQL identity locks, Building grants, Payment/settlement/outbox writer and signed-test-cookie HTTP/ACK sequence. `IdentityUserManager` and `IdentityRoleManager` are **not** being tested in this narrowly isolated fixture; that needs a separately configured native ABP application host. All operational activation stays disabled.

The above fix has **not** been compiled or executed here; the previous `0118727a` full isolated PASS applies only to that earlier source. Owner must run the same guarded isolated suite and return the first error/stack + `LOG_DIRECTORY` or its success summary. D04-B BUSINESS HOLD remains pending native OpenIddict login, durable independent bank evidence, process-kill replay and least-privilege runtime DB role. PR #7 remains Draft and production is prohibited.

## 2026-11-08 — narrow ABP Identity DbContext DI failure: 19a (new fix unverified)

Owner reran the isolated bundle after the previous OpenIddict module fix. The *new* `19a-real-legacy-writer-acceptance` failure occurred at `PayOsStagingD04RealLegacyWriterAcceptance_Tests.cs:321`, before the native Identity EF data creation or D04-B financial scenario. Full evidence is in the owner's local Windows directory `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-152735`, especially `19a-real-legacy-writer-acceptance.log` and `payos-d04-real-writer.trx` (not uploaded here). Actual first exception:

```text
System.InvalidOperationException: The requested service
'NgocTrangHouseManagementSystem.EntityFrameworkCore.NgocTrangHouseManagementSystemDbContext'
has not been registered.
 at Volo.Abp.Uow.EntityFrameworkCore.UnitOfWorkDbContextProvider<T>.CreateDbContextAsync(...)
 at D04RealLegacyWriterAcceptance_Tests.VerifyD04BIdentityLeaseAsync(...):line 321
```

**Root cause:** `AddAbpDbContext<NgocTrangHouseManagementSystemDbContext>` configures ABP provider and options, but the narrow `D04RealLegacyWriterAcceptanceTestModule` does not include or convention-scan the application's EntityFrameworkCore assembly; therefore, the actual DbContext concrete class was absent from the Autofac container. The earlier whole-app EF module dependency caused OpenIddict permission initialization to fail and is intentionally NOT restored.

**Correction in `9330cc129525fb9d53ba590499b046a110d3fa23`:** explicitly register `context.Services.AddTransient<NgocTrangHouseManagementSystemDbContext>()` immediately after the existing `AddAbpDbContext<...>` registration. Retain ABP-supplied DbContextOptions, UoW, Autofac property injection, exact 3 disposable PostgreSQL connection names and all SQL-based finance permission checks. No financial writer or production runtime code was modified.

**Validation:** source-only change; no .NET SDK or Docker runner is accessible to the assistant. The new HEAD must be verified on the owner's disposable test container using the existing local runner. The earlier owner PASS at `0118727a` proves the old HTTP/real-PostgreSQL path only. A new local PASS is required before claiming native Identity EF model acceptance. Native login, secure bank file archive, process-kill recovery, and least-privilege runtime role are still HOLD.
