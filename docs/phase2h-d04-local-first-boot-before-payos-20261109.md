# Ngọc Trang — Safe Windows localhost first boot BEFORE payOS (09/11/2026)

## Current boundaries

This branch is independent from PR #9 engineering PASS at
`705a4812141da4c3a98985addaea7af9a5e0de5c`.
**No Docker or .NET execution result exists yet for this new branch.**

This guide is for starting the **local backend**, NOT for accepting live payOS
webhooks, activating D04-B, submitting banking documents or posting money.

- Keep `BuildingManagement:PayOsStaging:Enabled=false` and
  `BuildingManagement:PayOsD04B:Enabled=false`.
- NEVER paste real `Client ID`, `API Key` or `Checksum Key` into ChatGPT,
  Git, PowerShell command history, or `appsettings*.json`.
- DO NOT run the root `docker-compose.yml` or
  `etc/scripts/initialize-solution.ps1` for this first boot. The former binds
  port 5432 and a persistent volume, and the latter starts DbMigrator with
  whatever connection strings happen to be configured.
- The dedicated runner
  `etc/scripts/start-ngoctrang-local-safe.ps1` creates a **fresh,
  throwaway** PostgreSQL 16 container with no mounted volume on
  `127.0.0.1:15433`, with two uniquely named DBs:
  `nt_firstboot_identity` and `nt_firstboot_building`.
  Existing `ngoctrang-postgres` on port 5432 and prior payOS D04 tests on
  15432 are not accessed or stopped.
- The throwaway test container is deleted when the runner ends. Data from this
  first boot are intentionally NOT durable and not for real tenants.

## Important security correction

The previous source tree included a **hard-coded bootstrap administrator
password** in application constants and a JS browser login helper. Those
credentials were exposed in Git history and must not be reused. The new branch
removes the hard-coded password and browser-side autofill, and requires
`NgocTrang:LocalBootstrap:AdminPassword` (minimum 16 characters) from a
secret source **BEFORE ANY schema migration**. The runner prompts securely
and sets only a process-local environment variable; no password is written
to the repository. Rotate any account that ever used that old credential.
Do not republish its value.

The disposable local PostgreSQL still uses a generated test-only superuser
for bootstrap/migrations. **This is NOT production runtime least privilege.**

## Do this in Windows PowerShell

1. Open Docker Desktop and wait until it shows its engine running.
2. Open **PowerShell** and enter your project checkout folder (adjust only
   the path if you cloned elsewhere):
   ```powershell
   cd D:\Dev\NgocTrangBuilding\Project\NgocTrangHouseManagementSystem
   git status --short
   git fetch origin
   git switch feat/local-safe-startup-20261109
   git pull --ff-only
   ```
   If the switch is blocked by locally edited files, STOP rather than
   discarding or resetting any changes.
3. Check local prerequisites:
   ```powershell
   dotnet --version
   docker info --format '{{.ServerVersion}}'
   dotnet dev-certs https --trust
   ```
   Approve the Windows certificate trust prompt only for the standard
   development HTTPS certificate. Do not generate or deploy a production
   OpenIddict signing key here.
4. Start the new isolated first-boot workflow:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\start-ngoctrang-local-safe.ps1
   ```
   It compiles Host/DbMigrator **BEFORE** Docker, then prompts for a NEW
   **LOCAL administrator password** of at least 16 chars. Use a unique
   password with upper/lowercase letters, a number and symbol. Save it
   privately in a password manager. This is **NOT a payOS key** and
   should never be pasted into any chat.
5. The script starts a new PG16 on loopback 15433, migrates **only** those
   new DBs, then launches backend at `https://localhost:44311`.
   Wait for `Now listening on: https://localhost:44311`.
6. In another browser tab, open
   `https://localhost:44311/swagger` or
   `https://localhost:44311/health-status`. Browser access proves
   only local server health, not that financial posting is allowed.
7. To finish, press **Ctrl+C** in the original PowerShell window. The
   script removes only its self-created, labeled container. The local
   test data disappear. This does NOT touch the existing Docker container.

## If first boot fails

- On step 1/5 (build): return only compiler error lines with no secrets.
- On step 2/5 (password): choose a strong **new local password**; do not use
  the previous password exposed in source.
- On step 3/5 (Docker): verify Docker Desktop is running and that
  port `15433` is unused; do not move to port 5432.
- On step 4/5 (migrations): STOP. The existing repository has staged and
  evolving BuildingManagement EF migrations; do not run migration or
  `EnsureCreated` against any existing tenant database as a workaround.
  Return the first SQL/EF migration error, with credentials redacted.
- On step 5/5 (Host): return the first ASP.NET startup exception,
  with database connection strings/passwords removed.
- Do not turn either payOS feature flag ON to make endpoints appear.
  The D04-B Host guard explicitly requires a separately provisioned
  protected bank-statement archive backend.

## What comes next, but NOT now

1. Native OpenIddict login/permissions of two distinct operator roles.
2. Managed protected document vault and storage/backup/retention controls.
3. Least-privilege database roles for the full financial writer.
4. Protected HTTPS staging deployment and confirmed payOS webhook (payOS has
   no separate sandbox, so no premature live payment/API calls).
5. After all gates PASS: user only performs **one** real QR payment and one
   **independent receiving-bank statement confirmation**.

The local first-boot procedure neither authorizes production usage nor
substitutes for those outstanding D04-B operational/business gates.
