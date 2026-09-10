# Deliver Workflow

Take an approved `docs/decisions/<name>.md` and `<name>.spec.md` pair and land
it: a plan, the work in reviewable slices, two guides, and two stacked PRs.

The shape came out of phase 4 of the EFT 1.1 roadmap (PRs #55 and #57, the
`quest-loyalty-gating` pair). Where a rule below looks oddly specific, it is
because something cost time there.

## 0. Preconditions and the plan file

1. Confirm the pair exists and is approved. The spec's **Files touched** is the
   implementation checklist and its **Verification** section holds the commands.
   Facts the spec already verified (row counts, live probes, bounds) are not
   re-derived: they were checked once, with evidence, and re-deriving them is
   how a plan drifts from the decision it implements.
2. Write `PLAN-<topic>.md` at the repo root from
   [plan-template.md](plan-template.md). It is **never committed** (the commit
   workflow stages by explicit path) and is deleted at the end.
3. **Record the baseline into the plan before the first edit:**

   ```sh
   dotnet build TarkovHelper.sln          # expect 0 warnings, 0 errors
   dotnet test --no-build --filter "Category!=E2E"
   ```

   Write the pass count down. Every later count is reported as a delta from it.
4. Branch: `<type>/<topic>` in kebab-case from wherever the decision docs live,
   so the docs merge in the same PR as the work.

Do not ask for approval between steps once the plan is written. Phase
boundaries are checkpoints, not handoffs. Stop only for a genuine blocker:
a design problem that invalidates the plan, an ambiguity the docs cannot
settle, or a failing test that exposes a flaw the plan did not anticipate.

## 1. Implementation

Work in the spec's Design order. **One slice per commit, each with its own
tests**, so a review can bisect and the commit body can say why.

For the defect the phase exists to remove:

1. Write the tests first, naming the behaviour that is wrong today.
2. Run them and **confirm they fail for the expected reason.** If they pass,
   they do not reproduce the defect: revise before continuing.
3. Fix the root cause.
4. Run them again, and put the red-then-green fact in the commit body.

Ripple discipline, which is where the real cost hides:

- A new positional value on a record is found by the compiler. A new
  **reflection-driven** guard is not: suites that enumerate constructor
  parameters, key arrays or events will either break or, worse, pass
  vacuously. Search for them and make each one cover the new value for real.
- When a change alters what a fixture query returns, fix the fixture in the
  same commit. A test-data helper that silently selects a different row is a
  regression with no error message.
- Run the full non-E2E suite after every slice, not only at the end.

**Exit criteria before step 2** (all in the plan as checkboxes):

- Debug and Release build clean, zero warnings
- non-E2E suite green, count above the baseline by the new tests
- E2E green on a clean desktop slot, or failing only in cases proven
  pre-existing (see Traps)
- self-review of the whole diff against the global checklist, reading each
  edited function whole rather than each hunk
- the spec's Files touched matches the diff; any divergence is **appended to
  the spec's Technical Decisions in this PR**, never left silent
- working tree clean, every slice committed

## 2. Code guide

Invoke `/code-guide <branch>` and follow its own workflow. Two things belong to
this workflow rather than that one:

- **Do it after step 3 if you want the PR number**, or accept that you will
  amend. See Traps.
- The guide is written against the branch diff, the decision docs, and the
  previous guide in the same series. Reference the earlier part in the lede.

## 3. PR A

`gh pr create --base main --head <branch>`. Check for an existing PR first: a
push link sometimes creates one.

Body shape (the repo's model is PR #50):

- first paragraph names both decision docs as what this PR implements and
  merges, and says plainly if it publishes no data
- **Why**: the defect in numbers, not adjectives
- **What changed**: one bullet per slice
- **Divergences from the approved spec**, each with what it fixes
- **Tests**: count before and after, and the fail-first evidence quoted
- **Verification**: the exact commands and their output
- **Residuals**: what is knowingly left, including anything the manual check
  found and anything you could not explain

No attribution footer. Wait for CI before moving on.

## 4. Deep review

**A fresh session, a separate worktree, a stacked branch.** Not this session.
The review's value is that it did not make the assumptions it is checking, and
a session that just wrote the code cannot supply that.

```sh
git worktree add -b fix/<topic>-review-fixes ../<short-name> <feature-branch>
```

Short worktree names: icon asset paths overflow MAX_PATH with a long prefix.

Start a fresh agent session in that directory and run
`/deep-review main...<feature-branch>`. The ref range is what makes the driver
review the PR's diff rather than an empty working tree. Expect the order of an
hour, and answer its steering questions as they come.

After the report:

- read every Fixed, Refuted and Needs-your-decision item
- **read the production diff yourself before committing it.** Do not take a
  summary on faith, especially where it rewrites code this session wrote
- re-run build, both suites, and any guide checkers and lab drivers, since the
  review may have edited the guides
- a fix that reverses a recorded decision appends to the spec's Technical
  Decisions in the same PR
- commit. The repo's precedent (PR #51) is **one fix commit** plus the guide
  commit, with the clusters enumerated in the body. Splitting an interdependent
  refactor into per-cluster commits that do not build is worse than one commit
  that does

## 5. Deep-review guide

`/code-guide <fix-branch>`, into
`docs/YYYY-MM-<same-topic-slug>-deep-review-guide.html` so the pair sorts
together. The lede says it continues part 1.

Labs only where a finding changed decision logic: a boundary, an order, a null
policy. If none qualifies, say so and ship a chapters-only guide rather than
inventing a decorative widget.

Give the last chapter to **what stayed open**: findings refuted with their
proof, and any decision that traded a mechanical guarantee for a process one.
Those are what a reader most needs and least gets.

## 6. PR B

The `/create-pr` skill always bases on the default branch, so create this one
directly:

```sh
gh pr create --base <feature-branch> --head fix/<topic>-review-fixes \
  --title "..." --body-file <file>
```

so the diff is only the fixes. Body shape (the repo's model is PR #51):

- opens with "Stacked on #A. Base is `<feature-branch>`"
- one section per defect cluster: what would have gone wrong, why the fix lands
  where it does, which test now pins it
- Tests, Verification, Residuals carried forward
- a **read closely** note for anything reconstructed, judgement-called, or
  unverifiable
- names the guide file and both decision docs

Merge order: PR A first. GitHub retargets PR B to `main` when the base branch
is deleted; confirm the base reads `main` before merging PR B.

## 7. Wrap-up

- `git worktree remove ../<short-name>`, delete the local branches
- delete `PLAN-<topic>.md`
- the release is a separate decision and a separate skill (`/release`)

## Traps

Each of these cost time once.

**PR numbers.** A guide's eyebrow, its approval snippet and the `docs/README.md`
entry all cite the PR number, and the number is not known until the PR exists.
Either create a draft PR before writing the guide, or write it and expect to
amend all three afterwards. Do not guess: guessing produced a guide citing a PR
that turned out to be someone else's.

**Blaming your own change for a pre-existing failure.** When a test fails,
check it against `main` in a throwaway worktree before investigating your diff.
A deterministic failure on `main` is a separate defect that deserves its own
commit and its own explanation in the PR body, not a debugging detour.

**Flake hunting with filtered output.** Never pipe a test run to a summary line
while chasing an intermittent failure. Use `--logger "trx;LogFileName=..."`, or
the one failure that does not reproduce becomes a failure you cannot name.

**Leading slashes through a terminal bridge.** Sending `/deep-review ...` into
an agent TUI through MSYS-based tooling path-mangles it to
`C:/Program Files/Git/deep-review`. Send it from PowerShell, or expect the
receiving agent to have to recognise and recover it.

**Session limits on long runs.** A deep review can be killed by a usage cap
mid-run. It checkpoints into the repo's git dir and a fresh driver resumes from
it, so re-invoke with the same arguments rather than starting over.

**Manual verification steps that cannot be reached.** A spec's manual check may
name a quest or a state that some other gate makes unreachable on a fresh
profile. Verify the manual script against the data when writing the spec, and
when one turns out to be unreachable, record that in the PR body rather than
quietly skipping it.
