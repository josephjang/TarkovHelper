---
description: Plan and execute the delivery of an approved decision-doc pair end to end
---

Deliver the approved decision-doc pair named by `$ARGUMENTS`.

Read `.agents/skills/deliver/references/workflow.md` completely and follow it as
the required workflow. The plan file it produces is built from
`.agents/skills/deliver/references/plan-template.md`.

`$ARGUMENTS` names the pair: a `docs/decisions/` file name, or the topic the PRD
and its spec share. If it is empty, ask which pair this run implements before
writing the plan file.

The workflow makes no product decisions. If the pair does not exist, or does not
answer a question the implementation needs, stop and say so: that is a blocker
for `/design-product-spec`, not a gap to fill by guessing.
