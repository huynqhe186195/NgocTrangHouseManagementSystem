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

The **identity-only** negative matrix (reusing the cookie-free script with
a fresh actor token, but without its admin-200 assertion) should demonstrate:
admin may read `/api/identity/users` (200); Owner and reviewer must not
read it (ideally 403, possibly redirect to access denied depending scheme).
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
