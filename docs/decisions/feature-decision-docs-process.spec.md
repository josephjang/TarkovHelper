# Decision Docs Process — Split Product Decisions from Technical Design — Technical Spec

- **Created**: 2026-07-29

> Sibling PRD: `feature-decision-docs-process.md` — it holds the problem, the
> requirements, and the product decisions; its evidence is not restated here.
> State counts describe the repository as of `origin/main`, before this change.

> Superseded in part by `feature-decisions-folder-rename.spec.md` (2026-08-08):
> the Technical Decisions entry "The folder keeps the name `docs/PRDs/`" is
> reversed; the folder is now `docs/decisions/`. Every other decision stands.

> Superseded in part by `2026-09-13-adopt-change-proposal.md` (2026-09-13): the
> two templates in Design section 1 are replaced by the three change-proposal
> templates under `templates/`; the trigger rules in Design sections 2 and 3 by
> the behavior-change trigger; and the `DecisionDocsTests` guard (Design section
> 5) is extended to the dated file names, the fixed section layouts, and the
> two-way Split pairing. The four original invariants stand.

## Summary

Four groups of changes plus two review-added guards, no application code: two new
templates replace the old one (no kept-current field, no terminal section);
`docs/PRDs/README.md` is rewritten to match;
the trigger rules go into root `CLAUDE.md`; and a one-time flattening — everything
in `active/` moves up to `docs/PRDs/`, false `Status` lines are corrected, and the
legacy `archive/` tree is frozen in place. The deep review of this PR added
`TarkovHelper.Tests/PrdDocsTests.cs` (the four format invariants, checked offline)
and `.github/PULL_REQUEST_TEMPLATE.md` (a checkbox linking to the trigger rules).

## Current Behavior

`docs/PRDs/` holds one template, one README, six live documents (eleven files
including Korean twins), and 34 archived documents (35 files — one has a Korean
twin).

- `templates/feature-template.md` has six sections a diligent author must revisit
  mid-flight: `Overview` (`Status`, `Updated`), `Implementation Plan`
  (phase checkboxes), `Progress Log`, `Completion Criteria`, and the `Goals` and
  `Dependencies` checkbox lists. It also asks for `**Related Agents**` and requires
  an agent "Learning Log" update as a completion criterion.
- The README's `## Agent Integration` table names five agents; none exist — all
  were deleted from `TarkovHelper/.claude/agents/` in `b2fdac8` (2025-12-19), and
  two further README lines depend on the same missing machinery.
- The README claims the `.prd` → `.md` conversion was applied wholesale;
  `TarkovHelper/Quests_DB_Migration.prd` and `TarkovHelper/TabVerification.prd` are
  still tracked.
- No frontmatter, hooks, markdown linter, link checker, or docs CI step exists at
  any level; `.github/workflows/` contains only `ci.yml` and `release.yml`.

## Design

### 1. Templates: two new, one deleted

The blocks below are the templates **as reviewed in this pair's own PR**; once
they land, the authoritative copies are the files under `docs/PRDs/templates/`. Two conventions
apply to both:

- **Reference other documents by filename, never by folder path**
  (`feature-hideout-localized-sort.md`, not `docs/PRDs/active/…`). Name-only
  references survived the folder consolidation (`3a1936b`) and the bulk extension
  rename (`9cb13a4`) untouched; every path-bearing reference broke. With no folder
  moves in the new model, a filename is also a permanent address.
- **Anchor code citations to a symbol**, line number optional:
  `UserDataDbService.InitializeAsync` (`UserDataDbService.cs:1057`). A bare line
  number is invalidated by the next insertion above it — often by the very change
  the document describes.

**New — `docs/PRDs/templates/prd-template.md`**

```markdown
# <Feature Name> — PRD

- **Created**: YYYY-MM-DD

> The sibling `<name>.spec.md`, if it exists, holds the technical design. Write
> this on the work's branch and merge it in the same PR as the work. Nothing is
> kept current: fields are written once, discoveries are appended. A later change
> that reverses a decision here appends `Superseded by <doc>` below this line, in
> the PR that reverses it.

## Summary
The decision in a few sentences, readable on its own. Detail and evidence live in
the sections below, never here.

## Problem
What a user experiences today and why it is a problem. For user-facing work, keep
code and file names out of it.

## Goals
What must become true for a user. Prose or a short list — not checkboxes.

## Non-Goals
Explicitly out of scope, so a later reader knows it was considered and declined.

## Requirements / Acceptance Criteria
R1..Rn — each one observable by a user without reading code.

## Product Decisions
One short section per decision: the decision in the first sentence, then what else
was considered and why this one won. Keep the evidence to a sentence or two. Prose,
not a table — a rationale that fits in a table cell is usually too thin to be worth
recording. Where a rejected alternative had real merit, give it its own paragraph.
Append decisions discovered during implementation as they happen.

## Risks
User-facing risk, and what makes it acceptable. If the shipped result has a
user-facing limitation, record it here before the final PR merges (technical
limitations belong in the sibling spec's Risks & Migration).
```

**New — `docs/PRDs/templates/spec-template.md`**

```markdown
# <Feature Name> — Technical Spec

- **Created**: YYYY-MM-DD

> The sibling `<name>.md`, if it exists, holds the product decision; a spec with no
> PRD is the normal shape for an internal change. Write this on the work's branch
> and merge it in the same PR as the work. Nothing is kept current: fields are
> written once, discoveries are appended. A later change that reverses a decision
> here appends `Superseded by <doc>` below this line, in the PR that reverses it.
> If this spec records a decision whose implementation is deliberately deferred,
> say so explicitly — a merged spec is otherwise read as shipped.

## Summary
The design in a few sentences: what changes, and the one or two ideas it rests on.

## Goals
What must become true technically. Omit if a sibling PRD already states it.

## Non-Goals
Technical scope explicitly declined — adjacent problems you chose not to solve here.

## Current Behavior / Root Cause
How it works today, anchored to symbols. For a fix this is the *confirmed* root
cause — read the code first, never a guess.

## Design
Which services, files, and data flows change. Prose plus a file list. No phase
checkboxes: implementation order belongs in the session's plan file.

## Technical Decisions
One short section per decision: the decision in the first sentence, then what else
was considered and why this one won. Keep the evidence to a sentence or two; prose,
not a table. Append decisions reached during implementation as they happen — that
is history, and history does not go stale — including where the implementation
diverged from the design above.

## Open Questions
Anything genuinely unresolved, with what would settle it. Delete the section if empty.

## Test Strategy
- **Unit**: the invariant each guard test locks down
- **E2E**: which user-visible path
- If something cannot be tested automatically, say so here and why.

## Verification
Commands to run, including any manual check, and the observable result that proves
it works.

## Risks & Migration
Data migration, ordering, compatibility, rollback. Technical limitations of the
shipped result go here before the final PR merges (user-facing limitations belong
in the sibling PRD's Risks).
```

**Deleted — `docs/PRDs/templates/feature-template.md`**, fully replaced by the two
above.

### 2. `docs/PRDs/README.md` rewrite

Every current section has a disposition:

| README section | Disposition |
|---|---|
| `# PRDs` intro | **Rewrite** — define the umbrella term **decision docs** and its two types (PRD = product decisions, spec = technical design), and state that the folder name predates the split and names only one of the two (kept; see Technical Decisions) |
| `## PRD vs 참고 문서` | **Rewrite** — retitle to `결정 문서 vs 참고 문서`: a decision doc (PRD or spec) records decisions about work; a reference document describes how the system already works |
| `## Folder Structure` | **Rewrite** — documents live flat in `docs/PRDs/`; `templates/` holds the two templates; `archive/` is frozen legacy history, and nothing moves in or out of it |
| `### 1. 새 기능 계획` | **Rewrite** — the trigger rules, the two template names, and the same-PR rule: the document merges with the work it describes |
| `### 2. 작업 진행` | **Remove** — replaced by one line: append discoveries as they happen, and name the documents a PR implements in its body |
| `### 3. 완료 및 아카이빙` | **Remove** — nothing happens at completion; the merged PR *is* the completion. Replaced by the supersede rule: a change reversing a recorded decision appends `Superseded by <doc>` in that same PR |
| `### 4. 정체(Stale) PRD 처리` | **Remove** — a document on `main` is born final; work in flight is an open PR and abandoned work is a closed one. There is nothing left to go stale |
| `## 이중 언어(EN/KO) PRD` | **Rewrite** — new documents are English-only; existing twins stay paired. Keep both surviving rules: the English original wins a conflict, and code identifiers are not translated |
| `## PRD Status` table | **Remove** — no status field survives |
| `## Agent Integration` table | **Remove** — all five agents were deleted in `b2fdac8` |
| `## Commands` | **Remove** — there is no archiving procedure left to script |
| `## Best Practices` 1 (1–2주 크기) | Keep — unaffected |
| `## Best Practices` 2 (완료 기준 명시) | Keep, reworded to point at `Requirements / Acceptance Criteria` |
| `## Best Practices` 3–5 | **Remove** — the Progress Log and the agents are gone, and there is no periodic tidying left to do |

**Added** to the README: the two document types and the `name.md` / `name.spec.md`
pairing; the trigger rules; the born-final model and the same-PR rule; the rule
that a PR body names the documents it implements; the supersede rule; the anti-rot
principle with its reason (five of six documents were wrong); the
reference-by-filename convention; and the note that legacy documents
keep their original format, including their `Archive Info` sections. The README
stays Korean, and the sections added to it are written in Korean.

**Trigger rules, as they will read:**

- A hard-to-reverse **product decision** → PRD (`name.md`)
- A non-obvious **technical decision** → spec (`name.spec.md`)
- Both → both files, sharing one name
- Neither (obvious bug fix, mechanical refactor) → no document; the PR body is enough
- Adding the sibling file mid-flight is expected, not a failure — a technical
  decision often only becomes visible after investigation. Just add it.

### 3. `CLAUDE.md`

Two edits. The `## Documentation & PRDs` section is retitled `## Documentation &
Decision Docs` and describes the model: PRDs hold product decisions, `*.spec.md`
holds technical design; documents live flat in `docs/PRDs/` (a folder whose name
predates the split), merge in the same PR as their work, and are never moved or
revised afterwards — only appended to.

More importantly, **the trigger rules go here, not only in the README**. The
session that shipped the top-bar redesign did not fail to follow
`docs/PRDs/README.md` — it never opened it. Root `CLAUDE.md` is loaded into every
session; putting the four-line rule there is what makes R5 achievable.

### 4. One-time flattening

Thirteen files end up flat in `docs/PRDs/`, and `active/` is removed: eleven
pre-existing files move up one level as git renames (the five shipped document
sets with their Korean twins, plus `fix-userdata-init-deadlock.md`), and this
change's own two documents are born at the flat location — against `main` they
are additions, not moves. Because nothing moves again afterwards, this is the
last path change these files can ever have.

**Corrections made while flattening**, in both the English original and its Korean
twin:

1. **Correct the false `Status` lines** in the four documents that lie
   (`fix-quest-name-localization`, `feature-hideout-localized-sort`,
   `feature-quest-unlock-sort`, `feature-persist-map-view-state`) to what actually
   happened, with their PR links. No other header field changes — legacy documents
   keep their format, including any `Archive Info` heading they already have.
2. **Record real limitations** where they exist, as short appended notes: the
   hideout sort shipped locale-aware alphabetical ordering only (the in-game list
   groups modules by build state first); the quest sort deliberately diverges from
   the in-game reverse-unlock-time order (per-profile, not readable by the app);
   `feature-fork-release-process`'s manual smoke-check of the released zip has no
   recorded result — logged as unverified.
3. **Add the deferred-implementation note** to `fix-userdata-init-deadlock.md`:
   investigation record, implementation deliberately deferred. Verified against the
   code, not commit messages — lazy re-init via
   `InitializeAsync().GetAwaiter().GetResult()` is still at five sites
   (`UserDataDbService.cs:1057, 1076, 1102, 1223, 1243`), and `Program.Main`
   reaches `new App()` with no eager initialization. Its stale `Status: Planning`
   line is corrected to say the same (investigation complete, fix deferred) — the
   deep review caught the header contradicting the note it sits above.

**Path references updated for the last time:**

| Location | Points at | Update to |
|---|---|---|
| `CLAUDE.md` `## Releases` (line 154 pre-change) | `docs/PRDs/active/feature-fork-release-process.md` | `docs/PRDs/feature-fork-release-process.md` |
| `.claude/commands/release.md:8` | same | same new path |
| `feature-fork-release-process.md:103` | `docs/PRDs/active/feature-hideout-localized-sort.md` | `docs/PRDs/feature-hideout-localized-sort.md` |
| `feature-fork-release-process.ko.md:102` | same | same new path |

Ten source and test comments mention documents **by name only** (e.g.
`QuestUnlockOrderTests.cs:8`, `WindowBoundsPersistence.cs:8`), and five more
reference a PRD generically without naming a file; none carries a path, none
breaks, and the ten name-bearing ones are the working precedent for the
reference-by-filename convention above.

### 5. Review-added guards

Two files added during the deep review of this PR (decisions recorded under
Technical Decisions):

- `TarkovHelper.Tests/PrdDocsTests.cs` — one `[Fact]` per format invariant from
  the PRD's Risks, on the `UpdateXmlTests.cs` repo-root-walk harness.
- `.github/PULL_REQUEST_TEMPLATE.md` — a decision-docs checkbox linking to the
  trigger rules in root `CLAUDE.md`.

## Technical Decisions

**The folder keeps the name `docs/PRDs/`.** Renaming to `docs/design/` or
`docs/specs/` would be more accurate but churns `CLAUDE.md` and
`.claude/commands/release.md`. Re-examined when the process was renamed *decision
docs* — the folder now names only one of the two types it holds — and still kept:
the churn is unchanged, and the README's umbrella definition, not the folder name,
carries the terminology.

**The legacy `archive/` tree is frozen in place.** Moving its 34 documents out — or
sorting the five newly finished documents in — was rejected: filing documents by
finished-ness re-encodes in folders the state distinction this design removes, and
frozen paths are stable paths. `archive/` remains as history's address, nothing
more.

**On the flattened legacy documents, correct the `Status` line and change nothing
else.** Correcting a factual error is not a retrofit; leaving it produces documents
that contradict themselves, as `archive/2025-12/refactoring-large-files.md` already
does.

**The new format has no terminal section.** Earlier drafts kept an `Outcome`
section to be written at archiving time; born-final removes the moment it would
have been written, and an empty stub waiting for that moment is the exact
anti-pattern the four flattened shipped documents demonstrate with their empty
`Archive Info` stubs (every document in `archive/` has its section filled in).
Anything worth saying at the end of a change — limitations, divergence
from the design — is ordinary content, appended before the final PR merges.

**Separate commits for the format change and the bookkeeping.** The format change
is reviewable on its own; the flattening is its mechanical consequence. Matches the
repository's `docs(prd):` pattern.

**The `PrdDocsTests` guard ships with this change after all.** The first draft
declined it as an explicit non-goal and parked it as an open question; the deep
review of this PR judged the recorded cost understated — every surviving obligation
was discipline-guarded while the four invariants are mechanically checkable — and
reversed the decision deliberately. Invariant 1 distinguishes new-format from
legacy documents by a closed allowlist of the eleven flattened files (the set can
never grow — `active/` is gone), and invariant 4 exempts this spec, which records
the removed paths by design (mirroring verification checks 4–5 below).

**A PR-template checkbox links to the trigger rules instead of copying them.**
`.github/PULL_REQUEST_TEMPLATE.md` (also review-added) puts one checkbox at the PR
gate — the one boundary every change passes regardless of authoring tool — and
points at root `CLAUDE.md` rather than restating the rules, so they live in one
place and cannot drift.

## Test Strategy

- **Unit**: `TarkovHelper.Tests/PrdDocsTests.cs` locks down the four format
  invariants listed in the PRD's Risks (no kept-current field in new-format
  documents, spec↔PRD pairing, `.ko.md` pairing, path resolution) — added during
  the PR's deep review, reversing the earlier non-goal.
- **E2E**: none — this change touches no application code.
- No docs lint target exists to run (no `.markdownlint*`, no docs step in either
  workflow).

The other structural guard is the design itself: the new templates contain no field
that has to be kept current and no section that waits for a later moment. Reviewing
the two templates for either is the single most valuable review action on this
change.

## Verification

Run from the repository root **after the change is applied**; the expectations
describe the post-change tree. `git grep` rather than `rg`: ripgrep is not on
`PATH` in a stock PowerShell session on this machine, and it skips
dot-directories, so it cannot see `.claude/commands/release.md` — one of the four
references this change updates.

```powershell
# 1. active/ is gone; documents live flat in docs/PRDs/
Test-Path docs/PRDs/active
#    expected: False
Get-ChildItem docs/PRDs/*.md
#    expected: the README plus thirteen documents (eleven flattened + this
#    change's own pair)

# 2. The README and templates reference no deleted agent (legacy documents keep
#    their history and are deliberately out of scope)
git grep -n -E "prd-manager|map-feature-specialist|db-schema-analyzer|wpf-xaml-specialist|service-architect|Learning Log" -- docs/PRDs/README.md docs/PRDs/templates/
#    expected: no output

# 3. Neither template carries a kept-current field or a terminal section
git grep -n -E "\*\*(Status|Updated|Owner|Related Agents|PR)\*\*|## Progress Log|## Outcome" -- docs/PRDs/templates/
#    expected: no output

# 4. No reference to the removed active/ path survives anywhere
git grep -n "docs/PRDs/active/" -- . ":!docs/PRDs/feature-decision-docs-process.spec.md"
#    expected: no output (this spec is excluded: it necessarily records the old
#    paths in its own path-reference table and in this very command)

# 5. Every docs/PRDs path written in a tracked file resolves
git grep -h -o -E "docs/PRDs/[A-Za-z0-9_/.-]+\.md" -- . ":!docs/PRDs/feature-decision-docs-process.spec.md" | Sort-Object -Unique | Where-Object { -not (Test-Path $_) }
#    expected: no output (this spec is excluded for the same reason as check 4 —
#    it records the removed paths and the deleted template by design)

# 6. The moves registered as renames, not delete+add — run after committing.
#    This inspects the flattening commit itself; in the PR view against main,
#    the eleven pre-existing files are renames and this change's own pair are
#    additions.
git show --stat -M HEAD

# 7. Nothing in the C# build was disturbed
dotnet build TarkovHelper.sln

# 8. The four decision-doc format invariants hold
dotnet test TarkovHelper.Tests/TarkovHelper.Tests.csproj --filter "FullyQualifiedName~PrdDocsTests"
#    expected: 4 passing
```

By eye, for each corrected legacy file: the corrected `Status` line and any
limitation note must correspond 1:1 between the English original and its Korean
twin.

## Risks & Migration

**Rename detection.** The edits accompanying each move — a corrected `Status` line
and, in some files, a short note — stay far above git's 50% similarity threshold,
so the moves register as renames regardless of edit order. Verify with
`git show --stat -M HEAD` **after committing** (`git diff --stat -M HEAD` compares
the working tree against HEAD and is empty at that point, which proves nothing).

**Rollback.** Documentation-only; reverting the commits restores the prior state
exactly.

Commit sequencing and working-tree state live in the session's plan file, not here —
a commit checklist inside the document would be a progress marker, the thing this
format removes.
