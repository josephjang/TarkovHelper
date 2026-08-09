---
name: release
description: Publish a TarkovHelper CalVer release through guarded preflight, version bump, atomic tag push, GitHub Actions monitoring, curated notes, asset verification, and update.xml activation. Use only when the user explicitly invokes $release or requests a release with a concrete version; do not use for ordinary release questions, planning, or unrelated version edits.
---

# Release

Require an explicit release version. If the user did not provide one, stop and
ask for it before changing repository or remote state.

Read [references/workflow.md](references/workflow.md) completely. Substitute the
provided version for `<version>` and follow every gate in order. Stop on any
failed preflight or workflow check. Do not skip ahead to `update.xml`.
