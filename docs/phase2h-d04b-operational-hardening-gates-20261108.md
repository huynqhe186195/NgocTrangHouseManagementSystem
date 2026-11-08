# D04-B — Operational hardening after isolated PASS (2026-11-08)

## Baseline and strict safety scope

- The owner-verified full isolated D04 test suite **PASS** belongs to commit
  `66a62dc19d1546fa5fc30c5c4784112f41968df3`, local logs
  `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261108-190449`.
- This is a NEW branch `feat/d04b-operational-gates-20261108` built from
  that exact source SHA, not an alteration to the green PR #8.
- No production DB, payOS real money, public HTTP endpoint or official
  financial posting may be used during development. Three disposable PG16
  databases on `127.0.0.1:15432` only; the local runner deletes only its
  newly created container.
- **This branch has NOT been compiled or run in the assistant environment.**
  It cannot be called PASS until the owner supplies one full guarded local
  run with `SOURCE_COMMIT` and `LOG_DIRECTORY`.

## Engineering changes made on this branch

### A. D04-B Host default-deny on absent durable protected archive

Before this branch, Host's `PayOsD04BStagingOrchestrator` could hand the
canonical review service a **caller-supplied ProtectedArchiveReference**
without using PR #8's protected archive facade, whereas the successful
TestServer experiment injected its own encrypted vault.

The actual Host orchestrator now REQUIRES the
`IPayOsD04ProtectedBankEvidenceArchive` service. It uses
`PayOsD04ArchiveBackedManualEvidenceService` for reviewer attestation and
Owner approval, and `PayOsD04ArchiveVerifiedPostingCoordinator` before
calling the existing real payment writer and ACK. The Host module's
D04-B activation block refuses to start if a separately reviewed protected
archive implementation has not been registered. None is registered by
default; setting `PayOsD04B:Enabled=true` alone still cannot post money.

This closes the production-code wiring gap WITHOUT pretending that the
test-only local AES-GCM vault is a KMS-backed, ACL-protected operational
storage backend. Operational archive key provider, storage IAM/ACL, malware
scanning, encrypted backups, retention/legal hold and orphan-file cleanup
must be provisioned and independently tested before business acceptance.

### B. Real forced OS-process termination and independent ACK recovery

New `test/PayOs.D04.ProcessRecovery.Probe` console executable is compiled
before any disposable Docker container starts by the same
`verify-payos-d04-local.ps1` command. The actual `19a` real financial
writer test persists a committed Payment, DepositSettlement, journal and
outbox while payOS staging remains `ConfirmedPendingPosting`.

It then creates **process A** to read committed journal from PostgreSQL,
prints the exact barrier, and forcibly `Kill(entireProcessTree: true)` only
that child process. A completely different **process B** reconstructs
the real `PayOsD04StagingReceiptAcknowledger` and immutable receipt store
from DB, verifies `Posted/ACK` and same ReceiptId/OutboxId, exits. A third
independent **process C** replays ACK and must report
`RecoveredAlreadyPosted`. The original xUnit host again verifies no
second money allocation, no extra journal and one ACK. The probe cannot
create a Payment or call payOS; all DB names, address, port, credentials,
and env opt-in are hardcoded to disposable test fixtures only.

This proves actual PROCESS death and fresh-process ACK in test. It does
not yet prove sudden death at **every** possible instruction boundary;
operational chaos drills still require a running staging service.

### C. PostgreSQL restricted roles exercised in acceptance

PostgreSQL 16's `SELECT FOR SHARE` needs UPDATE privilege on at least one
column of each locked table (official docs
https://www.postgresql.org/docs/16/ddl-priv.html). Giving the payment
service direct UPDATE over ABP role/permission tables is not safe.

The new `19a` fixture therefore creates three narrowly scoped,
non-superuser roles in its disposable test cluster:

1. `d04_identity_guard_test` in Identity DB: NO direct SELECT/UPDATE
   privilege on ABP Identity tables. It calls three explicitly granted,
   `SECURITY DEFINER` functions with pinned search_path and SELECT FOR
   SHARE inside the actual `AbpUsers`, `AbpPermissionGrants` and
   `AbpUserRoles/AbpRoles` tables. Each function checks exact persisted
   account/role/grant/tenant conditions, returns boolean and keeps locks
   in the caller transaction. The test rejects direct updates with SQLSTATE
   `42501` and executes `AcquireCommitLeaseAsync` using that restricted
   Identity login.
2. `d04_ack_legacy_test` in Building DB: SELECT-only journal/payment/
   settlement/review/approval/outbox evidence plus UPDATE only of the
   `AcknowledgedAtUtc` and `AttemptCount` outbox columns. Cannot
   update Payment or insert journal rows.
3. `d04_ack_staging_test` in payOS staging DB: SELECT intent and reconciled
   inbox, and UPDATE `Status` only on staging intents. Cannot update
   webhook inbox or write new payments. Processes A/B/C use these two
   restricted credentials, **never postgres**.

The newly introduced function-only role is accepted by the SQL authority
ONLY with the exact disposable Identity database name, loopback port,
username and disposable password. The actual Building DB remains
postgres only in this experiment, and production Host remains disabled.

**LIMITATION:** The primary legacy **financial writer** and full payOS
staging runtime checkout still use elevated disposable test credentials.
The new role tests prove narrow **Identity authorization and ACK** rights,
NOT complete production-grade least-privilege for the entire D04 writer.
Do not mark the whole database access gate PASS until writer-specific
roles are also implemented/accepted in a fully isolated host.

### D. Native ABP/OpenIddict login

The actual Host configures OpenIddict validation and bearer forwarding,
but the full isolated D04-B HTTP test still issues **signed ASP.NET test
cookies**. An earlier full ABP module test crashed during startup before
schema/permission initializer setup. No end-to-end native password/token
acquisition and role/lockout/revocation HTTP test has yet passed. The
new branch does NOT claim to have replaced synthetic sessions with a
native OpenIddict login. This must use actual Identity users, native token
issuer, JWT validation on the real Host, distinct reviewer and Owner
accounts, non-authorized caller rejection, role revoke/lockout and
cross-building denial. No live payOS call is needed for this auth gate.

## Single consolidated test command (NO real payment)

On the user's Windows checkout, after this branch is pushed:
```powershell
git fetch origin
git switch feat/d04b-operational-gates-20261108
git pull --ff-only
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-payos-d04-local.ps1
```

The script validates exact branch/source/clean tree, .NET 10, then compiles
the real Host, staging test and process probe BEFORE touching any Docker
database. It reuses archive test 18a, real 3-DB money writer 19a, native
EF Identity test, restricted SQL role/function checks, process-kill/fresh
process B+C ACK and the rest of the existing D04 regression.
`SOURCE_COMMIT` + `LOG_DIRECTORY` are the acceptance evidence.
If the first failing step occurs, do NOT skip checks or run production.

## The two remaining human bank operations

Refer to
`docs/phase2h-d04-real-payos-final-uat-two-manual-actions-20261108.md`.
Only AFTER all necessary native login, storage, least-privilege and process
recovery gates are green and a safe staging integration is authorized:

1. User pays ONCE via a real payOS QR generated by the actual Ngọc Trang
   test contract intent, with an amount exactly equal to the intent.
2. User independently checks the receiving account's official bank
   statement for a posted CREDIT with matching reference, amount and time,
   and then follows the protected two-actor attestation procedure.

payOS has **no separate sandbox**, so this last test touches actual
bank balances. Never send credentials, raw statements or live bank details
to ChatGPT/GitHub. No payment should be made yet.

## Current disposition

- Verified green source: `66a62dc1` **FULL ISOLATED PASS** only.
- This branch: **code committed; NO local PASS evidence yet**.
- Identity role and restricted ACK recovery: acceptance candidate;
  production full database least privilege still HOLD.
- Native ABP login, genuinely durable/KMS-backed evidence archive,
  writer role and real independent bank evidence: **HOLD**.
- Business acceptance: **HOLD**. No merge, no production deployment,
  no live merchant transactions.

## 2026-11-08 20:29 local 19a crash-probe first failure — no child stderr previously captured

Owner ran the single guarded local bundle on `feat/d04b-operational-gates-20261108`. The first failing step was `19a-real-legacy-writer-acceptance`, TRX `payos-20261108-202943\\payos-d04-real-writer.trx`, one failing test `D04A_REAL01_Approved_ContractDeposit_Writes_One_Payment_Allocation_Receipt_And_Outbox`. This failure reached `D04BForcedProcessRecoveryGate.VerifyAsync` line 34: xUnit expected `D04_PROCESS_BARRIER_COMMITTED_UNACKNOWLEDGED` but the child `Process.StandardOutput.ReadLineAsync` returned **null (EOF)**. The child process ended before the barrier; no child standard-error data or exit code was preserved by the old assertion. Earlier archive, compile, and restricted database-role setup gates had already progressed far enough to invoke this new crash probe, but neither subprocess ACK nor full suite PASS can be claimed.

**Root cause is not yet proved.** The pre-barrier subprocess may fail when connecting as `d04_ack_legacy_test` / `d04_ack_staging_test`, when building its independent EF context, or when reading the committed journal; an empty standard-output line does not identify which. Do not assert a particular Npgsql/ABP exception without collecting standard-error and exit code.

**Diagnostic test-harness fixes (not production changes):** commit `7256c2eb42c79fb8b4640b18b911659ba8767d1b` begins draining child standard-error at process start, reports its exit code and stderr if no exact committed barrier, and kills only the explicitly launched child; commit `0f1cf6f2c020d30b78fc0c43898f6edd8b99e32b` adds nonsecret child progress markers `READ_COMMITTED_JOURNAL`, `READ_STAGING_INTENT`, `ACKNOWLEDGE_FROM_FRESH_PROCESS` to stderr while preserving the exact stdout barrier requirement. On a repeat failure, the first `19a` xUnit error MUST now include the actual subprocess error/SQLSTATE or loader exception. This fixes lost diagnostic evidence; **it does not by itself prove the subprocess works**.

Do not touch production, disable the forced-kill assertion, or bypass restricted roles to obtain green. The source `66a62dc1` remains the verified isolated baseline; new PR #9 is draft and **HOLD** until the independently started process proves committed-journal ACK recovery and idempotent replay. One guarded isolated run after `git pull --ff-only` is needed for updated evidence.
