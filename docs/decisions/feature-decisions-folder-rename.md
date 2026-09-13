# Decisions Folder Rename PRD

- **Created**: 2026-08-08

> The sibling `feature-decisions-folder-rename.spec.md` holds the technical design.
> Written on the work's branch and merged in the same PR as the work.

> Superseded in part by `2026-09-13-adopt-change-proposal.md` (2026-09-13): the
> goal that the folder name match the process's umbrella term no longer holds.
> The umbrella term is now Change Proposal and the folder keeps the name
> `docs/decisions/`, for the reasons in that proposal's D3. The rename itself
> and the choice of `decisions` over `design` or `specs` stand.

## Summary

`docs/PRDs/` is renamed to `docs/decisions/`, so the folder carries the umbrella
term the process itself coined: decision docs. This reverses the recorded decision
to keep the old name; the supersede notes on the `feature-decision-docs-process.md`
pair point here.

## Problem

The folder holds two document types (PRDs and specs) plus templates and the frozen
archive, but its name names only one of them. The decision-docs process coined the
umbrella term "decision docs" precisely because "PRD" was ambiguous as both the
folder name and one half of the pair, yet the folder kept the ambiguous name. Both
the folder README and root `CLAUDE.md` carried a standing explanation that the name
predates the split: permanent apology text for a known misnomer, read by every new
session.

## Goals

- The folder name matches the umbrella term used by the process, the README, and
  `CLAUDE.md`.
- No live file references the old path.
- Frozen documents are not edited: historical `docs/PRDs/` mentions in merged
  decision docs stay as written.

## Non-Goals

- No change to the decision-docs format or process rules. Only the folder's name
  changes.
- No renaming of document files. Filenames remain the permanent addresses.
- No rewriting of frozen documents. The archive and merged decision docs keep
  their historical path mentions, matching the precedent set by
  `feature-repo-rename.md`, whose merged documents keep the old repository URLs.

## Requirements / Acceptance Criteria

- R1: All decision docs are reachable under `docs/decisions/`, with git history
  preserved as renames.
- R2: `CLAUDE.md`, the folder README, the release command, source, and tests
  reference only the new path.
- R3: The four decision-doc format invariants pass against the new location.

## Product Decisions

**Rename now rather than live with the name.** The keep-the-name decision (recorded
in the `feature-decision-docs-process` pair) weighed the rename against churn in two
files. Re-measured on 2026-08-08, eight live files carried the path, including test
infrastructure (`TestRepo` locates the repo root by this path) and three code
comments added after that decision was recorded, so the cost of renaming only grows
with time. Against that, the misnomer's cost was permanent: two standing apology
notes and a folder whose name misleads every new reader. The deciding observation:
the reference-by-filename convention was designed so that folder moves break no
document-to-document reference, which makes this rename exactly the operation the
process already made cheap.

**`decisions` over `design` or `specs`.** The two candidates from the original
deliberation each name only one document type, recreating the mismatch being fixed.
`decisions` matches the term the process defines ("decision docs", 결정 문서) and
the folder-naming convention common in repositories that keep architecture decision
records.

## Risks

GitHub blob URLs pointing into the old folder path stop resolving on the default
branch, because repository renames redirect but in-repo path renames do not.
Acceptable: this is a solo-maintained repository, the documents are not linked
externally, and permalinks pinned to old commits still resolve.

The flattening change recorded that its path updates were "the last path change
these files can ever have"; this rename makes that sentence stale as prose. Accepted
and made explicit by the supersede notes. The property that sentence protected,
filenames as permanent addresses, is preserved: every file keeps its name.
