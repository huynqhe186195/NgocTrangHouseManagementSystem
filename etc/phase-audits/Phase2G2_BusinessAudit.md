# Phase 2G.2 — Business loophole audit (WIP)

Audited against *Business Process Specification — Phase 2G & 2H*, v1.0,
22/10/2026, section 4 (Meter & Pricing), and the branch
`wip/phase-2g2`. This is a risk register, not a release approval.

## Implemented / covered by tests

- Replacing a physical meter is an atomic operation with an immutable
  old-final/new-initial snapshot; historical readings cannot be silently
  changed after handover. Covered by RM01-RM14 and PGUTIL03-PGUTIL04.
- Unique active meter and rate-period overlap protection on PostgreSQL.
  Covered by PGUTIL00-PGUTIL02.
- Monthly recorded usage is a sum of posted monthly readings plus old-meter
  closing segments. Covered by domain usage calculator tests and UP01-UP06.
- Monetary preview refuses to allocate a single month's consumption between
  different in-month tariffs. No auto-issued invoices.
- **Audit repair, 23/10/2026:** add `MonthlyUsageCoverageAssessor`.
  Preview exposes `CoverageIssues` and
  `IsSafeForProvisionalPricing`. Money preview no longer invents a zero
  charge or quotes multi-month consumption as one month's charge when the
  prior-period meter reading is missing. Reading-date/billing-month mismatch
  is reported for review pending an agreed cutoff policy. PR06/PR08-PR10
  validate the new guards; the user reported 59/59 passing on 23/10/2026.
- **Audit repair, additional historical gap:** a meter may be inactive now
  but have been the responsible physical meter during the month under review.
  Do not treat `IsActive == false` as proof it needed no monthly reading in
  the past. Use full replacement history to determine whether its outgoing
  replacement happened before the billing month. If the meter may have been
  in use but has neither a monthly reading nor a closing snapshot, add
  `InactiveMeterPeriodUnverified` and suppress monetary estimates.
  Regression tests PR11-PR12 passed in the user's 61/61 run on 23/10/2026.

## Open / release-blocking business policy decisions

1. **Final meter reading and handover** — the document requires a final
   counter reading for Final Bill. A new READ-ONLY preflight now links the
   approved MoveOutRequest to physical meters and monthly reading evidence
   on the approved move-out date, handles later physical replacements, and
   checks usage coverage. It has a distinct
   `HasReadingEvidenceForBothUtilities` flag and an explicit
   `IsFinalBillApproved == false` guard. Domain cases and integration
   checks ME01-ME04 have been added but still require a fresh test run.
   A persisted handover sign-off, operator/tenant evidence, checklist
   completion workflow, and final invoice authorization remain unimplemented.
2. **Billing cutoff** — agreement needed on the billing month/date boundary,
   late meter reads, missed months, and incomplete/zero readings. The
   conservative coverage warning blocks provisional quotes in ambiguous
   cases; no automated reallocation of historical consumption is attempted.
3. **Tariff structure** — flat rate vs tiers, whether tariff changes within
   a month require readings at each boundary, how prorated services are
   handled, and rounding/currency treatment are not finalized in the spec.
   The current provisional price uses only one flat unit rate covering the
   whole month and must not be treated as an authorized invoice amount.
4. **Issued invoice immutability** — Phase 2G.3 must store a durable snapshot
   of meter quantities and selected rates, lock historical issued lines, and
   route corrections through audited adjustment records. The mutable
   UtilityRate table is NOT a historical invoice snapshot.
5. **Contract/tenant attribution** — today's previews are keyed by RoomId
   and month. Phase 2G.3 must attribute costs to a specific contract and
   deal explicitly with tenant change / room transfer / move-out mid-period.
   It must not charge a new tenant for the predecessor's consumption.
6. **Exceptional meter history** — manual deactivation without a replacement
   closing snapshot, broken chain, imported readings and meter rollover
   require explicit treatment before invoice issuance.

## Gate to Phase 2G.3

The Phase 2G.2 data and read-only previews (including the final-meter
*evidence preflight*) can be used as an **input** to draft invoice modeling. Do not allow issuance, payment allocation,
debt recognition or final move-out settlement until the open policies and
invoice snapshot/approval guards are resolved.

Never run migrations against the production database as part of this audit.
