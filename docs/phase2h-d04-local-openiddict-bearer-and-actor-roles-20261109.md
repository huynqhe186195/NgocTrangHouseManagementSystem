# Ngọc Trang — local OpenIddict Bearer + two-actor identity acceptance (09/11/2026)

## Evidence on existing localhost runner

Operator confirms new local admin can log in through ABP, and Swagger OAuth
`GET /api/identity/users` returned HTTP 200. Unauthenticated `curl.exe`
(without `-L`) showed both `/api/identity/roles` and
`/api/identity/users` redirect **302 to /Account/Login**; therefore an
older `Invoke-WebRequest` response HTTP 200 was the result of following
a browser login redirect. This is **not** proof that an unauthenticated
user received protected identity data.

The Swagger HTTP 200 is also **not yet proof** of Bearer independence from
ABP cookies. And a successful Identity role test is **not** financial D04-B
business acceptance.

## Token logging security fix before the next token test

In the old Host code, `App:DisablePII=false` caused IdentityModel
`ShowPII=true` and `LogCompleteSecurityArtifact=true`, allowing
sensitive artifacts to appear in development logs. This branch **now
forces both fields false**, regardless of that application flag, and
changes the default setting to `App:DisablePII=true`.

**Restart the host from updated source** before sending a Bearer token into
the localhost test. Do not upload old Host logs; inspect them locally and
treat any already logged access tokens as sensitive.

## Rebuild and restart only the disposable localhost instance

1. Press **Ctrl+C** in the PowerShell window currently running the
   dedicated local-first-boot host. That test database is deliberately
   disposable. Do not stop `ngoctrang-postgres` or containers used by
   unrelated projects.
2. From the repo checkout:
   ```powershell
   git pull --ff-only
   $files = @(
      '.\etc\scripts\start-ngoctrang-local-safe.ps1',
      '.\etc\scripts\verify-openiddict-bearer-local.ps1'
   )
   foreach ($f in $files) {
      $tokens = $null
      $errors = $null
      [System.Management.Automation.Language.Parser]::ParseFile(
          (Resolve-Path $f).Path, [ref]$tokens, [ref]$errors
      ) | Out-Null
      if ($errors.Count) { $errors | ForEach-Object { Write-Host $_.ToString() }; throw "PARSE_FAILED: $f" }
   }
   'POWERSHELL_PARSE_PASS'
   powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\start-ngoctrang-local-safe.ps1
   ```
3. Enter a NEW local administrator password of at least 16 chars, as
   previously. Do not paste any real payOS key. After Host starts, login
   as `admin` in a browser.

## Capture bearer from YOUR browser without exposing it

1. Open `https://localhost:44311/swagger`; click **Authorize**, select
   scope `NgocTrangHouseManagementSystem`, finish native OpenIddict login.
2. Press **F12** (Chrome/Edge), open **Network** and filter **Fetch/XHR**.
3. In Swagger execute **GET /api/identity/users**.
4. Select that API request in Network; under **Request Headers** locate
   `Authorization: Bearer ...`. If absent, DO NOT claim bearer acceptance;
   Swagger may be using only a session cookie. Check OAuth configuration.
5. Copy only the token header value LOCALLY, not "Copy as cURL" (which may
   also include cookie headers). **NEVER send token or a screenshot of
   request headers to ChatGPT, GitHub or another person.**
6. In a SECOND PowerShell window in the repo, execute:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-openiddict-bearer-local.ps1
   ```
   The script uses HttpClient with **UseCookies=false**,
   **AllowAutoRedirect=false** and **UseDefaultCredentials=false**. It
   calls only `GET https://localhost:44311/api/identity/users?maxResultCount=1`.
   Three requests: no auth; invalid Bearer; then the token pasted via
   `Read-Host -AsSecureString` (input is masked). Tokens are NOT
   passed as CLI arguments, saved to disk or printed.
   The script attempts to clear the clipboard after the paste.
7. Only share these non-secret status lines:
   ```text
   NO_COOKIE_NO_TOKEN: HTTP=302 TYPE=...
   INVALID_BEARER: HTTP=302/401 TYPE=...
   ADMIN_BEARER_ONLY: HTTP=200 TYPE=application/json
   AUTH_BEARER_ONLY_PASS
   ```
   Never share the token, JSON response containing users, browser cookies,
   API keys or local passwords.

If the valid request gets 302/401, OAuth Bearer was not accepted. If 403,
the bearer might authenticate but lacks `AbpIdentity.Users`; check role
and permission assignment. If 200 with HTML, this is NOT a pass.

## Two distinct Identity actors — later phase within this same temporary DB

After admin Bearer PASS, create **two separate synthetic test accounts** in
Swagger ABP Identity (no real personal names, emails, bank data):
- `owner-uat`: assigned persistent ABP Identity role named **Owner**
  (exact case, used by `PayOsD04StagingDualAuthority`).
- `reviewer-uat`: separate active user, optionally role
  `BankReviewer`, but never Owner.
- Give neither actor `AbpIdentity.Users` nor administrative roles.
  Use unique local passwords; do not share them in chat.

The **identity-only** negative matrix should demonstrate: admin may
read `/api/identity/users` (200), while a distinct Owner and Reviewer
who lack `AbpIdentity.Users` should be denied (normally 403). A positive
Bearer-only `GET /api/account/my-profile` (200, matching each distinct
username) is required first, or a redirect could be mistaken for denied
permission. The current `verify-openiddict-bearer-local.ps1` intentionally
supports **ADMIN ONLY**; do not run it with a reviewer/Owner token and
claim 403 is a PASS. Implement and inspect a separate per-actor
positive+negative gate before marking the actor matrix PASS.

A denied identity-admin read does **not** itself prove that both actors can
review/post finances.

### D04-specific authorization remains HOLD here

In the D04-B code, review/posting additionally require **direct per-user**
permissions (not just role claims):
- reviewer: `BuildingManagement.Payments.ReviewBankEvidence`
- Owner: `BuildingManagement.Payments.ApproveD04Posting`, plus actual
  `Owner` role membership.
- both: persisted building-specific, non-expired operator grants.
- reviewer ID != approver ID; both accounts active/unlocked; authorization
  rechecked in a SERIALIZABLE transaction during financial posting.

This local-firstboot runner forcibly disables D04-B and payOS. It DOES NOT
register a protected immutable bank document archive and DOES NOT
demonstrate production least-privilege DB writer roles. Therefore never
enable `BuildingManagement:PayOsD04B:Enabled` or
`BuildingManagement:PayOsStaging:Enabled` to make these endpoints
available. No real payment is required for this identity verification.

No automated test on actual Windows PowerShell has been performed by the
assistant for this new read-only script; require syntax parser check before
execution and record the independent result before calling it PASS.

## Windows PowerShell 5.1 clipboard compatibility

During a real Windows PowerShell 5.1 run, anonymous GET returned 302 and
invalid Bearer returned 401. A valid token was successfully entered at
`Read-Host -AsSecureString`, but the script stopped before the valid
Bearer GET because `Set-Clipboard -Value ''` raised an
`ArgumentNullException`. The positive Bearer acceptance was therefore
**NOT EXECUTED / NOT ACCEPTED**.

Code fix `4bfd1d6760edc887429e605f9780a4d904b4d8a1` replaces the
clipboard with a non-empty single space and catches any clipboard command
failure without preventing the subsequent read-only HTTP verification.
The script still does not print or persist tokens.

If any access token is unintentionally disclosed, treat it as compromised:
stop the current local Host and remove its **dedicated disposable** DB
rather than reusing the token. After pulling the fix, launch a fresh
disposable localhost Host, seed a new local administrator password,
complete OpenIddict login again, and use only a NEW OAuth token to test.
Logout by itself does not guarantee immediate revocation. Never copy
access tokens, cookie values, secret keys, or HTTP authorization headers
into chats, screenshots, issue comments, or PRs.

This fix is source-reviewed, NOT yet verified through a subsequent actual
Windows PowerShell positive Bearer execution.

## JWT paste normalization and error classification (2026-11-09)

The user sent a second local-only access token into chat. Treat it as compromised.
Do not reuse it. A new disposable local first-boot Host/database plus a new
Swagger OAuth authorization avoids reusing the compromised test identity.
Logging out of Swagger alone does not guarantee token revocation.

The token was structurally a three-part JWT. No evidence of formatting failure
was presented; the original exact PowerShell error after paste was not supplied.

Commit 5249594f3d1992ad4980037c4baae471e161a801 improves the local script:
- Accepts plain JWT, `Bearer JWT`, and `Authorization: Bearer JWT`.
- Trims copied quote marks, BOM, and whitespace; checks three Base64URL sections.
- Decodes unverified JWT payload locally only to check expiry, local issuer and API audience.
  Only the localhost Host performs cryptographic signature/auth checks.
- Uses nonsecret error codes for expired, malformed, redirected, forbidden or rejected tokens.
- Retains the Windows PowerShell 5.1 clipboard fix and no-cookie/no-redirect GET checks.

Source was fetched after write: exactly one JWT normalizer, one HTTP request
function and one PASS marker. Quote/brace inspection passed, but the actual
Windows PowerShell parser and runtime have **NOT** been executed by the assistant.
Only nonsecret status lines may be shared.

## Verified Windows PowerShell localhost Bearer acceptance — 2026-11-09

Operator-provided output from Windows PowerShell on local disposable Host:

```text
NO_COOKIE_NO_TOKEN: HTTP=302 TYPE=none
INVALID_BEARER: HTTP=401 TYPE=none
TOKEN_LOCAL_FORMAT_PASS
ADMIN_BEARER_ONLY: HTTP=200 TYPE=application/json
AUTH_BEARER_ONLY_PASS
```

**ACCEPTED: native OpenIddict admin Bearer can authorize GET Identity.Users
without a Chrome cookie, without following redirects and with the server
rejecting missing/invalid Bearer.** This acceptance applies only to the
local instance tested, not production or D04 financial posting.

### Next: synthetic Owner/reviewer Identity separation

On the CURRENT disposable local DB, create the following **two synthetic**
ABP Identity users via authenticated Swagger UI/API:

- `owner-uat`: enabled user, assigned **Owner** role (exact spelling).
- `reviewer-uat`: enabled, assigned **BankReviewer** role, never Owner/admin.
- Both roles must have NO ABP Identity Users management permission.
- Both use different new local passwords; do not send passwords or tokens
  to ChatGPT or put them in PowerShell command history.
- Do not modify any production role or existing Identity data.

Find Identity Roles POST and Identity Users POST in Swagger. Use a secure
locally chosen unique password and synthetic local email addresses if ABP
requires email. Do not assign financial permissions, connect a real bank,
or enable PayOS/D04B as part of this test.

Once the two accounts actually exist, authenticate **separately as each**
through native OpenIddict/Swagger. Use new distinct Bearer tokens:

```powershell
$tokens = $null; $errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path .\etc\scripts\verify-openiddict-actors-local.ps1).Path,
    [ref]$tokens, [ref]$errors
) | Out-Null
if ($errors.Count) { throw ('PARSE_FAILED: ' + $errors) }
'PARSE_PASS'
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-openiddict-actors-local.ps1
```

The read-only script prompts twice, with concealed token input. For each
actor it requests GET `/api/account/my-profile` (should be 200 JSON) and
GET `/api/identity/users?maxResultCount=1` (must be 403). It locally
compares distinct JWT subjects/usernames and an Owner role claim for the
Owner and absence of Owner/admin for the reviewer; final output is
`IDENTITY_TWO_ACTOR_PASS`. No response body or token is printed.

**Limitations:** The read-only script has not been runtime-tested on
Windows and should not be marked PASS before operator evidence. Even
after identity matrix PASS, persistent role membership and financial
direct-user grants + building scope + cross-actor D04 posting must be
verified separately. D04-B stays disabled due to unregistered protected
bank-evidence archive. payOS remains OFF.

## Two-actor Identity UAT: executable local-only fixture and role check (2026-11-09)

The isolated first-boot Docker/PostgreSQL must still be **RUNNING**.
Never use the script against production data, a different forwarded localhost
backend or any public URL. The setup script refuses to start unless a **single**
running PostgreSQL container has the test label
`nt.scope=local-first-boot-20261109` and port `127.0.0.1:15433`.

First, pull the latest PR #10 branch and parse BOTH scripts:

```powershell
cd D:\Dev\NgocTrangBuilding\Project\NgocTrangHouseManagementSystem
git pull --ff-only
$files = @('.\etc\scripts\setup-openiddict-actors-local.ps1', '.\etc\scripts\verify-openiddict-actors-local.ps1')
foreach ($f in $files) {
  $t = $null; $e = $null
  [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f).Path, [ref]$t, [ref]$e) | Out-Null
  if ($e.Count) { $e | ForEach-Object { Write-Host $_ }; throw "PARSE_FAILED: $f" }
}
'PARSE_PASS'
```

Then execute the LOCAL fixture setup script while keeping the Host running:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\setup-openiddict-actors-local.ps1
```

Paste your **current local admin Swagger access token** only in the script's
masked prompt. If the script finds the synthetic accounts missing, it asks
for separate **new 16+ character test-only passwords** in masked prompts.
Passwords must contain upper/lower/digit/symbol and are used ONLY in local
POST `/api/identity/users` requests. No passwords or token contents are
printed or saved. Remember the test-only passwords privately for subsequent
interactive native OpenIddict login. Do not send tokens/passwords in chat.

The fixture creates exactly two non-default, non-public ABP Identity roles
(`Owner`, `BankReviewer`) and users (`owner-uat`, `reviewer-uat`) if absent,
using authenticated native Identity API. It queries the actual persisted
user-role membership and refuses an existing conflicting/privileged mapping.
All created emails use the fake `.invalid` TLD. It does not touch financial
permissions, building grants, payment endpoints, the payOS merchant or any
other database. Expected output:

```text
ADMIN_IDENTITY_MANAGEMENT_API_PASS
ROLE_CREATED: Owner
ROLE_CREATED: BankReviewer
OWNER_ROLE_PERSISTED: Owner
REVIEWER_ROLE_PERSISTED: BankReviewer
IDENTITY_UAT_FIXTURES_PASS
```

Now get **two new OpenIddict authorization-code Swagger tokens**, one by
logging in as `owner-uat` and the other as `reviewer-uat`. Use separate
incognito browser sessions or fully sign out of the ABP login session before
switching users, to prevent reusing `admin` cookies or identity. Retrieve
each token privately via browser DevTools as for the admin acceptance.

Run this read-only script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-openiddict-actors-local.ps1
```

The first hidden prompt expects the Owner's access token; the second expects
the Reviewer's. Script sends no cookies or Windows credentials and never
follows redirects. It checks per-user JWT subject and role, GET profile
(200 JSON with matching username), and GET Identity.Users (403). It must
produce `IDENTITY_TWO_ACTOR_PASS` to accept the **Identity-only**
separation gate. If OAuth redirects or users have unintended permissions,
the script fails closed. Actual Windows runtime acceptance is still pending.

Even if both gates PASS, D04 financial dual authorization remains **HOLD**
because direct reviewer/approver permissions, scoped building membership,
protected bank evidence archive and constrained financial DB writer role
are separate concerns. Keep payOS OFF. Do not activate staging flags.

## Host offline while disposable PostgreSQL remains up — non-destructive recovery

Observed on Windows PowerShell 2026-11-10:

```text
docker ps --filter publish=15433
nt-safe-firstboot-7520-10455   127.0.0.1:15433->5432/tcp
docker ps --filter label=nt.scope=local-first-boot-20261109
nt-safe-firstboot-7520-10455   Up 22 minutes
curl https://localhost:44311/api/identity/users -> HTTP=000
```

Root issue: user attempted a *second* first-boot script while original
disposable DB container remained alive and HTTPS Host was down.
Second docker run failed binding 15433. No migration or fresh DB was
performed by the failed attempt. Previously created Owner and BankReviewer
roles may still exist; do not delete the original container.

Recovery script: `etc/scripts/resume-ngoctrang-local-host.ps1`

1. Keep the existing labelled PostgreSQL container running.
2. `git pull --ff-only` on branch `feat/local-safe-startup-20261109`.
3. Run Windows PowerShell `Parser.ParseFile` for both resume and first-boot scripts.
4. In a dedicated PowerShell window, execute:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\resume-ngoctrang-local-host.ps1
```

Safety checks: expected repo branch, exactly one labelled running container,
image `postgres:16`, loopback mapping `127.0.0.1:15433`, known Docker
init DB name, both exact isolated databases available, existing Release
Host DLL, and no other process already listening on TCP 44311.

The resume runner derives the disposable DB password ONLY from the
existing labelled Docker container config, in local memory. It does NOT
print the secret, create/migrate/reset databases, run DbMigrator, remove
containers, or rewrite persisted roles/users. It starts only the compiled
Host with the original two DB connection strings, `Development` and
both payOS feature flags set false. It does not set the admin-bootstrap
password. Keep this host PowerShell window open throughout UAT.

Verify in separate PowerShell: `curl.exe -sS -o NUL -w
"HOST_HTTP=%{http_code}\n" https://localhost:44311/api/identity/users`
should give 302. Then run `setup-openiddict-actors-local.ps1` with a
**new local admin token**, which will reuse Owner/BankReviewer roles if
present and create any remaining synthetic test accounts. Then run the
two-actor GET-only verification script after their separate logins.

The original first-boot script now fails EARLY with
`LOCAL_FIRSTBOOT_ALREADY_EXISTS` or `LOCAL_FIRSTBOOT_PORT_BUSY`
instead of asking for a new password and then failing Docker binding.

Source reviewed after write with basic bracket/quote checks, but the
new host-only runner's actual Windows PowerShell execution is NOT yet
verified. A script success marker alone is not proof of two-actor or
financial D04 acceptance. Keep payOS OFF.

## Windows UAT accepted: independent Owner and BankReviewer Bearer authorization — 2026-11-10

**Evidence provided directly by the local Windows operator** after creating synthetic
`owner-uat` (persisted `Owner`) and `reviewer-uat` (persisted `BankReviewer`)
through the real ABP Identity HTTP APIs:

```text
ADMIN_IDENTITY_MANAGEMENT_API_PASS
ROLE_ALREADY_PRESENT: Owner
ROLE_ALREADY_PRESENT: BankReviewer
OWNER_PASSWORD_POLICY_PASS
OWNER_ROLE_PERSISTED: Owner
OWNER_USER_CREATED: owner-uat
REVIEWER_PASSWORD_POLICY_PASS
REVIEWER_ROLE_PERSISTED: BankReviewer
REVIEWER_USER_CREATED: reviewer-uat
IDENTITY_UAT_FIXTURES_PASS

OWNER_PROFILE HTTP=200 TYPE=application/json
OWNER_USERS HTTP=403 TYPE=none
REVIEWER_PROFILE HTTP=200 TYPE=application/json
REVIEWER_USERS HTTP=403 TYPE=none
IDENTITY_TWO_ACTOR_PASS
```

ACCEPTED for **local Identity authorization only**: both distinct real
OpenIddict-issued Bearer tokens were accepted by `GET /api/account/my-profile`
and returned 200 JSON with matching profile username; neither actor
could access `GET /api/identity/users?maxResultCount=1` (403).
The checker used no cookies, did not follow redirects and asserted
distinct JWT subjects, Owner role for owner, and absence of Owner/admin
for reviewer. The operator did not disclose raw JWTs, passwords or
identity response bodies. No secrets are recorded here.

**NOT ACCEPTED:** any D04 financial permission, direct-user grant,
building-specific operator scope, bank proof archive or bank reconciliation,
least-privilege DB writer role, financial POST/ACK recovery, or production
payment. The D04-B dual-authority code is explicitly gated to separate
`payos_d04_identity_test` and `payos_d04_legacy_test` databases in Staging
with special activation and transactional checks; the current
`nt_firstboot_identity` / `nt_firstboot_building` Development databases
are deliberately incompatible. Do not enable payOS or D04B on this
local first-boot Host to force financial endpoints through the gate.

Next technical work is to use an isolated D04-specific rehearsal harness
for a *distinct native Owner + reviewer* positive and negative authorization
matrix, ensure reviewer and Owner have separate direct-user permissions
and building grants, and retain staging financial operations behind
the protected durable independent bank evidence barrier. Keep PR Draft.

## D04 next isolated financial authorization negative matrix — 2026-11-10

The user has already provided genuine **local first-boot** ABP/OpenIddict
evidence `IDENTITY_UAT_FIXTURES_PASS` and `IDENTITY_TWO_ACTOR_PASS`.
These results are accepted and should not be repeated unnecessarily.

**Different acceptance environment:** D04 financial writer acceptance
uses *new, separate disposable* PostgreSQL 16 databases on loopback
`127.0.0.1:15432` (`payos_d04_identity_test`,
`payos_d04_legacy_test`, and `payos_ef_migration_test`).
It must NEVER use/reconfigure the running firstboot Identity/Building
databases on `127.0.0.1:15433`. The firstboot Native-Identity test
accounts and their JWTs are NOT imported into these D04 databases.

Expanded `PayOsStagingD04RealLegacyWriterAcceptance_Tests.cs`
disposable PostgreSQL permission negative cases:

- Bank reviewer and Owner are the **same GUID**: deny.
- Genuine Owner presented as reviewer, genuine reviewer as approver: deny.
- Owner token/identity cannot acquire review permission lease: deny.
- Bank reviewer cannot acquire Owner approval lease: deny.
- Revoke exactly one reviewer building grant in a SERIALIZABLE
  transaction: deny financial commit; roll back synthetic mutation.
- Wrong building: deny (pre-existing case).
- Missing SERIALIZABLE transaction: deny (pre-existing case).
- Owner disabled in the Identity database: deny (pre-existing case).
- Distinct authorized reviewer+Owner: the existing real synthetic
  ContractDeposit test must still post once with receipt/outbox,
  ACK protection and replay idempotency. No real money is transferred.

Run ONLY the isolated D04 **writer** rehearsal on reviewed current branch:

```powershell
cd D:\Dev\NgocTrangBuilding\Project\NgocTrangHouseManagementSystem
git pull --ff-only
$t = $null; $e = $null
[System.Management.Automation.Language.Parser]::ParseFile(
  (Resolve-Path .\etc\scripts\verify-payos-d04-local.ps1).Path,
  [ref]$t, [ref]$e) | Out-Null
if ($e.Count) { throw ('PARSE_FAILED: ' + $e) }
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-payos-d04-local.ps1 -WriterOnly
```

`-WriterOnly` requires a clean tracked working tree and matching origin
head for the exact allowlisted branch. It compiles the D04 test project
and now also checks the authenticated ASP.NET D04 HTTP route preflight
before creating a **separate** Docker container at 15432, running
protected archive rehearsal and the real PostgreSQL writer test.
That HTTP preflight still uses synthetic session cookies, NOT
the user's already-verified native Owner and Reviewer OpenIddict JWTs.

Expected only **after actual Windows test results**:

```text
TRX VERIFIED: payos-d04-http-preflight.trx passed=1 total=1
PASS: 18a-d04-protected-archive-rehearsal
PASS: 19a-real-legacy-writer-acceptance
=== PAYOS D04 WRITER-ONLY ACCEPTANCE PASS
```

Do not mark any of these PASS ahead of execution. Never transmit
access tokens, DB passwords, personal bank evidence or production data
into log files or chat. The process forcibly removes only its own labelled
temporary D04 Docker container in `finally`. If 15432 is occupied,
investigate before running; do not remove unrelated Docker containers.

Even full isolated synthetic financial writer PASS does **not** close
D04 BUSINESS HOLD. Native authenticated **financial POSTs** with
direct-user grants and scope, live protected independent bank-proof
archive and restricted financial DB login require separate verification
and an explicit staged activation review. payOS remains OFF.

## 2026-11-10 — D04 WriterOnly MSB3021/MSB3027 locked Release DLL remediation

The Windows operator supplied `04a-staging-test-compile` failure from log
`payos-20261110-114042`: MSBuild `MSB3027` / `MSB3021` when trying to
overwrite `src/NgocTrangHouseManagementSystem.HttpApi.Host/bin/Release/net10.0/*.dll`.
The locking process was the running local Host PID `15752`.
These errors occurred before D04 HTTP or PostgreSQL financial tests started.
This is a build output sharing conflict, **not** authorization rejection
or a C# compiler error. Native admin/Owner/Reviewer Identity UAT remains PASS.

**Fix:** `verify-payos-d04-local.ps1` now checks the approved branch, origin
SHA and clean tracked source **before** creating a disposable detached Git
worktree under that run's log directory. All D04 `dotnet build/test` calls
execute from this separate source tree with its OWN `bin`/`obj`, so active
localhost firstboot Host DLL files cannot be overwritten.
The worktree is detached at the exact verified source commit and checked
again before any build, then removed with `git worktree remove --force`
from the original repo in `finally` (does NOT touch any other worktree).
It still creates only the separate labelled disposable D04 PostgreSQL
on `127.0.0.1:15432`, not the Identity firstboot DB on `15433`.

**Retry safely while keeping the existing localhost Host and firstboot DB
running:**

```powershell
cd D:\Dev\NgocTrangBuilding\Project\NgocTrangHouseManagementSystem
git pull --ff-only
$t = $null; $e = $null
[System.Management.Automation.Language.Parser]::ParseFile(
  (Resolve-Path .\etc\scripts\verify-payos-d04-local.ps1).Path,
  [ref]$t, [ref]$e) | Out-Null
if ($e.Count) { $e | ForEach-Object { Write-Host $_ }; throw 'PARSE_FAILED' }
'PARSE_PASS'
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-payos-d04-local.ps1 -WriterOnly
```

Expected early message: `ISOLATED_SOURCE_READY: <verified commit sha>`,
then D04 project build and tests. Do not report a PASS until actual
Windows runtime/TRX results prove it. This run needs no Swagger token,
Owner/reviewer password or real payOS/bank data.

Earlier failed run never reached the D04 money-writer acceptance, so
D04 finance remains HOLD. The running firstboot Host should not need
to stop for this isolated compilation fix.
