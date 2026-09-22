---
description: Plan and execute the delivery of an approved change proposal (Split or Unified) end to end
---

Deliver the approved change proposal, Split or Unified, named by `$ARGUMENTS`.

Read `.agents/skills/deliver/references/workflow.md` completely and follow it as
the required workflow. The plan file it produces is built from
`.agents/skills/deliver/references/plan-template.md`.

`$ARGUMENTS` names the proposal: a `docs/decisions/` file name, or its slug (the
one a Split pair's Product Requirements and Technical Design share, or a
Unified proposal's). If it is empty, ask which
proposal this run implements before writing the plan file.

The workflow makes no product decisions. If the proposal does not exist, or does
not answer a question the implementation needs, stop and say so: that is a
blocker for `/design-product-spec`, not a gap to fill by guessing.
