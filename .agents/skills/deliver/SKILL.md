---
name: deliver
description: Plan and execute the delivery of an approved change proposal, Split (Product Requirements plus Technical Design) or Unified, end to end - a sequencing plan, implementation in reviewable slices, an interactive code guide, PR A, an adversarial deep review on a stacked branch, its own guide, and PR B. Use when the proposal is approved and the work is large enough to want a review trail, whichever form its proposal takes; do not use for an obvious bug fix or a mechanical refactor, which need neither a plan file nor a guide.
---

# Deliver

Read [references/workflow.md](references/workflow.md) completely and follow it
as the required workflow. The plan file it produces is built from
[references/plan-template.md](references/plan-template.md).

This skill turns decisions that are already made into merged, reviewed,
documented work. It does not make product decisions: if the proposal (a Split
pair's Product Requirements and Technical Design, or a Unified proposal) does
not answer a question, that is a blocker to raise, not a gap to fill by
guessing. When no proposal exists, draft one first from the template in
`docs/decisions/templates/` whose form fits the change (Unified by default, as
root `CLAUDE.md` says), and get it approved. A Unified
proposal carries no file list or test strategy, so the plan derives them from
the code; that is sequencing, not a product decision. A pair drafted in the older `<name>.md` and `<name>.spec.md` form is
renamed to the dated names and retitled to the templates in
`docs/decisions/templates/` before PR A opens; the guard rejects a new undated
file.

Three rules carry most of the value and are each easy to skip:

- **Record the baseline before touching anything.** The test count, the build
  warning count. Every number reported later is meaningless without it.
- **Write the failing test before the fix that makes it pass**, for the defect
  the change exists to remove, and put the red-then-green fact in the commit
  body. A gate that was never seen failing is a gate nobody has evidence for.
  A change with no defect to reproduce (a pure removal, say) says why no
  fail-first step applied rather than inventing a red run (workflow step 1).
- **The deep review runs in its own session, in its own worktree, on a branch
  stacked on PR A.** Not in the session that wrote the code. The review's whole
  value is that it did not make the assumptions being reviewed.
