# Phase 2H — local/staging foundation acceptance and remaining execution gates

**Date:** 2026-11-03 (Asia/Ho_Chi_Minh)  
**Tested code baseline:** `d047734a120a3efcf256f1a94e973854d84d8d1b`  
**Branch:** `wip/phase-2g2`  
**Acceptance:** H2/H3/H4/H6 **FOUNDATION STAGING VERIFIED**; full mid-month transactional Room Change **HOLD**.  
**Production:** no deployment or activation. Database and financial writer changes excluded.

## User-reported local verification evidence

The operator ran `etc/scripts/verify-phase-2h-financial-foundations.ps1` at the exact code baseline above. The provided final output was:

```text
=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===
COMMIT=d047734a120a3efcf256f1a94e973854d84d8d1b
Full mid-month contract/cash writer remains HOLD; production not used.
```

The script has five hard-stop gates:
1. Exactly **32/32** focused Domain tests: 24 rent/deposit foundations and 8 server-verified H6 handover scenarios.
2. Exactly **17/17** PostgreSQL room-change request/roster/atomic transfer tests.
3. Full migration rehearsal on a throwaway local PostgreSQL test database.
4. Authenticated staging HTTP/PostgreSQL regression on independently named disposable databases.
5. Full Domain/Application/EF/HTTP regression requiring its exact quality-gate banner.

This is **user-provided local test evidence**, not an independently retrieved GitHub Actions run URL. Logs remain local in `$env:USERPROFILE\NgocTrang-Test-Logs\15-...` through `19-...`. Do not assert that CI has passed this SHA without an actual corresponding run.

## Cross-layer closeout guard review

- Existing `Phase2HWallClockCompletionValidation` SQL commit function `BM_VerifyRoomChangeCommit` already recalculates the transfer-completion canonical SHA-256 over the archived typed fields, so a duplicate hash-only migration is unnecessary.
- The same SQL function still restricts the executable completion to the legacy safe policy lane: `PricingTreatment=1` (full old month/new following month) and `DepositTreatment=1` (full verified carry with no difference). The Domain `RoomChangeCompletionRecord` and the authenticated `SliceBStagingRoomChangeRequests.CompleteRoomChangeAsync` independently enforce that lane.
- H6's `RoomChangeReducedHandoverChecklistGate` runs only **after** original E/W meter verification, immutable all-tenant roster matching, official receivable settlement and cash-backed held-deposit checks, and before either contract/room state is mutated. Its `ServerVerified*` flags are constructed by trusted server code, not accepted from HTTP input.
- A signed string reference is **not** a cryptographically verified signature or proof of a successful bank transfer.

## Remaining specific HOLD items (not new owner approval of H1–H6)

| Gate | What is still required before widening the writer |
|---|---|
| D01 | Signed rule identifying the charged room on handover date and exact source/target effective cutoff. |
| D02 | Approved VND rounding per rent line, residual policy and month-length/leap-day examples. |
| D03 | Independently reviewed append-only correction of already-issued rent/service debt and receipt provenance; prevent negative or duplicate receivables. |
| D04 | Verified deposit top-up receipt or independently reviewed partial refund disbursement with approved beneficiary, immutable journal, idempotency/concurrency/rollback. |
| D06/H6 | Approved signatory matrix and signed physical asset/defect/room-handover checklist; no fake document/reference acceptance. |
| Final execution | Authenticated non-safe-lane HTTP writer, PostgreSQL deferred commit checks for both rooms/contracts and cash disposition, race/rollback/security test evidence. |

No further Phase 2H financial mutation, default proration policy, production migration or tenant-facing access should be inferred from the five PASS groups. Phase 2G.4 and 2G.5 accepted code baselines remain unchanged by this documentation.


## Additive PostgreSQL hardening — 2026-11-03 (new commit, test PENDING)

Source review found a SQL-bypass gap in the 2H month-end completion:
`BM_VerifyRoomChangeCommit` validates the completion SHA, source Room
state and money, but did not itself validate whether a source `Available`
disposition contradicts an existing next-tenant `Reserved` booking or
`Signed` future contract. The authenticated writer originally checked
only reserved booking facts and could miss a signed future source contract.

The new `20261103050000_Phase2HSourceRoomDispositionCommitGuard` migration
adds a *separate, deferred, SERIALIZABLE* constraint trigger to the existing
completion table. It checks the actual source room state and current
reservation/contract facts at constraint/commit time. A sole confirmed
future commitment requires `Reserved`; no commitment permits `Available`
or `Maintenance`; PendingPayment is HOLD; multiple confirmed commitments
or another active source contract fail closed. `Maintenance` must never
silently supersede a confirmed next occupant. All existing 2H money/roster
checksum and 2G.5 proof triggers are untouched.

`SliceBStagingRoomChangeRequests.CompleteRoomChangeAsync` now derives
confirmed future source occupancy from **both** reserved reservations and
signed future contracts, rejecting multiple/active/awaiting-payment source
facts. The server-only H6 gate takes this combined commitment fact; its
signed checklist semantics remain unchanged.

Seven direct SQL PostgreSQL cases `TRFPG18–24` cover Available/Reserved,
signed next contract, PendingPayment, multiple next occupants, second active
tenancy, unsafe downgrade, and late competing reservation before deferred
commit. They use isolated transaction-rolled-back schemas. The full fresh
migration chain requires the new latest migration and checks the deferrable
trigger, and the one-command 2H verifier now expects **24/24** Phase 2H
PostgreSQL cases in addition to the previously passing 32 Domain goldens,
migration rehearsal, authenticated HTTP and Full regression.

**Evidence boundary:** the previous 5/5 PASS is only for
`d047734a120a3efcf256f1a94e973854d84d8d1b`. New code/migration
must pass at its **new** commit SHA before staging verification. The
mid-month pricing/receipt/refund writer remains HOLD, no default day policy
is chosen, no production migration or banking action is authorized.


## 2026-11-03 — source-disposition SQL hardening verified at exact SHA

**Operator-supplied Windows PowerShell result:**

```text
=== PHASE 2H FINANCIAL FOUNDATIONS: ALL FIVE GROUPS PASSED ===
COMMIT=40d691389e78098cd58a12e6c0011b2641a05682
Full mid-month contract/cash writer remains HOLD; production not used.
```

This supersedes the **test PENDING** statement above **only for the
exact code SHA `40d691389e78098cd58a12e6c0011b2641a05682`**.
The verification script hard-stops unless all five groups succeed:
32/32 focused H2/H3/H4/H6 Domain cases, 24/24 2H PostgreSQL cases
(including the new TRFPG18–24 source commitment and commit-gate probes),
fresh disposable PostgreSQL full migration, authenticated staging HTTP
with isolated PostgreSQL databases, and Full .NET regression.

**Acceptance scope:** Phase 2H *local/staging technical* PASS for the
existing **month-end, full-carry-forward** transfer and the
H2/H3/H4/H6 read-only/supporting foundations. **NOT** Phase 2H full
BUSINESS ACCEPTED or production-ready; mid-month execution with
invoice corrections, independently posted top-up/partial refunds,
handover signatory detail and policy D01–D04/D06 remains HOLD.
The result is operator-reported local evidence, not an independent
GitHub Actions execution record. An exact-SHA GitHub Actions run was
not found through commit workflow-run/status lookup. Do not claim CI
passed based on local test output.

No code change is required to record this evidence. The existing
untracked `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`
remains outside the commit and must not be altered by this status
update.


## 2026-11-03 — additive H6 signed-physical-handover checksum guard (test pending)

During review, the existing `Phase2HPhysicalHandoverEvidence`
migration was found to check only the *format* of
`RoomChangeHandoverEvidence.EvidenceSha256`, while its original-reading
trigger correctly scoped physical electricity/water meters. An attacker
writing directly to the SQL table could persist a mismatched but
well-formed 64-hex H6 payload without crossing the Domain factory.

The additive `20261103060000_Phase2HPhysicalHandoverDigestIntegrity`
migration fixes only that gap: PostgreSQL recomputes the **exact Domain
canonical digest** over request/contract/room/building IDs, handover
business date, original E/W IDs and three-decimal readings, inspection
and handover references, actor and normalized UTC instant before
an insert is accepted. Historical rows must pass the same checksum
before migration; corrupted history makes the migration fail closed
rather than rewriting signed artifacts. Down fails if *any* handover
history exists. It does not change the original source-meter guards,
the financial/cash guards or authorization.

Six new transaction-isolated PostgreSQL tests `TRFPG25–30` cover
real Domain canonical SHA parity (including fractional readings and
microsecond UTC), direct-SQL forgery of hash/reference/reading,
corrupted pre-existing history migration denial, nonserializable write
denial, clean reversible schema and valid pre-existing record upgrades.
The full fresh EF migration-chain test now expects the new migration
as the latest and checks its PostgreSQL trigger. The single 2H
verification script now requires **32/32 Domain, 30/30 PostgreSQL 2H
cases, scratch full migration, authenticated HTTP/PostgreSQL and full
regression** at the exact new code SHA.

**Status:** Code committed; **runtime PostgreSQL evidence on this
new SHA pending**. The operator's prior five-group PASS at
`40d691389e78098cd58a12e6c0011b2641a05682`
is historical only. Full mid-month transfer with invoice
corrections, actual top-up/refund and the remaining D01–D04/D06
business detail gates remains HOLD. A consistent SHA does not
cryptographically prove a human signed the referenced document;
independent signature/identity verification is a separate gate.
Production is not deployed.
