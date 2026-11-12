# Phase 2G.3A.8 — Conventional HTTP authorization boundary audit

**Date:** 2026-10-26  
**Branch:** `wip/phase-2g2`  
**Status:** SECURITY BLOCKER / SOURCE AUDIT ONLY — LIVE HTTP VERIFICATION PENDING

## 1. New verified test baseline

User-local output after fast-forward to `8907545`:

- `InvoiceDraftRevisionServerGraphIntegration_Tests`: **GRAPHDB01–08 = 8/8 PASS** (isolated ABP/SQLite EF-backed resource graph).
- `etc/scripts/check-invoice-revision-no-write-gate.ps1`: **PASS**, read-only source check.
- Build succeeded with four existing `RMG012` Mapperly warnings (`TenantDto.CreatorUserId` and `TenantDto.LastModifierUserId`, two mapping sites each).
- `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql` remains a **local untracked file**. It must not be removed, overwritten, applied or implicitly committed.

The 8/8 tests do not exercise actual anonymous HTTP requests, real ABP user grants, live PostgreSQL application roles, or an approved mutating invoice workflow.

## 2. Source finding — cross-module HTTP access risk

1. `NgocTrangHouseManagementSystemHttpApiHostModule.PreConfigureServices` registers conventional controllers from `BuildingManagementApplicationModule` with root path `building-management`. The module's application assembly is also added as an MVC application part.
2. `BuildingManagement.Application/Buildings/BuildingAppService.cs` declares public `CreateAsync`, `GetAsync`, `GetListAsync`, `UpdateAsync` and `DeleteAsync` methods but no class/method `[Authorize]` attribute or explicit permission checks.
3. The base `BuildingManagementAppService` only configures localization and mapping; it does not establish an authorization boundary.
4. The reviewed host module calls `UseAuthentication`/`UseAuthorization`, but contains no `FallbackPolicy`, `RequireAuthorization` or equivalent explicit requirement in that file. Merely enabling authorization middleware does not prove that every endpoint has a policy.
5. `BuildingManagementPermissions` currently declares only draft-revision permissions. It has no reviewed cross-building CRUD permission/scope matrix.
6. `BuildingManagementDbContext` contains Building/Floor/Room/Contract and other business entities but no reviewed operator-to-building membership entity. Invoice revision access explicitly uses a `DenyAllInvoiceDraftRevisionBuildingGrants` implementation.

**Risk:** Unless another pipeline-level policy is proven, conventionally exposed business application methods may be reachable without an authorization requirement. Global sign-in (even if confirmed) would still NOT enforce cross-building data isolation. This is a **source-level risk requiring real HTTP integration verification**, not a claim that an exploitable production endpoint has already been demonstrated.

Scope to enumerate: Buildings, Floors, Rooms, Tenants, Contracts, RoomReservations, Payments, UtilityMeters, MeterReadings, UtilityRates, MoveOutRequests and all other conventional module services. Consider both read and mutation routes, not only invoices.

## 3. Required remediation design

1. Build a complete list of effective HTTP routes and endpoint authorization metadata from a started **isolated test host**, including methods inherited from application services. Prove whether any global or endpoint convention applies at runtime.
2. Require authentication for business API endpoints; if a central fallback policy is selected, explicitly review exceptions for authentication, public health, OAuth/OpenIddict, Swagger and any intentionally anonymous routes. A global switch without such review can break identity flows.
3. Define operation-level ABP permissions (read/manage and sensitive financial mutation independently) and test deny-by-default. Do not infer `RebuildDraft` from any broad admin or building view permission.
4. Introduce a separately reviewed, server-owned operator↔building authorization source bound to authenticated ABP user ID and **active** building membership. Define grant/revocation history, who may assign rights, and isolation across organizations/tenants. Do not trust client building IDs as proof of access.
5. Query/scope resources server-side. For a direct GUID lookup, resolve the full relationship to its building; for lists, filter by authorized buildings BEFORE paging/counting; prevent IDOR and cross-building enumeration.
6. For each mutating command, re-evaluate current grant and permission inside the transaction under an agreed lock/consistency protocol; cached UI preflight is not an authorization decision.
7. Maintain a separate minimum-privilege PostgreSQL runtime role. A trusted DB connection does not replace user/building authorization.
8. Inventory which application services are internal-only (`[RemoteService(IsEnabled = false)]`) before introducing any centralized auth or routing change. Do not expose draft revision or invoice issue/post endpoints as part of this audit.

## 4. Mandatory next executable tests — NOT YET RUN

| Test | Expected evidence |
| --- | --- |
| APIAUTH01 | Enumerated effective `building-management` HTTP route/verb/action inventory, including authorization metadata |
| APIAUTH02 | Anonymous GET of protected Building/Room/Contract/Payment route returns 401 (or authenticated access challenge), never business data |
| APIAUTH03 | Anonymous POST/PUT/DELETE to representative real business routes cannot persist mutations |
| APIAUTH04 | Authenticated user without action permission is refused even for an existing known resource ID |
| APIAUTH05 | User with action permission but no building grant is refused on own/other building IDs |
| APIAUTH06 | Cross-building collection, count and paging return no unauthorized room/tenant/payment information |
| APIAUTH07 | Revoked operator loses access despite a previously successful preview/UI state |
| APIAUTH08 | Identity login, OpenIddict discovery/token routes, and explicitly public health endpoints still behave as intended after policy changes |
| APIAUTH09 | Invoice draft remote APIs, draft rebuild, invoice issue and debt posting remain inaccessible |

The first safe engineering increment is a **test-host route/policy inventory** without altering production middleware, data models, permission grants or schema. Do not write an optimistic 401/403 test that only mocks `[Authorize]` or an inaccessible class method; assert against the actual generated endpoint collection and a real anonymous request.

## 5. Status and safety constraints

- Phase 2G.3A.7 server-graph check: **8/8 PASS**.
- Phase 2G.3A.7 source NO-WRITE check: **PASS**.
- **APIAUTH01–09 pending**: cross-module protection has not been proven.
- **SEC01–04, REV05–07, DEP01, OPS01 pending** per the preceding operator/DB role audit.
- Do not run `dotnet ef database update`, DbMigrator, grant/revoke against application DB, or `psql` against populated database as part of this audit.
- Keep `InvoiceDraftStorageAppService` remote-disabled, `CurrentRevision` absent and all invoice issue/post/rebuild writes blocked.

**Release gate:** No production rollout of mutation-capable business endpoints until effective endpoint authorization and server-side building scope checks are implemented and the listed negative tests pass.

## 6. Staged authentication-only host guard — NOT YET BUILD-VERIFIED

The branch now contains an incremental defense-in-depth change:

- `src/NgocTrangHouseManagementSystem.HttpApi.Host/Security/BuildingManagementAuthenticatedControllerConvention.cs` applies `AuthorizeFilter` with `RequireAuthenticatedUser()` to MVC controller models originating from either `BuildingManagementApplicationModule` (generated app-service controllers) or `BuildingManagementHttpApiModule` (explicit controllers).
- The convention deliberately throws at MVC-model discovery if an application controller/action explicitly includes `IAllowAnonymous`, requiring a separate review of any exception.
- `NgocTrangHouseManagementSystemHttpApiHostModule.ConfigureServices` registers the convention through `MvcOptions.Conventions`.
- An isolated model-test project at `test/NgocTrangHouseManagementSystem.HttpApi.Host.Tests` contains **AUTHMODEL01–06**: target-controller filter; unrelated-controller isolation; class/action anonymous bypass refusal; invoice-draft remote disabled attribute; and explicit HttpApi controller protection.

**These new tests are UNVERIFIED** until run locally, and MVC model tests are not equivalent to inspecting the deployed effective endpoint set or making a real HTTP request. In particular, the implementation assumes ABP's generated controller models preserve their original application-service assembly; validate that assumption through `APIAUTH01` route/authorization-metadata inspection against an isolated host. If any `building-management` route evades the convention, stop and repair before deployment.

Recommended verification from the repository root:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj --filter "FullyQualifiedName~BuildingManagementAuthenticatedControllerConvention_Tests" --logger "console;verbosity=normal"
dotnet build .\src\NgocTrangHouseManagementSystem.HttpApi.Host\NgocTrangHouseManagementSystem.HttpApi.Host.csproj --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
```

Use an isolated test host for the subsequent actual-endpoint inventory; do **not** casually start the regular host with credentials for production data. No migration, DbMigrator, role GRANT/REVOKE, live PostgreSQL mutation or invoice write accompanies this stage.

**Critical:** Authentication is only the outermost boundary. An authenticated user still must not be allowed to read or mutate another building's resources; no operator↔building grant model/DB migration and no per-operation permissions have been implemented. APIAUTH04–09 remain pending. The application cannot be declared secure for multi-building production use based on this convention alone.

## 7. Explicit-controller gap identified and included in staged guard

Source review found `BuildingManagement.HttpApi/Samples/ExampleController.cs` with `GET /api/building-management/example`: its `GetAsync` has no explicit `[Authorize]` even though another method in the same class does. This is a concrete route-level metadata omission in source, not evidence of an executed anonymous HTTP request. The initial convention restricted to the application assembly could not cover this explicitly authored controller. The updated convention now also targets `BuildingManagementHttpApiModule`'s assembly, and `AUTHMODEL06` is added.

**Still pending:** actual route/verb/policy inventory and live anonymous 401 verification in an isolated host. Authenticated cross-building read/write remains blocked from release until per-resource grants are implemented.

## 8. Local verification and in-memory HTTP increment — 2026-10-26

User-local evidence received after the initial `AUTHMODEL01–06` commit:

- `BuildingManagementAuthenticatedControllerConvention_Tests`: **6/6 PASS**.
- Independent host project build: **succeeded**, output showed no warnings.
- `check-invoice-revision-no-write-gate.ps1`: **PASS**.
- The preceding `dotnet test` command reported **9 warnings** in its build, but did not include individual warning diagnostics; do not misattribute them to the four older Mapperly warnings without reviewing output.

New staged tests, **PENDING local execution**:

- `BuildingManagementExplicitHttpAuthorization_Tests.APIHTTP01`: starts an **in-memory ASP.NET Core TestServer** with the real `BuildingManagement.HttpApi` assembly, uses the same `BuildingManagementAuthenticatedControllerConvention`, checks discovered controller action descriptors and verifies they contain an authenticated-user authorization filter.
- `BuildingManagementExplicitHttpAuthorization_Tests.APIHTTP02`: calls the exact `GET /api/building-management/example` route anonymously through the isolated HTTP pipeline and requires HTTP 401. The test authentication handler refuses all identities; it does not create users or access application credentials.
- Adds `Microsoft.AspNetCore.TestHost` 10.0.9 to the new host test project. The real ABP host is **not** booted; the test supplies only controllers, routing, authentication, authorization and the convention. It cannot touch PostgreSQL.

Run from repository root on `wip/phase-2g2`:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj --filter "FullyQualifiedName~BuildingManagementExplicitHttpAuthorization_Tests" --logger "console;verbosity=normal"
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

**Scope caveat:** APIHTTP01–02 validate the explicitly authored HttpApi controller assembly in a disposable test host. They do **not** prove that ABP's conventionally generated controllers from application services receive the same authorization filter in the **real configured production host**, that authorization is complete for all routes, or that an authenticated actor is restricted to their buildings. Full-host route/verb/policy inventory (`APIAUTH01`) and permissions/ownership isolation remain pending.

Never run against production data or enable draft revision/issue/debt mutations as part of this verification.

## 9. APIHTTP verification received; ABP auto-route discovery staged — 2026-10-26

User-local evidence on commit `865ce84`:

- **APIHTTP01–02 = 2/2 PASS**. `APIHTTP01` discovered and checked the explicitly authored `ExampleController` route policy; `APIHTTP02` received HTTP **401** for `GET /api/building-management/example` in the disposable, in-memory TestServer.
- `git status --short` continues to show **only** the preserved, untracked `Phase2G3A7_RevisionLedger_Generated_Review.sql`.
- Build reported **12 warnings**: two pre-existing `CS8604` in OpenIddict data seeding, four `RMG012` Tenant Mapperly warnings, two `CS8602` in Swagger response filter, one `CS0162` in host module, and three introduced by the newly written HTTP test (`ASPDEPR004`, `ASPDEPR008`, `xUnit2031`).

The new test-only warnings were corrected by replacing deprecated `WebHostBuilder`/`TestServer(IWebHostBuilder)` with the documented `HostBuilder`/`UseTestServer` in-memory pattern, and by using `Assert.Single(collection, predicate)`. **Not yet rebuilt or verified** after this change; do not state the warnings are gone until the next local build.

### ABP-generated controller coverage: APIAUTO01–02 — NOT YET RUN

Staged an additional `BuildingManagementAutoApiAuthorization_Tests` class in the existing isolated host test project. It boots a **minimal ABP MVC application on in-memory TestServer** using the actual module `BuildingManagementApplicationModule`, `BuildingManagementHttpApiModule`, official ABP conventional-controller configuration (`RootPath = "building-management"`) and the production authentication MVC convention:

- **APIAUTO01**: asserts ABP discovers `BuildingAppService` methods as generated controllers, every generated module action carries authenticated-only policy, no `IAllowAnonymous` metadata, and `InvoiceDraftStorageAppService` remains undiscoverable due to its remote-disabled status.
- **APIAUTO02**: resolves the generated `BuildingAppService.GetListAsync` action's actual route and sends an unauthenticated GET through the in-memory pipeline, expecting HTTP 401. The anonymous authentication probe never grants a principal, and no business services are executed.
- No `BuildingManagement.EntityFrameworkCore` dependency, database connection, data seeding, DbMigrator, production configuration, user/role change, or invoice mutation is required to run these tests. They intentionally do not start the real production host.

Run from the repository root, only on `wip/phase-2g2`:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj --filter "FullyQualifiedName~BuildingManagementAutoApiAuthorization_Tests|FullyQualifiedName~BuildingManagementExplicitHttpAuthorization_Tests|FullyQualifiedName~BuildingManagementAuthenticatedControllerConvention_Tests" --logger "console;verbosity=normal"
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

**Stop if APIAUTO fails**: capture compiler error/exception; do not start the real HTTP application, run DbMigrator or change connection strings in an attempt to make this test pass. This is an experimental isolated ABP test harness and its compatibility with this solution's ABP 10.6.1 must be confirmed locally.

**Release posture unchanged:** full-host configured API route/verb/policy inventory and HTTP 401/403 coverage remain pending. Authorization by operator/building and explicit permissions, concurrent revoke rechecks, and PostgreSQL least-privilege runtime credentials are NOT implemented or passed. Keep production blocked.
