# P2.6 — UI / Frontend Inventory and Integration (Discovery Audit)

**Date:** 2026-11-13
**Auditor:** Claude Code (local checkout, branch `feat/p2-6-frontend-discovery`)
**Rule honored:** no framework change, no rewrite, production code untouched. Discovery only.

---

## 1. Verdict (one line)

**There is no frontend in this repository — by design.** The solution was generated with `abp new ... -t app` with **`uiFramework: "no-ui"`** (recorded verbatim in `.abpstudio/state.json → creatingStudioConfiguration.uiFramework`, plus `mobileFramework: "none"`, `publicWebsite: "false"`, `sampleCrudPage: "false"`). What exists is a **headless API host** with a single bootstrap landing page. Everything a Tenant/Staff/Owner portal would consume is already exposed as authenticated JSON API.

## 2. Inventory of everything UI-shaped in the repo

| Item | Location | What it is / is not |
|---|---|---|
| ABP Studio solution config | `.abpstudio/state.json` | Template = `app`, **`uiFramework: "no-ui"`** — the authoritative record of the no-frontend decision |
| Razor landing page | `src/.../HttpApi.Host/Pages/Index.cshtml` (+ `Pages/Index.cshtml.cs`) | The ABP template's app-launcher page: login/logout + language switch + OpenIddict application cards. **Not a portal** — no domain screens, no CRUD, no domain model references |
| Theme package | `src/.../HttpApi.Host/package.json` → `@abp/aspnetcore.mvc.ui.theme.basic ~10.6.1` (+ `resolutions.jquery 3.7.1`) | MVC theming packages to render the single landing page only. Not a SPA stack |
| `wwwroot` | `global-scripts.js` (`/* Your Global Scripts */` — empty), `global-styles.css` (empty), `images/clients/*.svg` (template icons), `dev-login-helper.js` ("intentionally disabled: no embedded credentials") | Template placeholders only |
| `abp.resourcemapping.js` | Host root | Empty aliases/mappings — no client bundling pipeline in use |
| OpenIddict seeding | `src/.../Domain/OpenIddict/OpenIddictDataSeedContributor.cs` | Exactly 2 clients: `NgocTrangHouseManagementSystem_App` ("Console Test / Angular Application" — **ClientId declared but no RootUrl configured in any appsettings**, so no real SPA client is registered) and `NgocTrangHouseManagementSystem_Swagger` (RootUrl `https://localhost:44311/`) |
| Solution projects | `src/` (9 projects: Application, Contracts, DbMigrator, Domain ×2, EFCore, HttpApi, HttpApi.Client, HttpApi.Host) + `modules/buildingmanagement` (8 src projects) | **All server-side.** `HttpApi.Client` is a C# client proxy, not a web frontend. No `angular/`, `react/`, `vue/`, `blazor/`, or any folder with `package.json` beyond the Host's theme one |
| Docs | `docs/` (11 payOS/D04 phase docs), `specs/` (3 spec kits), `etc/phase-audits/` | No UI/UX specs, wireframes or frontend handoff documents |

**Tech stack answer (DoD item):** .NET 10 / ABP 10.6.1 layered backend; the only "UI" is server-rendered Razor (Basic theme) on the API host. **No SPA framework exists to inventory.** Any future portal is greenfield — nothing to migrate, nothing to rewrite.

## 3. API surface a frontend would consume (the real integration map)

### 3.1 Conventional CRUD API (auto-exposed, `/api/building-management/...`)

RootPath `building-management` (Host module line 90–96). 22 AppService classes (auto-exposed conventional routes + the module base class), conventional ABP routes:

- **Buildings / Floors / Rooms / Tenants**: `BuildingAppService`, `FloorAppService`, `RoomAppService`, `TenantAppService` — full DTO + interface sets in Contracts
- **Contracts**: `ContractAppService` (create, tenants, renew), `ContractRenewalHoldAppService`
- **Reservations**: `RoomReservationAppService` (create, cancel)
- **Utilities**: `UtilityMeterAppService`, `UtilityRateAppService`, `MeterReadingAppService` (+ replacement), `UtilityChargePreviewAppService`, `UtilityUsagePreviewAppService`
- **Invoices (draft lane)**: `InvoiceDraftCandidateAppService`, `InvoiceDraftStorageAppService`, `MonthlyInvoiceBasePreviewAppService`
- **Payments**: `PaymentAppService` (contract deposit, reservation deposit, allocation, settlement DTOs)
- **Move-out read models**: `MoveOutRequestAppService`, `MoveOutCompletionReadinessAppService`, `MoveOutFinalMeterReadinessAppService`, `MoveOutFinalMeterSignoffAppService`
- **Financial review/preview read models**: `FinalInvoiceEvidencePackageAppService`, `FinalInvoiceIssuePreflightAppService`, `FinalInvoiceReleaseReadinessAppService`, `FinalInvoiceSourceProvenanceAppService`, `FinalMonthRentPreviewAppService`, `FinalMoveOutBillReviewAppService`, `FinalMoveOutPaymentEvidenceAppService`, `StagedContractDebtReviewAppService`, `StagedIssuedInvoiceDebtReviewAppService`, `ContractDepositLedgerReviewAppService`, `ContractDepositOfficialOffsetReadAppService`, `InvoicePaymentAllocationLedgerReviewAppService`, `FinalInvoiceIssueEvidenceDriftAppService`

### 3.2 Authenticated security lane (custom controllers, `/api/slice-*/...`, 137 HTTP-verb attributes across 10 controllers — some cover multiple verbs per action)

| Controller | Routes | Covers |
|---|---|---|
| `SliceAStagingOperatorGrantsController` | 3 | Owner bootstrap, operator grant/revoke (two-building scoping) |
| `SliceBStagingContractsController` | 44 | Contract lifecycle: create, tenants, activate, transfer, renewal, move-out requests, verify-final-meters, closeout … |
| `SliceBStagingFinalInvoiceController` | 34 | Final bill: prepare → review → decide → issue-candidate → receipt → post-receivable → journal review → deposit decision/destination/bank-review → complete |
| `SliceBStagingRoomChangesController` | 37 | Room transfer lane: H2/H3 mid-month, handover, three-party signoff, D06 signed-scan upload/download, DOCX export |
| `SliceBStagingRoomScopedServiceTariffsController` | 2 | D03 room-scoped service tariffs |
| `SliceCStagingMeterReadingController` | 4 | Meter readings (staging lane) |
| `SliceCStagingUtilityMeterController` | 9 | Meters + replacement |
| `SliceCStagingUtilityRateController` | 5 | Utility rates |
| `PayOsStagingPaymentsController` + `PayOsD04StagingReviewController` | 6 | payOS staging checkout / D04 dual-authority review |

### 3.3 Auth for a frontend

- OpenIddict authorization-code + PKCE (public client type already seeded for the `_App` client id — RootUrl must be configured before a real SPA can redirect)
- 19 permission constants (group "Building Management"): Buildings/Floors/Rooms/Contracts/Utilities Read|Manage + `AssignOperators`/`RevokeOperators`/`BootstrapOwner`/`CoSignOwnerRecovery` — the natural menu/role model for three portals
- Two-building scoping enforced server-side; `X-Slice-A-Test-Actor` header auth exists for the test harness only

## 4. Portal coverage matrix (DoD item)

| Portal | Status | Basis |
|---|---|---|
| **Tenant portal** | ❌ NONE | No UI consumes contract/invoice/payment APIs on behalf of a tenant; tenant-facing flows (view invoice, pay deposit, see meter readings) are API-only |
| **Staff portal** | ❌ NONE | No UI for the operator lanes (meter reading entry, draft invoices, room change, move-out checklist) |
| **Owner portal** | ❌ NONE | No UI for owner lanes (bootstrap, grants, final-invoice issuance, deposit refund bank review, D06 scan download) |
| Swagger | ✅ exists | `NgocTrangHouseManagementSystem_Swagger` client + Swagger UI on the host — the current de-facto "frontend" |

## 5. Missing flows (DoD item — mapped to backend readiness)

All flows below exist as authenticated backend routes (tested E2E through P2.1–P2.5) but have **zero UI**:

1. **Transfer (room change)** — 37 routes in `SliceBStagingRoomChangesController` (mid-month H2/H3, three-party signoff, D06 signed scan upload + Owner DOCX download)
2. **Invoice** — final-invoice lane (34 routes) + draft candidates/previews + issued-invoice correction (P2.1 D01/D02) + service pricing (P2.2 D03)
3. **Meter** — meter/rate/reading CRUD + usage/charge previews + `/verify-final-meters` move-out signoff
4. **Contract** — create/activate/renew/transfer-hold + contract debt review read models
5. **Payment** — deposit payments/allocations/settlements + payOS staging checkout + D04 dual-authority bank review + H4 dispute lane (P2.3)
6. **Proof upload** — D06 signed-handover scan upload (PDF/PNG/JPEG → AES-256-GCM vault), D04 bank-evidence archive, meter-reading evidence — all API-only
7. **Move-out E2E** (P2.5) — checklist, final-meter signoff, disposition, booking retention: full lane exists over HTTP; no screens

## 6. Constraints a future frontend must inherit (from the security architecture)

- **No direct `Contract.End`** — the closeout gate is the only writer (source-gated)
- Immutable evidence rows are append-only at DB level (triggers) — a UI can only append, never edit
- Two-building scoping: every list endpoint is scoped; a portal must pass actor scope, not trust client-side filtering
- Owner-only routes (bootstrap, D06 download, refund bank review) need the Owner role + live revocation checks — a portal must handle 403 revocation mid-session
- payOS real money flow, TLS public callback, production vault stay **HOLD** (Owner decisions pending) — a tenant "pay" button must remain behind the staging simulator until released
- Vietnamese default language (`selectedLanguages: [Vietnamese, English]`) — localization resource exists in the template

## 7. Recommended next step (not executed — Owner decision)

Pick the first portal slice (suggestion: a minimal **Owner/Staff operations SPA** — Angular or React — consuming the existing conventional + slice routes with authorization-code + PKCE, seeded by configuring `NgocTrangHouseManagementSystem_App:RootUrl`). Per the P2.6 rule, this audit does **not** change framework or start any rewrite; the choice of stack for the greenfield frontend is an Owner decision.

---

**Audit evidence:** read-only inspection of the working tree at `43c70dd` (branch `feat/p2-6-frontend-discovery`); no files were modified, no application was started, no database touched.
