# Change Proposal: Adopt the change-proposal practice

## Summary

This repository's decision docs become Change Proposals as the
[change-proposal](https://github.com/josephjang/change-proposal) practice
describes them at revision `8fba2af`, the practice that was generalized from
these very documents. A change needs a proposal when it alters observable
behavior, decided from the diff rather than from how hard a decision looks. The
PRD and spec pair becomes the Split form; a single six-section document, the
Unified form, becomes the default. New proposals take the practice's dated file
names in the same `docs/decisions/` folder, three templates replace the two, the
guard test learns the new names and section layouts, and the deliver and
code-guide workflows and the PR template follow the vocabulary. Every document
already merged keeps its name, title, and format.

## Problem

The practice this repository follows is now described twice. The
`feature-decision-docs-process` pair, the folder README, root `CLAUDE.md`, and
the two templates describe it here; the change-proposal repository describes the
same practice, generalized from these documents and taken further, and its
Split-form guide cites this repository's own records as its source. Anything
learned upstream has to be re-derived here, and the two descriptions have
already diverged in four places that cost something:

- **The trigger is a judgment call.** "A hard-to-reverse product decision" and
  "a non-obvious technical decision" are answered per change, by the person or
  assistant most inclined to answer no. The failure the process was built on,
  the top-bar redesign shipping 1155 insertions with nothing under `docs/`, was
  that judgment going wrong, and nothing since has made the judgment mechanical.
- **Small changes have no cheap form.** The templates offer a PRD and a spec. Of
  the twenty-seven changes recorded here, nineteen carry both, and every
  single-document record is either a legacy-format document or one of the two
  README changes. In practice a change was either worth the pair or worth
  nothing.
- **The template and the guard contradict each other.** `spec-template.md` says
  a spec with no PRD "is the normal shape for an internal change";
  `DecisionDocsTests.Every_spec_has_its_sibling_prd` fails any spec without one.
  The contradiction was noticed on 2026-08-08 and has been worked around since
  by always writing the pair.
- **Verification recorded commands, not results.** The spec template's
  Verification section asks for "commands to run" and "the observable result
  that proves it works", which reads as evidence whether or not anything ran;
  `2026-08-quest-data-1-1-refresh-handoff.md` had to separate expected results
  from observed ones after the fact. The practice gives planned checks (Test
  Strategy) and observed results (Verification) separate homes.

## Goals

- Whether a change needs a proposal is answerable from its diff.
- A small change can be recorded in one short document, and a technically
  complex one in a pair that can be reviewed one aspect at a time.
- The practice is explained once, upstream; this folder's README holds only what
  this repository adds on top of it.
- The guard test pins the new form as mechanically as it pinned the old.
- Nothing already merged is moved, renamed, or rewritten.

## Non-Goals

- Renaming `docs/decisions/` to the practice's recommended `docs/changes/` (D3).
- Retrofitting merged documents to the new names, titles, or sections. The
  practice says not to, and a merged filename is a permanent address here.
- Changing the deliver workflow's shape: the plan file, the slices, the two
  guides, the two PRs. Only its vocabulary changes, and which Technical Design
  sections it reads (D5).
- Tooling beyond the guard test: no docs CI step, no linter, unchanged from the
  process decision.
- Translating the practice, or Korean editions of new documents; new documents
  stay English only.

## Requirements

- R1: Root `CLAUDE.md` states the behavior-change trigger, the two forms, the
  file names, and links the upstream guide; `docs/decisions/README.md` holds
  this repository's conventions on top of the practice and links the guide for
  the rest.
- R2: `docs/decisions/templates/` holds `change-proposal.md`,
  `product-requirements.md`, and `technical-design.md`, adapted to this folder
  and its conventions; `prd-template.md` and `spec-template.md` are gone.
- R3: A new Unified proposal is `YYYY-MM-DD-<slug>.md` titled
  `Change Proposal: <name>`; a new Split proposal is
  `YYYY-MM-DD-<slug>.requirements.md` titled `Product Requirements: <name>` and
  `YYYY-MM-DD-<slug>.design.md` titled `Technical Design: <name>`, each linking
  the other below its title. Documents merged before this change keep their
  names, titles, and format.
- R4: `DecisionDocsTests` fails a dated document whose name is not one of the
  three shapes, whose title prefix does not match its suffix, whose top-level
  sections are not the form's fixed names in the template's order, or that
  lacks the form's required sections; it fails a `.requirements.md` or
  `.design.md` without its counterpart, or a pair whose documents do not link
  each other below their titles; and it fails an undated document whose name is
  not one of the twenty-seven changes recorded before this one. The four
  existing invariants still hold, the legacy `.spec.md` pairing included.
- R5: The deliver skill, its plan template, the deliver command, the code-guide
  workflow, and the PR template use the practice's vocabulary, and the deliver
  workflow reads planned checks from Test Strategy and records observed results
  in Verification.
- R6: Each process document whose decision this change reverses carries a
  `Superseded in part by` note naming what changed.
- R7: This proposal is the first document in the new form and passes R4.

## Decisions

- **D1: A change needs a proposal when it alters observable behavior; nothing
  else decides it.** The current trigger, a hard-to-reverse product decision or
  a non-obvious technical decision, was kept in the first draft of this change
  and rejected: it asks for a judgment at exactly the moment the author is least
  inclined to make it, and the precedent in `feature-decision-docs-process.md`
  shows that judgment going wrong on the largest change of its period. "Does the
  diff change what a user or a consuming component observes" is answered by
  reading the diff, and a change with no behavior change says so in its PR
  description, with how that was checked. The cost is more documents: every
  behavior-changing fix pays a Unified proposal. Revisit if the Decisions and
  Risks sections are routinely deleted and the Requirements routinely restate
  the PR title; that would mean the Unified form has become ceremony for this
  repository's fixes, and a lighter floor is the pattern to write then.

- **D2: The PRD and spec pair is the Split form, a single document is the
  Unified form, and there is no design-only shape.** The three-shape model the
  templates implied (PRD alone, spec alone, both) was rejected because the
  practice defines two complete forms and says either Split document alone is
  not a proposal, and because the spec-alone shape never existed here: the guard
  forbade it. The contradiction between `spec-template.md` and the guard is
  therefore resolved in the guard's favor, and the guard now checks the pairing
  in both directions. A change whose only non-obvious content is technical is
  still a Split proposal with short Product Requirements; a Unified proposal
  carrying Design and Test Strategy sections was rejected because section names
  are fixed per form and the guard checks them. The deliver workflow takes only
  the Split form: it reads Files touched, the Design order, Test Strategy, and
  Verification, none of which a Unified proposal has, and a change small enough
  for the Unified form does not need a plan file, two guides, and two PRs.

- **D3: The folder stays `docs/decisions/`; new files take the practice's dated
  names.** Renaming the folder to `docs/changes/` was rejected: it would be the
  second rename in five weeks, it touches more than twenty live path references
  including both `CLAUDE.md` files, the guard, and the test repo-root walk, and
  the practice names its location as a recommendation while allowing a
  repository's own. `feature-decisions-folder-rename.md` argued that the folder
  name should carry the umbrella term; that goal no longer holds, and the
  supersede note on it says so. The name `decisions` still describes what every
  proposal holds. Keeping the `feature-` and `fix-` prefixes with `.md` and
  `.spec.md` for new files, which the practice allows, was rejected too: a bare
  `.md` cannot be told from the product half of a pair without looking for a
  sibling, the prefix was never used by tooling, and the date gives the folder
  an order it lacked and replaces the `Created` field. Because the file name
  now tells the form, a draft that starts Unified and splits mid-flight is
  renamed from `.md` to `.requirements.md` before it merges; the upstream option
  of keeping the `.md` path is not taken here. Nothing merged is renamed.
  Revisit the folder name only if something else forces a rename, and do both
  at once.

- **D4: The templates are local copies, adapted.** Pointing authors at the
  upstream templates was rejected: they name `docs/changes/`, they carry em
  dashes this repository's writing convention excludes, and they cannot carry
  this repository's conventions (reference by filename, code by symbol, English
  only, the Files touched list, the supersede note). Keeping the old templates
  beside the new ones was rejected because a template that exists gets used.
  The local copies name the upstream revision they were taken from, so a later
  upstream change is adopted deliberately, by a later proposal.

- **D5: Technical Design keeps a `Files touched` list at the end of Design.**
  The practice discourages exhaustive file inventories in a design because
  copied implementation detail goes stale. Here the list is not a copy of the
  implementation but a reviewed claim about where the change lands, and the
  deliver workflow reads it as the implementation checklist and records every
  divergence from it in Technical Decisions;
  `feature-kappa-collector-1-1.spec.md` shows both halves working. Moving the
  list to the uncommitted plan file was rejected because the divergence record
  would then compare the diff against prose. The list names where the change
  lands, not what each file's diff will contain. Revisit if the lists are
  routinely appended around rather than kept honest; then they belong in the
  plan file.

- **D6: A draft is revised while its change is open; append-only applies after
  the last merge.** The process decision made every field write-once from the
  first draft, so that nothing would need upkeep. Upstream rejected that for
  drafting because obsolete assumptions end up competing with the current
  proposal in the same document, and this repository saw the same: the channel
  pair had to be folded into one coherent record after the fact (`3530285`).
  The invariant the old rule protected, that no field ever needs upkeep on
  `main`, survives unchanged: after the change's last PR merges, the only later
  write is a `Superseded in part by` note from a reversing PR. A change
  delivered as PR A and PR B is one change; PR B still edits the proposal.

- **D7: The guard checks shape, not content.** Titles, section names and order,
  required sections, pairing, and mutual links are mechanical and cheap; whether
  a Problem states a problem, whether Verification records what actually ran,
  and whether a reversing PR remembered its supersede note remain review
  discipline, as they were. Checking section order for the Split documents is
  stricter than the upstream guide, which fixes the order only for the Unified
  form; it costs nothing and keeps the three templates authoritative. A dated
  document with any other name shape, a `.ko.md` twin included, fails: new
  documents are English only, and the guard is where that rule is cheapest to
  hold. The undated names are a closed set, the twenty-seven changes recorded
  before this one, listed in the guard the way the eleven flattened legacy
  documents already are; a new undated file fails, so a document written in the
  old form, by habit or by tooling that knows only the old pair, cannot merge
  unnoticed. The list never grows, because every later document is dated.

## Risks

- Every behavior-changing fix now costs a document. Accepted: a Unified proposal
  with its empty sections deleted is a few paragraphs, and D1 names the signal
  that would mean it has become ceremony.
- Two naming generations share one folder. Accepted: the seam is a date, the
  README explains it and maps the old sections to the new, and renaming merged
  files would break the permanent-address rule for no reader's benefit.
- The local templates can drift from upstream. Accepted: they name the revision
  they came from, and adopting a later revision is a proposal like this one.
- The section check can reject a document that legitimately wants a top-level
  section its form does not have. Accepted: a `###` subsection under the
  nearest fixed section is always available, and a real need for a new
  top-level section is a pattern to write and a guard to extend deliberately,
  not a check to loosen in passing.
