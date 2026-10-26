# Phase 2G.3A.8 — Mapped-route follow-up, 2026-10-26

## Verified baseline
User-local host tests: **10/10 PASS** (AUTHMODEL01–06, APIHTTP01–02, APIAUTO01–02), build reported **9 warnings**. Source NO-WRITE gate **PASS**. Untracked generated review SQL file stays untouched.

## Staged, NOT yet locally run
- APIAUTO03: inspect mapped EndpointDataSource for conventional and explicit BuildingManagement routes; require authenticated filters, forbid anonymous bypass.
- APIAUTO04: anonymous POST to conventional BuildingAppService.CreateAsync -> HTTP 401.
- APIAUTO05: anonymous GET for Room/Contract/Payment GetAsync with synthetic GUID -> HTTP 401.

All new checks use a minimal **in-memory ABP TestServer** without production host, real database, or business writes. Target host-suite count **13/13**, pending local verification.

## Verify from repo root
```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj --logger "console;verbosity=normal"
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

## Outstanding security blockers
Authentication-only filters do **not** grant or restrict per-building rights to an authenticated operator. Dedicated operation permissions, server-side operator-to-building membership, collection filtering before paging, revocation and multi-tenant isolation, runtime PostgreSQL role tests, and actual configured-host route inventory remain pending. No invoice rebuild/issue/debt endpoints may be enabled.
