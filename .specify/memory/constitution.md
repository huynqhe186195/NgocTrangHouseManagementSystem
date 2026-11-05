# NgocTrangHouseManagementSystem Constitution

**Version:** 1.0.0  
**Ratified:** 2026-11-05 (Asia/Ho_Chi_Minh)  
**Scope:** Brownfield development on the `wip/phase-2g2` lineage. **Development/staging only; never an authorization to deploy production.**

## I. Business requirements and authority

The controlling business source is `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx` (v1.0, 2026-10-22), with explicit owner decisions H1–H6 and OI-01–OI-06 in `etc/phase-audits/Phase2G2H_ApprovedBusinessDecisions_20261101.md`. Use the post-decision implementation scope and requirements-first traceability ledger as cross-checks. Where sources contradict, **record the conflict** and seek a governing decision; never silently invent a business rule. Proposed implementation arithmetic is not owner approval.

A specification must identify its business source, desired behavior, applicable decisions, exclusions, acceptance cases and unresolved detail gates. A historical ledger's dated status is not automatically the present code status.

## II. Preserve the established system

This is an existing ABP layered monolith following DDD boundaries: Domain, Application, EntityFrameworkCore, HttpApi, and separately tested modules. Verify existing code, migrations, tests and current branch SHA **before** creating a new writer, endpoint, schema or ledger. Reuse accepted behavior; do not redo completed phases. Do not change `main`, deployment configuration or production resources during specification/adoption work.

Changes must be sliced by coherent business operation, with traceable spec, plan, tasks, tests and a reviewable PR. A task is not complete merely because code was written.

## III. Deny-by-default authentication and building isolation

Preserve ABP Identity authorization, current account status, permissions, live building membership, lease/lock scope and separation of Owner, Staff, Tenant and root-administrator authority. No cross-building reads/writes, client-supplied trusted verification flags, authorization through a document reference alone, or public exposure of staging-only APIs. Direct `ContractAppService.EndAsync` bypass remains denied. Cross-building and stale/revoked-access attacks require authenticated HTTP tests and, where applicable, transaction-level commit defenses.

## IV. Financial correctness and immutable provenance

Keep issued invoice, receipt, debt, deposit, adjustment, offset, refund and contract history immutable or append-only as dictated by the approved design. An instruction, attestation, string reference, calculated preview or manual receipt claim is **not** bank confirmation or posted money. An amount may have only one authorized disposition; do not double refund, double carry-forward or double charge a day. Require exact integer-VND policy, reconciliation, independent reviews, stable audit IDs, replay/idempotency protection, concurrent request handling and atomic rollback before expanding financial execution.

For Phase 2H, do not conflate transfer-only H3 proration with ordinary Phase 2G.5 full-final-month rent. H1 ends the old contract and creates a linked new one; H5 moves the full verified roster. H2/H3/H4/H6 execution is limited by explicit evidence and business gates, not merely enum support or green unit tests.

## V. Database and execution safety

Use PostgreSQL behavior, migrations, constraints, serializable/locking assumptions and commit-time checks as security-critical boundaries where money, room status and contracts are changed. EF migration scripts and scratch migration rehearsals require explicit review; **do not run migrations against production or unmanaged databases**. Tests that use `BM_PG_TEST_CONNECTION` must obey the repository's allowlisted disposable targets and opt-in flags. Do not alter safety scripts to make a failure look green.

## VI. Evidence-based acceptance

Every acceptance claim must reference the **exact commit SHA** and the test evidence it establishes. Maintain distinction among: specified, implemented, source-reviewed, isolated unit tested, authenticated HTTP tested, disposable PostgreSQL tested, business accepted, production released. Source review and compilation never substitute for authenticated cross-building CRUD or PostgreSQL atomicity tests.

Preserve existing `verify-2g-security-batch.ps1`, `verify-phase-2h-financial-foundations.ps1`, `verify-phase-2h-h4-candidate.ps1` and CI safety gates. A known branch-specific test runner must not be casually run on a different branch. Count skipped/failed tests, verify TRX against expected counts, and retain logs/CI URLs before marking PASS.

## VII. Specification and change governance

Use a living `spec.md` as the desired behavior contract and update `plan.md` and `tasks.md` when verified facts change. Preserve historical approvals/evidence as dated artifacts; do not rewrite them to retroactively imply acceptance. Before implementation, perform an impact and gap audit, surface contradictions and protect already-accepted lanes. After implementation, reconcile spec ↔ tests ↔ code ↔ migration ↔ approval, report residual HOLD conditions, and avoid merging without review.

## Governance

Constitution changes require a reviewed PR explaining business/architecture impact, version increment, affected specs and test gates. More restrictive rules can be added without code change; relaxing finance/security/production restrictions requires explicit owner approval and suitable acceptance evidence. This constitution is an adoption policy and **does not attest implementation status**.

**Initial baseline for adoption:** `a9519996c06d42db880cec4a741036a2e520e460` on `wip/phase-2g2` (2026-11-05).
