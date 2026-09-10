# Plan template

Copy to `PLAN-<topic>.md` at the repo root, fill the bracketed parts, and
delete the parts that do not apply. It is never committed and is deleted at the
end of the wrap-up.

The template is longer than it looks like it needs to be. Every section below is
one that was consulted mid-run in the phase this came from; the plan earns its
length by removing questions from the middle of the work, where answering them
means stopping.

---

# Plan: [topic], from code to merged fixes

Temporary working document. It is never committed (the commit workflow stages by
explicit path) and it is deleted at the end of the wrap-up. The decisions live in
`docs/decisions/[name].md` and its spec; this file only sequences the work and
records what each step needs.

## Starting state ([date])

- Worktree `[path]`, branch `[branch]` at `[sha]` = `main` (`[sha]`) plus
  [what else]. Release in the field: `[version]`. Published data: `[version]`.
- The spec's "Files touched" is the implementation checklist and its
  "Verification" section holds the commands. Facts already verified
  ([what: row counts, bounds, live probes]) are in the spec's Current Behavior;
  do not re-derive them.
- **Baseline, measured before the first edit:** build [N] warnings / [N] errors,
  non-E2E suite [N] passed, E2E [N] passed / [N] skipped.
- Six steps, two PRs, two guides:
  1. implement on `[feature-branch]`
  2. code guide `docs/[YYYY-MM]-[topic]-code-guide.html`
  3. PR A (feature)
  4. `/deep-review` of PR A, fixes on `fix/[topic]-review-fixes`
  5. deep-review guide `docs/[YYYY-MM]-[topic]-deep-review-guide.html`
  6. PR B (fixes), stacked on PR A

## Rules that apply to every step

- Build and tests: `dotnet build TarkovHelper.sln`, then
  `dotnet test --no-build --filter "Category!=E2E"`; E2E with `Category=E2E` on
  the desktop.
- Commits through `/commit`: conventional, English, imperative, 72 chars,
  scopes from recent history, no attribution footer in commits or PR bodies
  (root `CLAUDE.md` overrides the tool default).
- Writing conventions in every file, HTML guides included: no em dash, no
  single-character ellipsis, no Korean middle dot.
- E2E needs an exclusive desktop slot: no elevated TarkovHelper in the
  foreground. [Known environmental failures on this machine, and how to tell
  them from a real one.]
- Never put "pause", "wait for a slot" or session-limit management into a
  subagent prompt; the only pause an agent has is ending its turn.
- The app runs without UAC through
  `dotnet TarkovHelper\bin\Debug\net8.0-windows\TarkovHelper.dll`; its user data
  is `TarkovHelper\bin\Debug\net8.0-windows\Config\user_data.db`.

## Step 1. Implementation

Branch: `git switch -c [feature-branch]` from `[base]`, in this worktree, so the
decision docs merge with the work (PR A carries them).

Work in the spec's Design order; each slice is one commit with its tests.

1. **[Slice name]** (`[type](scope): [subject]`): [what changes]. Tests:
   [which, new or extended]. [Anything the compiler will find for you.]
2. ...

For the slice that closes the phase's defect: write and run the tests BEFORE
the fix; they must fail for the expected reason. Put the red-then-green fact in
the commit body.

Exit criteria before step 2:

- [ ] build clean in Debug and Release, zero warnings
- [ ] non-E2E suite green, count above the baseline by the new tests
- [ ] E2E: new suites green; existing suites green or failing only in the known
      cases
- [ ] self-review of the whole diff against the global checklist (removed
      behaviour, ripple, footguns, wrappers, duplication), reading each edited
      function whole
- [ ] the spec's "Files touched" matches the diff; a divergence is appended to
      the spec's Technical Decisions, not left silent
- [ ] working tree clean, every slice committed

## Step 2. Code guide

`/code-guide [branch or PR number]`. Inputs to study: the branch diff against
`main`, both decision docs, [the previous guide in the series], and the newest
guide for the CSS tokens.

Chapter candidates (background, defect, design): [list]

Lab candidates (2 to 4, each mirroring the shipped comparison and naming its
test): [list]

Verification, all required:

- [ ] `check-guide.mjs` passes
- [ ] `check-ste.mjs` passes (report it as "the STE writing rules pass")
- [ ] a node driver over `dom-shim.mjs` walks every control state and asserts
      against the C# behaviour, not the lab's current output
- [ ] headless render pass at 1440 and 390
- [ ] `docs/README.md` entry in Korean, matching its neighbours, with the PR
      number
- [ ] commit on the feature branch

## Step 3. PR A

[Base, repo, title.] Check for an existing PR first.

Body checklist:

- [ ] first paragraph names both decision docs
- [ ] Why (in numbers), What changed (one bullet per slice), divergences from
      the spec, Tests (before/after and the fail-first evidence), Verification
      (exact commands), Residuals
- [ ] no attribution footer
- [ ] CI green

Do not merge before step 4 finishes unless the user says so: the review fixes
stack on this branch.

## Step 4. Deep review and fixes

Preparation:

- [ ] clean, committed tree on the feature branch
- [ ] `git worktree add -b fix/[topic]-review-fixes ../[short-name]
      [feature-branch]` (long icon paths overflow with a long prefix)
- [ ] **a fresh agent session in that worktree**, not this one

Run `/deep-review main...[feature-branch]`. Answer its steering questions.
If interrupted, re-invoke with the same arguments; it resumes from its
checkpoint.

After the report:

- [ ] read every Fixed, Refuted and Needs-your-decision item
- [ ] read the production diff yourself before committing it
- [ ] build, both suites, and any guide checkers and lab drivers the review's
      edits could have broken
- [ ] a fix that reverses a recorded decision appends to the spec
- [ ] commit (the repo's precedent is one fix commit, clusters in the body)
- [ ] keep the report for step 5 and the PR B body

## Step 5. Deep-review guide

`/code-guide fix/[topic]-review-fixes`, file
`docs/[YYYY-MM]-[topic]-deep-review-guide.html`, same topic slug so the pair
sorts together.

- [ ] chapters from the report: the shape the defects share, then each cluster
      as defect then fix, then what stayed open and why
- [ ] labs only where a finding changed decision logic; a chapters-only guide is
      a legitimate answer
- [ ] the same five verifications as step 2

## Step 6. PR B

Create directly (the PR skill always bases on the default branch):

    gh pr create --base [feature-branch] --head fix/[topic]-review-fixes \
      --title "..." --body-file <file>

Body checklist:

- [ ] opens with "Stacked on #A. Base is `[feature-branch]`"
- [ ] one section per defect cluster: what would have gone wrong, why the fix
      lands where it does, which test now pins it
- [ ] Tests, Verification, Residuals carried forward, and a "read closely" note
      for anything reconstructed or unverifiable
- [ ] names the guide file and both decision docs
- [ ] no attribution footer

Merge order: PR A first; confirm PR B's base reads `main` before merging it.

## Wrap-up after both merges

- [ ] `git worktree remove ../[short-name]`, delete the local branches
- [ ] delete this file
- [ ] not in this plan: the app release (`/release [version]`)

## Progress

- [ ] 1. implementation
- [ ] 2. code guide
- [ ] 3. PR A
- [ ] 4. deep review and fixes
- [ ] 5. deep-review guide
- [ ] 6. PR B
