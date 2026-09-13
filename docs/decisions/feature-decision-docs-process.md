# Decision Docs Process — Split Product Decisions from Technical Design — PRD

- **Created**: 2026-07-29

> Written in the format it proposes — this is the worked example the templates are
> derived from. The sibling `feature-decision-docs-process.spec.md` holds the
> technical design.

> Superseded in part by `feature-decisions-folder-rename.md` (2026-08-08): the
> Product Decisions statement that the folder keeps its `docs/PRDs/` name is
> reversed; the folder is now `docs/decisions/`. Every other decision stands.

> Superseded in part by `2026-09-13-adopt-change-proposal.md` (2026-09-13): the
> umbrella term is now Change Proposal, with a Unified form (one document) and a
> Split form (Product Requirements plus Technical Design, the two roles PRD and
> spec named); the trigger (R5's PRD, spec, both, or nothing) is replaced by
> "does the change alter observable behavior"; new documents take dated names
> instead of `name.md` and `name.spec.md`; and "Documents are append-only" (R2)
> now applies from a change's last merge, a draft being revised while its change
> is open. The born-final model, the same-PR rule, the pairing by filename, the
> PR-body naming rule, and the supersede rule stand.

## Summary

The documentation process is rebuilt around three rules:

1. **Decisions get documents.** A product decision goes in `name.md` (a *PRD*), a
   technical decision in `name.spec.md` (a *spec*). The two pair by filename, and a
   change may need one, both, or neither. Together they are the repository's
   **decision docs**. A document is written on the work's branch and merges in the
   same PR as the work itself.
2. **Documents are append-only.** Every field is written exactly once, at a moment
   when its value is known. A decision discovered mid-flight is appended, never
   revised into what was already written.
3. **State belongs to GitHub, not to documents.** A document on `main` is a
   decision record in effect; work in flight exists only as an open PR. There are
   no `active/`/`archive/` folders for new documents, no status fields, no exit
   rules — and nothing at all to do after a merge. The one exception: a change
   that reverses a recorded decision appends `Superseded by <doc>` to the old
   document, in the same PR that reverses it.

The rest of this document is the evidence behind these rules and the decisions that
shape their edges.

## Problem

Measured against the repository before this change, the old process failed in two
ways.

**Documents lied.** Five of the six documents in `active/` described work that had
already shipped; four of them still read `In Progress` or `Review`, and the fifth
said `Completed` but sat in `active/` with its Archive Info never written:

| Document | Says | Actually |
|---|---|---|
| `fix-quest-name-localization.md` | In Progress | PR #2, #3 merged 2026-06-13 |
| `feature-hideout-localized-sort.md` | In Progress, "PR opened against upstream" | PR #4 merged 2026-06-19 |
| `feature-quest-unlock-sort.md` | Review, "PR pending" | PR #5 merged 2026-07-01 |
| `feature-persist-map-view-state.md` | Review, "PR #8 open" | PR #8, #9 merged 2026-07-24 |
| `feature-fork-release-process.md` | Completed | correct — but still in `active/`, with no Archive Info |

The truth sat in GitHub's merged PRs the whole time. The cause is structural, not
carelessness: the template demanded fields that must be **kept current** while work
is in flight — `Status`, `Updated`, `Progress Log`, phase checkboxes — and that
upkeep happened on exactly the two documents covering two-to-three-day tasks. Every
longer-running document rotted. `archive/2025-12/map-markers-rendering.md` shows the
end state plainly: all seven completion criteria unchecked for work that shipped the
same day the document was written.

**One document did two jobs.** The template mixed the product decision (what should
change for a user, and why) with the implementation plan (which files change, in
what order). Recent documents had already split on their own, in both directions:
`feature-quest-unlock-sort.md` carries requirements but no implementation plan,
while `fix-userdata-init-deadlock.md` is its inverse — root cause and test plan, no
product content.

The cost is decisions that vanish. The largest recent change — the top-bar redesign,
1155 insertions across 9 files — shipped with zero changes under `docs/`; its
central product decision ("four mockup alternatives reviewed, option A selected")
survives only in a pull request body. A smaller third problem — the README
documenting agents deleted months earlier — is detailed in the spec's Current
Behavior.

## Goals

A reader six months from now can open `docs/PRDs/` and trust every document they
find: what is there records decisions in effect, and nothing in it ever depended on
someone remembering to update it. Work in flight is visible where it actually
happens — in open pull requests — not mirrored into documents.

Durable decisions live in the repository; disposable step ordering lives in the
session's plan file. And the rules saying when a document is required live where
the decision is actually taken — the always-loaded root `CLAUDE.md` — so a change
the size of the top-bar redesign cannot ship undocumented by accident.

## Non-Goals

- **Automated validation beyond the four-invariant guard** — no frontmatter, docs
  CI step, or `/prd` command this round. (The first draft declined the guard test
  too; the deep review of this PR reversed that deliberately — see Risks.)
- **Reorganizing the legacy archive.** `archive/YYYY-MM/` documents keep their
  format and their location.
- **Unifying the four bilingual conventions** that coexist in `.ko.md` files.
- **Cleaning up non-PRD files** in `archive/2025-12`, or the two stray `.prd` files
  under `TarkovHelper/`.
- **A retroactive PRD** for the top-bar redesign.
- **A formal marker for decision records merged without implementation.** The one
  existing case (`fix-userdata-init-deadlock`) is handled with a one-line prose
  note; a field analogous to `Superseded by` is deferred until a second case
  exists and the pattern is confirmed.

## Requirements / Acceptance Criteria

- **R1** — A document's state never has to be looked up inside it: a document on
  `main` records decisions in effect (or says `Superseded by <doc>`), and open
  work is an open PR — state GitHub owns entirely.
- **R2** — No field has to be kept current: every write is once-and-done, and a
  mid-flight discovery is an append — history, which cannot go stale.
- **R3** — Product decisions and technical design live in separate files, paired by
  filename; a change may have only one of the two, or neither.
- **R4** — The README describes only mechanisms that exist in the repository today.
- **R5** — Whether a change needs a PRD, a spec, both, or nothing is decidable from
  the always-loaded root `CLAUDE.md` — not only from a folder README that a
  session skipping documentation never opens.
- **R6** — **Zero post-merge obligations.** Once a document's PR merges, the
  process never requires touching that document again. (The supersede append is
  performed by the later change that needs it — an act, not upkeep.)
- **R7** — New documents are English-only. Existing Korean twins stay paired 1:1
  with their originals, and the English original wins any conflict.

## Product Decisions

**Two document types, paired by filename.** `name.md` carries the product decision,
`name.spec.md` the technical design. Sibling files beat the alternatives — one file
with a hard internal boundary, or two parallel folder trees — because they reuse the
`.ko.md` twin convention this repository already runs (same folder, same lifecycle),
and because a change can carry just one: a fix with no product decision is a spec
alone; a product change with an obvious implementation is a PRD alone. The pairing
is derived from the filename, never stored in a field: a stored cross-reference is
a second source of truth, and it goes stale in exactly the routine case of a spec
added after its PRD.

**The process is named for decisions, not for PRDs.** The format makes *PRD* the
name of the product half, so keeping it as the umbrella term as well would leave
"does this change need a PRD?" with two readings — and the trigger rules depend on
that question having one. The umbrella term is **decision docs**; *PRD* refers
only to the `name.md` half. The folder keeps its
`docs/PRDs/` name for the churn reasons recorded in the spec, so the README states
explicitly that the folder holds both types.

**A document leads with its conclusion.** The first drafts of this very pair buried
the design under their own evidence — accurate everywhere, readable nowhere. Both
templates therefore open with a `## Summary` that states the decision or design in a
few sentences, and the decision sections put the decision in the first sentence with
the evidence kept to a sentence or two after it. The reasons stay — a rule without
its reason gets undone — but they follow the rules, never precede them.

**The discriminator is "must this be kept current?", not "is this ever edited?".**
Removed: `Status`, `Updated`, `Progress Log`, and all checkboxes — each has to be
revisited whenever reality moves, and five of six documents prove that revisiting
does not happen. Kept: `Created` and appended decisions — each written once at a
well-defined moment and never revisited.

**Documents are born final.** In this repository's actual flow, the document and its
implementation merge in the same PR — so a document reaches `main` only when the
work it describes is done, and there is no "in progress" state to track on `main`
at all. That dissolves the machinery earlier drafts of this change still carried:
no `active/` → `archive/` move, no `Outcome` section written at a terminal moment,
no exit rules, and no janitor to depend on — every write happens on the branch, at
the moment of highest energy. Dropped work never reaches `main`; its PR closes
unmerged and GitHub keeps that record. Work spanning several PRs (two of the five
recently shipped documents did) leaves the document silent about the gap: the
remaining work is an open PR or issue, which GitHub owns.

**A document never links its PRs; each PR names its documents.** A PR's number does
not exist until the PR is created, which is after the document is written — so a
PR field can only be filled by reopening a finished document, a bookkeeping-only
edit of exactly the kind this format removes. The link runs the other way: the PR
body names the documents it implements, written while authoring the PR and
requiring no commit, so `gh pr list --search "<name>"` resolves a document to its
PRs — and `git log --follow` on the file gives the same answer offline. The
document's header carries only `Created`.

**A reversed decision is marked by the change that reverses it.** A document on
`main` claims its decisions are in effect, and a later change can make that false.
The fix is one append — `Superseded by <doc>` — written in the same PR that
reverses the decision, by the session already editing that folder. This is the only
write the process ever asks for after a document's own PR merges, and it is coupled
to an act rather than a schedule, so it cannot rot the way scheduled upkeep did.

**Decision records without implementation are prose, not a field.**
`fix-userdata-init-deadlock` records a completed investigation whose fix is
deliberately deferred — a legitimate document with no implementation behind it,
which "born final" would otherwise misread as shipped. It gets a one-line note
stating exactly that, true at the time of writing; if implementation lands later,
that PR appends its link. A formal marker analogous to `Superseded by` was
considered and deferred until a second such case confirms the pattern. The spec
template's header blockquote now cues this note explicitly, so the next such case
is not produced blind while the formal marker waits.

**`Owner` and `Related Agents` are dropped for their own reasons, not for upkeep.**
This repository has a single maintainer, `Owner` accumulated eight different
spellings, and once a PR exists it identifies the author precisely; the field
returns if a second regular contributor appears. `Related Agents` named agents
deleted in `b2fdac8` — a field pointing at machinery that no longer exists.

**New documents are English-only; existing `.ko.md` twins stay as they are.** Four
incompatible bilingual conventions already coexist; halting new translation stops
the drift without rewriting the past. Two rules survive for the existing twins: the
English original wins any conflict, and code identifiers (`AppLanguage.KO`,
`NameKO`) are never translated.

**Legacy folders are frozen; everything in `active/` flattens.** The 34 documents
(35 files) in `archive/YYYY-MM/` stay exactly where they are — their paths can never change
again, which is all that matters. Everything in `active/` moves up to `docs/PRDs/`
once, and `active/` disappears. Sorting the flattened documents by finished-ness
into `archive/` first was considered and rejected: it would re-encode in folders
the very state distinction this design removes. With no future moves, a filename is
a permanent address — which retires the entire class of path breakage the old
process had to defend against. Flattened legacy documents keep their original
format; only their false `Status` lines are corrected, because a factual error is
not a format.

## Risks

**The removed fields get reintroduced** by a future session following the old
format from memory. Mitigation: the README records *why* each field was removed,
with the five-of-six evidence — a rule without its reason gets undone; a rule with
its reason gets argued with first.

**Drift returns and nothing catches it.** This was the cost of declining automated
validation in the first draft, accepted knowingly. The deep review of this PR
judged that cost understated — every surviving obligation was discipline-guarded
while these checks are mechanical — and reversed the decision:
`TarkovHelper.Tests/PrdDocsTests.cs`, following the `UpdateXmlTests.cs` precedent
(which already asserts on a non-code repository file), now checks four invariants
offline:

1. no new-format document contains `**Status**`, `**Updated**`, `**Owner**`,
   `**Related Agents**`, `## Progress Log`, or an unticked task checkbox
   (legacy-format documents are exempt);
2. every `name.spec.md` has a `name.md` beside it;
3. every `.ko.md` has its English original in the same folder;
4. every `docs/PRDs/…` path written in a tracked file resolves.

(The earlier fifth check — no document in `active/` with a merged PR — needed the
network; born-final removes the condition it existed to catch.) What no guard
catches — a forgotten `Superseded by` append, the quality of a document's content —
remains discipline, accepted with the act-coupling rationale above.

**A document on `main` can outrun a multi-PR change.** Between PR 1 and PR 2 the
document reads as complete while work remains. Accepted: the remaining work is an
open PR or issue — GitHub state, discoverable through the PR-body naming rule — and
mirroring it into the document is exactly the rot this design removes.

**Two files raise the cost of writing.** The trigger rules make one file, or zero,
the common case; only a change with both a non-obvious product decision and a
non-obvious technical decision pays for two.
