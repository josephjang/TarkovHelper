# Change Proposal: Deliver Unified proposals

## Summary

The deliver workflow (`/deliver`, `$deliver`) accepts an approved Unified
proposal as well as a Split one. The run keeps its shape for both forms: a plan
file, slices, a code guide, PR A, a deep review on a stacked branch, and, when
the review fixes something, its guide and PR B. What changes for a Unified
proposal is only where the run reads its checklist and where it records what the
Technical Design would otherwise hold. The Split path is unchanged.

## Problem

`2026-09-13-adopt-change-proposal.md` D2 made the deliver workflow take only the
Split form. It gave two reasons. The workflow reads Files touched, the Design
order, Test Strategy and Verification, and a Unified proposal has none of them.
And a change small enough for the Unified form was assumed not to need a plan
file, two guides and two PRs.

The second reason does not hold. The form records how much the technical side
needs its own explanation, not how much the change needs a review trail.
`2026-09-22-remove-quest-recommendations.md`, drafted on its own branch and not
yet merged when this was written, is Unified because a removal has no design
worth a separate document. It still deletes a service, a panel, a
view model, a setting, seven strings in three languages, tests and comments in
more than a dozen files, and it edits the refresh sequence that
`feature-quest-chip-only-status-filter.spec.md` consolidated. That is the kind
of diff the deep review and the guide exist for. When `/deliver` was pointed
at it on 2026-09-22, the run stopped at its precondition: the only way through was splitting the
proposal to satisfy the tool, which distorts the form choice the practice asks
for, or implementing it outside the workflow and losing the plan, the review
and the guide.

The first reason is a real gap, but a small one. The workflow needs a checklist,
a place for the planned checks, a place for divergences, and a place for the
checks that ran. A Unified proposal has a Requirements list and a Decisions
section, and the rest can live in the plan file and the PR body.

## Goals

- An approved Unified proposal can be delivered through `/deliver` without
  being split first.
- A Split proposal is delivered exactly as before.
- Every place that describes the deliver workflow says the same thing about
  which forms it takes.

## Non-Goals

- A shorter path for Unified proposals, such as no guides or no deep review.
  Invoking `/deliver` is the choice of a review trail; a change that does not
  want one is implemented without the workflow, as before (D1).
- Adding Files touched, Test Strategy or Verification sections to the Unified
  template. Section names are fixed per form and `DecisionDocsTests` checks them
  (D2).
- Choosing which deep-review variant step 4 runs. The workflow keeps naming
  `/deep-review`.
- Changing the code-guide, commit or release workflows, beyond teaching the
  code-guide workflow, which step 2 invokes, to find a Unified proposal.

## Requirements

- R1: `/deliver` and `$deliver` take a Unified proposal's file name or slug, as
  well as a Split pair's file name or shared slug. A name that matches no
  approved proposal stops the run before the plan file is written.
- R2: For a Unified proposal, the workflow reads its Requirements as the
  implementation checklist and its Decisions as the settled choices, and does
  not re-derive the facts its Problem already verified.
- R3: For a Unified proposal, the plan file lists the files the change touches
  and the checks it will run, derived from the code while planning, because the
  proposal does not carry them.
- R4: For a Unified proposal, a divergence from the proposal found during the
  work is recorded in its Decisions in the same PR, and the checks that actually
  ran are recorded under Verification in PR A's body (and PR B's, for the fix
  branch).
- R5: The Split path reads and records exactly what it did before: Files
  touched, Design order, Test Strategy, Verification, Technical Decisions.
- R6: The skill description, the `/deliver` command, the workflow, the plan
  template, root `CLAUDE.md` and `docs/decisions/README.md` all describe the
  workflow as taking either form.
- R7: `2026-09-13-adopt-change-proposal.md` carries a `Superseded in part by`
  note naming this proposal and the part of its D2 that this change reverses.

## Decisions

- **D1: A Unified proposal gets the same six steps as a Split one.** A trimmed
  path was considered: no code guides, or a deep review with no stacked PR B.
  It was rejected because the form is chosen by how much the design needs its
  own document, while the decision to run `/deliver` is the decision to want a
  plan, a review and a guide. Tying the second to the first is what produced the
  problem this proposal fixes. The workflow's existing rule already covers the
  small case: a review that fixes nothing ends the run with no part 2 guide and
  no PR B. Revisit if Unified deliveries routinely produce guides with nothing
  to teach.

- **D2: The Technical Design's four roles move to the plan file and the PR
  bodies, not into the proposal.** Letting a Unified proposal carry Files
  touched, Test Strategy and Verification sections was rejected for the same
  reason `2026-09-13-adopt-change-proposal.md` D2 rejected it: section names are
  fixed per form and the guard checks them. Requiring a Split before `/deliver`
  was rejected because it makes the tool, not the change, choose the form. The
  plan file already holds the sequencing; the list of files and planned checks
  sits there beside it, and the checks that ran are what PR A's Verification
  section has always quoted. The one role that must survive the run, a
  divergence from what was decided, goes to Decisions, where the practice
  already keeps a reversed decision. Revisit if the upstream practice gives the
  Unified form a verification section.

## Risks

- The list of files a Unified delivery touches is written by the session that
  implements it, not reviewed with the proposal beforehand, so a file it missed
  is not a divergence from anything approved. Accepted: the Requirements are
  the reviewed stopping condition, and the plan's list exists to make the work
  mechanical, not to be approved. A correction to that list is carried to PR
  A's body; only a Requirement or Decision that no longer holds goes to
  Decisions.
- The record of what was checked lives in a PR body instead of in the merged
  proposal. Accepted: that is where the practice puts it for the Unified form,
  and the PR body names the proposal, so the two are one search apart.
