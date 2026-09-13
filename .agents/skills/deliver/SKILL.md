---
name: deliver
description: Plan and execute the delivery of an approved Split change proposal (Product Requirements plus Technical Design) end to end - a sequencing plan, implementation in reviewable slices, an interactive code guide, PR A, an adversarial deep review on a stacked branch, its own guide, and PR B. Use when the pair is approved and the work is a phase or feature large enough to want a review trail; do not use for a change small enough for the Unified form, an obvious bug fix, or a mechanical refactor, which need neither a plan file nor a guide.
---

# Deliver

Read [references/workflow.md](references/workflow.md) completely and follow it
as the required workflow. The plan file it produces is built from
[references/plan-template.md](references/plan-template.md).

This skill turns decisions that are already made into merged, reviewed,
documented work. It does not make product decisions: if the Product Requirements
and Technical Design do not answer a question, that is a blocker to raise, not a
gap to fill by guessing. Use `/design-product-spec` first when the pair does not
exist; it still writes the older `<name>.md` and `<name>.spec.md` pair, which is
renamed to the dated names and retitled to the templates in
`docs/decisions/templates/` before PR A opens.

Three rules carry most of the value and are each easy to skip:

- **Record the baseline before touching anything.** The test count, the build
  warning count. Every number reported later is meaningless without it.
- **Write the failing test before the fix that makes it pass**, for the defect
  the phase exists to remove, and put the red-then-green fact in the commit
  body. A gate that was never seen failing is a gate nobody has evidence for.
- **The deep review runs in its own session, in its own worktree, on a branch
  stacked on PR A.** Not in the session that wrote the code. The review's whole
  value is that it did not make the assumptions being reviewed.
