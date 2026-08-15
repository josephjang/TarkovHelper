# Code Guide Workflow

Turn one substantial change (usually an open PR) into an interactive HTML code
guide: a curriculum a reviewer can learn the design from, with labs that run
the actual decision logic and a quiz gate that checks comprehension.

The series so far is indexed in `docs/README.md`. These three set the format:

- `docs/2026-08-profile-data-attribution-code-guide.html` (part 1)
- `docs/2026-08-profile-data-attribution-deep-review-guide.html` (part 2)
- `docs/2026-08-profile-settings-race-deep-review-guide.html` (part 7, and the
  first written in ASD-STE100; see section 4)

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
  current year and month. A guide covering a deep review of an earlier guide's
  change is `docs/YYYY-MM-<same-topic>-deep-review-guide.html`, reusing the
  earlier guide's topic slug so the pair sorts together (parts 1/2, 3/4, 6/7).
- One self-contained file: inline CSS, vanilla JS in one IIFE, no external
  requests of any kind. It must work opened from `file://`.
- The NEWEST existing guide is the style source for CSS. Copy its `:root` token
  block and component CSS so the set reads as one; the tokens carry the app's
  own AccentBrush gold and the profile hue law. Extend with lab-specific styles;
  do not restyle shared components.
- Copy the CSS, not the voice. Parts 1 to 6 predate the ASD-STE100 rule in
  section 4 and their prose does not follow it. Match their structure and their
  styling; write the prose to section 4.
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
3. A gold `note` callout headed "The whole change". Say it in as few sentences
   as the 25-word limit in section 4 allows, and never pad it back out to one
   long sentence: the summary is the callout's job, the single sentence was
   only ever the means. (Parts 1 to 6 head it "The whole change in one
   sentence" and are one sentence each.)
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

### Prose is ASD-STE100 (Simplified Technical English)

English throughout, written to ASD-STE100. This covers every reader-facing
string: hero and ledes, chapter prose, callouts, lab headings and subs, the
control labels and stat-tile captions, the verdict strings in the JS, the quiz
questions with their options and explanations, the checklist, the gate text,
the approval snippet, and the footer.

The checker in section 5 enforces these four:

- One sentence says one thing, and is 25 words or fewer.
- One paragraph is 6 sentences or fewer. Splitting a paragraph in two is the
  normal fix, and is why an STE guide has more `<p>` elements than parts 1-6.
- No contractions.
- None of a short, high-confidence list of non-approved words.

These are yours to apply, because no checker here can:

- Active voice, simple tenses, articles kept, no gerund used as a noun, no
  noun cluster longer than 3 words.
- Approved vocabulary wherever the word is not a technical name or verb from
  the domain. Substitutions this series has already made: raises -> sends,
  coalesce -> collect, deferrals -> items for later, hand-edited -> edited by
  hand, via -> by, in order to -> to.
- Domain terms stay: snapshot, publish, announce, dispatcher, revision gate,
  and any C# identifier. STE permits technical names and technical verbs.

**Never rewrite into STE:** the quoted code excerpts and the comments inside
them, identifiers, file paths, and test names. They are quotations. Changing
them to fit the vocabulary makes the guide false, which is a worse defect than
any wording.

STE and the house format collide in one place, and section 3 point 3 records
the resolution. If they collide anywhere else, keep STE and say in the PR body
which format point you bent.

### Other content rules

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

Two checkers ship with this skill. Run both from the repo root:

```sh
node .agents/skills/code-guide/scripts/check-guide.mjs docs/<file>.html
node .agents/skills/code-guide/scripts/check-ste.mjs docs/<file>.html
```

`check-guide.mjs` covers points 1 to 3 below, `check-ste.mjs` point 4. Both
exit non-zero on a finding. Fix findings; do not waive them.

All five are required:

1. The extracted `<script>` block passes `node --check`.
2. No forbidden characters (em dash, ellipsis character, Korean middle dot),
   and no external requests.
3. Every id referenced from JS resolves in the markup, every rail link
   resolves to a section, every `bindSegment` root has buttons, and every
   segment has exactly one `aria-pressed="true"` default.
4. ASD-STE100: sentence length, paragraph length, contractions and a
   conservative non-approved-word list. **Report its limit honestly:** it
   checks the writing rules, not the full approved-word dictionary, so say
   "the STE writing rules pass" and never "STE compliant".
5. Drive the labs, both ways:
   - **In node**, which is what actually proves the logic. Write a throwaway
     driver in the scratchpad that imports
     `.agents/skills/code-guide/scripts/dom-shim.mjs`, runs the guide's own
     `<script>`, and then walks every control combination: each seg state,
     each slider position, one right and one wrong quiz answer, and the gate
     to 8/8. Assert the outcomes against the SHIPPED behaviour you read out of
     the C# - never against whatever the lab currently prints, which would
     pass even when the lab is wrong.
   - **In a browser**, for layout only: a long run of `.add`/`.del` lines, a
     rail label that no longer matches its heading, a table that overflows.
     Drive one control to confirm the page is live. If no interactive desktop
     is available, say plainly that the visual pass is outstanding.

## 6. Index and commit

1. Add an entry to the reference list in `docs/README.md`, in Korean, matching
   its neighbours: the filename link, "코드 가이드 N부" when part of a series,
   one line naming the labs and the quiz gate, and the PR number. ASD-STE100 is
   an English standard, so it does not reach this entry; the repo's own writing
   conventions still do.
2. Commit both files together as
   `docs: add interactive code guide for <topic>` on the branch of the PR the
   guide explains, so they merge together. If that PR is already merged,
   create a `docs/<topic>-guide` branch and offer a separate PR.
3. Commit only; never push unless the user asks.
