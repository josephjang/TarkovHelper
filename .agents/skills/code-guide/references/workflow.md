# Code Guide Workflow

Turn one substantial change (usually an open PR) into an interactive HTML code
guide: a curriculum a reviewer can learn the design from, with labs that run
the actual decision logic and a quiz gate that checks comprehension. Existing
examples set the format:

- `docs/2026-08-profile-data-attribution-code-guide.html` (part 1)
- `docs/2026-08-profile-data-attribution-deep-review-guide.html` (part 2)
- `docs/2026-08-complete-profile-reset-code-guide.html` (part 3)

## 1. Scope and study

1. Identify the change set: the PR number or branch the user names (default:
   the current branch's open PR). Gather the full diff
   (`git diff main...<branch>` or `gh pr diff <N>`), the decision docs it
   implements (`docs/decisions/<name>.md` and `.spec.md`), and any assessment
   findings it resolves (`docs/assessments/`).
2. Extract, in writing, before designing the page:
   - the whole change in one sentence;
   - 4 to 8 chapter-sized ideas, ordered background first, defect second,
     design third (the reader must be able to judge the design before seeing
     it);
   - the invariants and boundary decisions (the "not after" comparisons, the
     null policies, the capture moments), plus what the PRD rejected and why;
   - which C# tests pin each behaviour, by name.
3. Pick 2 to 4 lab candidates. A good lab is decision logic reimplementable in
   a small amount of vanilla JS with visible consequences: a resolver, a race,
   a fence, a transaction. Each lab must mirror the shipped comparison exactly
   (same `<=`, same null handling, same ordering) and name the test that pins
   the shipped version. If nothing in the change qualifies, say so and propose
   a chapters-only guide instead of inventing a decorative widget.

## 2. File and series conventions

- Path: `docs/YYYY-MM-<topic>-code-guide.html`, flat in `docs/`, kebab-case,
  current year and month.
- One self-contained file: inline CSS, vanilla JS in one IIFE, no external
  requests of any kind. It must work opened from `file://`.
- The NEWEST existing guide is the style source. Copy its `:root` token block
  and component CSS so the set reads as one; the tokens carry the app's own
  AccentBrush gold and the profile hue law. Extend with lab-specific styles;
  do not restyle shared components.
- The colour law is a law: the same profile is the same colour in every
  diagram (`--pvp` blue, `--pve` green, `--season` orange), and `--wrong` red
  is reserved for data in the wrong place. Never spend these on decoration.
- If the topic continues an existing series, say "Part N" in the lede and
  cross-reference the earlier parts.

## 3. Page structure (the house format)

In order:

1. Sticky left rail: numbered chapters plus `LAB` entries, and the gate meter
   that fills as quiz answers land.
2. Hero: eyebrow `PR #N &middot; Code guide`, an h1 that states the promise of
   the change (not a label), a lede framing the page as a curriculum, and a
   second lede naming the companion decision docs.
3. A gold `note` callout: "The whole change in one sentence".
4. Chapters (`chapter-head` with number and kicker). Use real symbol and file
   names; show before/after code excerpts with `.del`/`.add`/`.cmt` spans,
   abbreviated honestly (mark elisions with comments). Use `note is-wrong`
   callouts for costs and defects.
5. Labs interleaved directly after the chapter that motivates them: a
   `lab-head` whose sub names the shipped test, `seg` controls (typically a
   before/after world toggle), a visual state (tables, lanes, or a timeline
   with a slider), and a `verdict` panel whose tone tracks the outcome.
6. Comprehension gate: exactly the established mechanics. Eight questions,
   four options each, one correct; every answer (right or wrong) reveals a
   `why` that teaches; at 8/8 the gate unlocks a reviewer checklist and an
   approval snippet naming the PR and this guide file. Questions test
   reasoning, never vocabulary: a reader who only skimmed must get them wrong.
7. Footer: sources (the decision docs, assessments), the sentence that the
   labs illustrate rather than ship, and the tests that pin the shipped
   behaviour.

## 4. Content rules

- English prose throughout the guide.
- Writing conventions apply inside the HTML: no em dash, no "..." as a single
  ellipsis character, no Korean middle dot. ASCII "..." and "->" are fine. The
  eyebrow's `&middot;` HTML entity is the one established exception.
- Every lab's core comparison must be copied from the shipped code, not
  paraphrased; where the shipped boundary is `<=`, the lab's is `<=`, and the
  lab text should point at the boundary case (put it within reach of the
  controls, e.g. a slider step that lands exactly on an event).
- Do not overstate: where the design accepts a hole (DST fold, clock skew),
  the guide says so in the same terms the spec does.

## 5. Verify before committing

All four are required:

1. Extract the `<script>` block and run `node --check` on it.
2. Scan the whole file for forbidden characters (em dash, ellipsis character,
   Korean middle dot): the count must be zero.
3. Cross-check every id referenced from JS (`getElementById`, selectors)
   against the markup, and every rail link against a section id.
4. Open the file in a browser and drive every control once (each seg state,
   each button to its terminal step, the slider across the boundary, at least
   one right and one wrong quiz answer) when an interactive desktop is
   available; otherwise say plainly that the visual pass is outstanding.

## 6. Index and commit

1. Add an entry to the reference list in `docs/README.md`, in Korean, matching
   its neighbours: the filename link, "코드 가이드 N부" when part of a series,
   one line naming the labs and the quiz gate, and the PR number.
2. Commit both files together as
   `docs: add interactive code guide for <topic>` on the branch of the PR the
   guide explains, so they merge together. If that PR is already merged,
   create a `docs/<topic>-guide` branch and offer a separate PR.
3. Commit only; never push unless the user asks.
