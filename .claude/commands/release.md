# Release Command

Release version `$ARGUMENTS`.

Read `.agents/skills/release/references/workflow.md` completely, substitute
`$ARGUMENTS` for `<version>`, and follow every gate in order.

If `$ARGUMENTS` is empty or does not match `^\d{4}\.\d{1,2}\.\d+$`, stop before
changing local or remote state and ask for a valid CalVer version.
