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
