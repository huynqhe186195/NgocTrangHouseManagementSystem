# Ngoc Trang — specifications

This directory tracks **bounded changes on the existing system**, not a rewrite of the entire application. Each feature has a living `spec.md`, technical `plan.md`, sequenced `tasks.md` and an explicit quality checklist.

## Rules

- Prefix feature folders with a stable number and descriptive slug.
- Keep business requirements in the spec and code locations/design choices in the plan.
- Use exact commit SHA and evidence links for each verified assertion; never copy a previous run's PASS onto a new revision.
- Reconcile feature status against implementation and approved decisions, not only dated status texts.
- Preserve HOLD or UNKNOWN for facts awaiting evidence; checkboxes are not proofs.
- Make one coherent PR per implemented business slice. Specification adoption is a separate PR from financial code changes.

First bounded scope: [001-phase-2h-h4-deposit-acceptance](./001-phase-2h-h4-deposit-acceptance/spec.md).
