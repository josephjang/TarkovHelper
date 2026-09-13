# Technical Design: [Change name]

Product Requirements: [Change name](YYYY-MM-DD-<slug>.requirements.md)

<!-- Technical part of a Split Change Proposal, paired with Product Requirements.
     Save as docs/decisions/YYYY-MM-DD-<slug>.design.md, with the same date and slug as the
     .requirements.md. The link above is required: it points at the actual Product Requirements
     file, which links back. Both documents together are the proposal; there is no third file.
     Explain enough context and reasoning for a dedicated technical review.
     Refer to the Product Requirements' R IDs without redefining acceptance criteria.
     Delete sections with nothing to say; keep the section names and their order, which
     DecisionDocsTests checks (Design, Test Strategy, and Verification are always present).
     Reference code by symbol (UserDataDbService.InitializeAsync, line numbers optional) and other
     proposals by filename, never by folder path.
     Revise the draft while the change is open and preserve meaningful reversals and their reasons
     in Technical Decisions. After the change's last PR merges, the only later write is a
     "Superseded in part by <doc>" note that a reversing PR appends in a blockquote below the title.
     If this design records a decision whose implementation is deliberately deferred, say so in the
     Summary: a merged Technical Design is otherwise read as shipped.
     Full explanation and the separate review questions, upstream:
     https://github.com/josephjang/change-proposal/blob/main/docs/split-proposals.md
     (this template follows the upstream template at revision 8fba2af). -->

## Summary

<!-- The solution in a few sentences and the main technical ideas, readable on its own. -->

## Non-Goals

<!-- Technical work deliberately excluded, and why: an adjacent refactor, a schema rewrite,
     or a new data source left for another change. Product exclusions stay in Product Requirements.
     Reflect any consequence for the promised outcome there and link the technical rationale. -->

## Context

<!-- Relevant current behavior, technical constraints, and boundaries, anchored to paths
     and symbols. Record the inspected commit (for example, "verified at <sha>") and identify
     any relevant uncommitted changes used in the analysis. Distinguish confirmed root causes
     from hypotheses; for a fix, the root cause here is confirmed by reading the code, never guessed. -->

## Design

<!-- Connect the design to R IDs from Product Requirements. Explain boundaries, contracts,
     data flow, state transitions, and failure behavior needed to assess the choices.
     Use a diagram or small contract example when useful. Reference executable definitions;
     omit whole schemas and task breakdowns: implementation order belongs in the deliver plan file.
     End with the "Files touched" list below, naming where the change lands (services, files,
     tests). The deliver workflow reads it as the implementation checklist and records every
     divergence from it under Technical Decisions. Where the change lands, not what each file's
     diff will contain. -->

### Files touched

-

## Technical Decisions

<!-- Record technical choices with alternatives. Link product decisions rather than repeating them.
     Use TD IDs for new decisions; retain existing IDs when moving decisions from a draft and
     update references or leave a short pointer at the old location where needed.
     If a choice changes scope, a user promise, or the stopping condition, revise Product
     Requirements too. Keep meaningful reversals here while updating Design to agree, including
     where the implementation diverged from the design above. -->

- **TD1: [Decision sentence.]** [Rejected alternative, reason, and when to revisit.]

## Open Questions

<!-- What remains unresolved and what would settle it. Resolve questions needed to establish
     the agreed behavior before merging implementation; explicitly defer those outside scope.
     Delete the section if empty. -->

## Test Strategy

<!-- Account for every R ID with a check and the observable result that proves it.
     In this repository: Unit for the invariant each guard pins, E2E for the user-visible path,
     and a manual method where neither reaches; say what cannot be automated and why.
     Reference existing coverage where sufficient. Commands to run live here.
     This is the plan: method and expected observation, not a record of a passing run. -->

- R1: [Invariant or behavior; test or manual method; expected observation.]

## Verification

<!-- Record which planned checks actually ran and what was observed, tied to a run or tested
     revision. Reference R IDs or checks from Test Strategy rather than copying the plan.
     Say what was not checked and why. If no checks have run yet, say so explicitly.
     Before merge, account for the planned coverage, including omissions and limitations. -->

- Checked: [R IDs or checks; run or tested revision; observed results.]
- Not checked: [Check; reason; remaining limitation.]

## Risks & Migration

<!-- Accepted technical risks and why, including compatibility, migration ordering, rollback,
     and limitations. Reflect product consequences in Product Requirements and link them.
     Independent technical review and the pair's consistency review should assess these links. -->
