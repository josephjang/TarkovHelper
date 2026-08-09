# Commit Workflow

Use this workflow only when the user explicitly asks to create commits.

## Procedure

1. Read the active repository guidance, especially the `Commits & Branches`
   section in the root `CLAUDE.md`.
2. Inspect `git status`, the relevant diffs, and recent `git log` messages.
   Preserve unrelated user changes.
3. Verify the solution builds before the first commit:

   ```powershell
   dotnet build TarkovHelper.sln
   ```

4. Split the working-tree changes by feature or purpose. Create one commit per
   coherent group instead of one broad commit.
5. Stage each group by explicit path. Never use `git add -A`, `git add .`, or
   `git add -u`.
6. Review `git diff --cached` and run `git diff --cached --check` before each
   commit. Derive the message from the staged diff.
7. Use an English conventional commit message that matches recent repository
   scopes and style. Use an imperative subject of at most 72 characters and add
   a body explaining why when the change is non-trivial.
8. Do not add attribution footers such as `Generated with` or
   `Co-Authored-By`.
9. Commit only. Never push and never use destructive Git commands.
10. After all requested commits, report the created hashes and subjects, then
    show the remaining `git status`.

If there is nothing to commit, the build fails, or changes are too intermingled
to split safely, stop and report the condition instead of guessing.
