# Agent-neutral Spec Kit adoption (Brownfield)

This repository is **being adopted into the Spec Kit workflow**, not regenerated from a greenfield specification.

## Current bootstrap status

- The reviewed `constitution.md` and feature specifications under `specs/` have been authored through ChatGPT's GitHub connection.
- **The official `specify` CLI has not been executed in this repository.** Consequently, upstream-generated templates, scripts, integration metadata and command dispatch are **not yet installed**. Do not claim this is a complete CLI installation.
- The project deliberately does not require Claude Code, GitHub Copilot or any other proprietary coding agent. GitHub Spec Kit supports the **`none`** integration for agent-neutral shared scaffolding.

## Optional one-time official CLI initialization (local Windows checkout)

Review and commit/stash your local edits first; use the dedicated adoption branch. In PowerShell at repository root:

```powershell
uv tool install specify-cli
specify version
git switch chore/adopt-spec-kit-20261105
git status --short
specify init --here --force --integration none --script ps --non-interactive --ignore-agent-tools
git status --short
git diff -- .specify
```

`--force` can replace conflicting managed infrastructure files, so review the diff and avoid overwriting reviewed custom content. GitHub's current guidance says existing `.specify/memory/constitution.md` is preserved, but always confirm before committing.

Official adoption reference: https://github.github.com/spec-kit/how-to/existing-projects.html

Official agent-neutral integration: https://github.github.com/spec-kit/integrations/none.html

## ChatGPT workflow without a local coding agent

1. Read this constitution, the controlling source documents and the requested feature's `spec.md`.
2. Verify the current branch HEAD, existing implementation and actual exact-SHA CI/test evidence.
3. Clarify uncertain **business rules**; do not confuse an approval of direction with approval of numeric details.
4. Update `spec.md` (what/why), `plan.md` (how, actual code locations and impact), then `tasks.md` (testable, sequential work).
5. Before coding, reconcile the spec against the current implementation and record what is already done.
6. Make code changes only in a separately scoped implementation branch/PR, run the appropriate isolated gates and document exact SHA evidence.
7. Mark tasks PASS only after matching code and tests; retain HOLD for anything unverified or undecided.

The `none` integration has no agent commands; its lack of `/speckit-*` invocation is expected. Running Spec Kit CLI is distinct from applying the spec-driven process manually through ChatGPT.

## Key existing authoritative records

- `etc/phase-audits/Phase2G2H_ApprovedBusinessDecisions_20261101.md`
- `etc/phase-audits/Phase2G2H_PostDecisionImplementationScope_20261101.md`
- `etc/phase-audits/Phase2G2H_RequirementsFirstTraceability.md`
- `etc/phase-audits/Phase2H_LocalStagingAcceptance_20261103.md`
- `etc/phase-audits/SpecKit_Brownfield_Adoption_20261105.md`

Do **not** update `main` or production from this bootstrap.
