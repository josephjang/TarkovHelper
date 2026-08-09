# TarkovHelper Release Workflow

Use this workflow only after the user explicitly requests a release and
provides a version. `<version>` must match `^\d{4}\.\d{1,2}\.\d+$`, for example
`2026.8.0`.

Pushing a `v*` tag starts `.github/workflows/release.yml`, which builds, tests,
packages, and publishes `TarkovHelper.zip`. This workflow owns the version bump,
atomic tag push, release-note curation, verification, and the final
`update.xml` activation. See
`docs/decisions/feature-fork-release-process.md` for the design rationale.

## Preflight

Stop if any check fails.

1. Validate the version against the CalVer pattern.
2. Confirm `v<version>` does not exist locally or on origin:

   ```powershell
   git tag -l v<version>
   git ls-remote --tags origin v<version>
   ```

3. Confirm the current branch is `main` and the working tree is clean, then
   run `git pull origin main`.
4. Run `gh auth status`. This repository has `origin=josephjang` and
   `upstream=Zeliper`, so pass `-R josephjang/TarkovHelper` to every `gh`
   command.
5. Pass the local build gate before creating a tag:

   ```powershell
   dotnet build TarkovHelper.sln -c Release
   ```

## Publish

1. Update only `<Version>` in `TarkovHelper/TarkovHelper.csproj` to
   `<version>`. Do not update `update.xml` yet. The SDK derives assembly and
   file versions from `<Version>`.
2. Commit the project version:

   ```powershell
   git commit -am "chore(release): bump version to <version>"
   ```

3. Create and atomically push the one release tag with `main`:

   ```powershell
   git tag v<version>
   git push --atomic origin main v<version>
   ```

   Never run `git push --tags`. Local legacy upstream tags must not be pushed.
4. Find and watch the triggered workflow:

   ```powershell
   gh run list -R josephjang/TarkovHelper --workflow release.yml --limit 1 --json databaseId
   gh run watch <run-id> --exit-status -R josephjang/TarkovHelper
   ```

5. After the workflow succeeds, curate the release notes:
   - Find the previous tag with
     `git describe --tags --abbrev=0 "v<version>^"`.
   - Review every commit from the previous tag through `v<version>`. Classify
     each as represented in the notes or explicitly excluded.
   - Exclude documentation and decision records, CI and test-only work, and
     TarkovDBEditor-only changes because the editor is not shipped. Include DB
     content changes because `tarkov_data.db` is shipped.
   - Map commits to PRs with
     `gh api repos/josephjang/TarkovHelper/commits/{sha}/pulls`. For commits
     without a PR, resolve the author with
     `gh api repos/josephjang/TarkovHelper/commits/{sha} --jq '.author.login'`.
   - Verify every claim against its commit and decision documentation. Do not
     describe behavior more favorably than the implementation supports.
   - Write English, Korean, and Japanese notes to a temporary scratch file,
     then run:

     ```powershell
     gh release edit v<version> -R josephjang/TarkovHelper --notes-file <notes-file>
     ```

6. Verify the published assets:

   ```powershell
   gh release view v<version> -R josephjang/TarkovHelper --json assets
   ```

   The result must contain `TarkovHelper.zip`.
7. Only after asset verification, update `update.xml`:
   - Set `<version>` to `<version>`.
   - Set `<url>` to
     `https://github.com/josephjang/TarkovHelper/releases/download/v<version>/TarkovHelper.zip`.
   - Validate the XML and version-to-URL match:

     ```powershell
     dotnet test --filter UpdateXmlTests
     ```

   - Commit and push the activation:

     ```powershell
     git commit -am "chore(release): point update.xml at v<version>"
     git push origin main
     ```

This ordering prevents clients from seeing a download URL before the asset
exists.

## Release Notes

Write from the user's perspective instead of copying commit subjects.

- Put the highest-impact feature first.
- Group features under bold titles and collect fixes under `Fixes`, `수정`, or
  `修正`.
- Describe observable symptoms and outcomes.
- Attribute PRs as `(#N by @user)` in English, `(#N, @user)` in Korean, and
  `(#N、@user)` in Japanese.
- For upstream commits without a PR, use `(upstream fix by @user)`,
  `(업스트림 수정, @user)`, or `(アップストリーム修正、@user)`.
- Follow the root `CLAUDE.md` writing conventions. In Japanese, use the EFT
  community terms `ハイドアウト`, `Scavカルマ`, and `陣営`.

Use this structure:

```markdown
## What's Changed / 변경 사항 / 変更内容

### English

**[Feature title]** (#N by @user)

[Optional one-line introduction]

- [User-visible outcome]

**Fixes**

- [Symptom-focused fix] (#N by @user)

### 한국어

[Same structure with `(#N, @user)` attribution]

### 日本語

[Same structure with `(#N、@user)` attribution]

---
**Full Changelog**: https://github.com/josephjang/TarkovHelper/compare/<previous-tag>...v<version>
```

Use the `v2026.7.0` release notes as a style reference.

## Workflow Failure Recovery

If the release workflow fails before `update.xml` is changed, clients have not
seen the failed version.

1. Delete a partially created GitHub release if one exists:

   ```powershell
   gh release delete v<version> -y -R josephjang/TarkovHelper
   ```

2. Delete the local and origin tag:

   ```powershell
   git tag -d v<version>
   git push origin :refs/tags/v<version>
   ```

3. Fix the cause on `main` with a normal commit, then restart at the atomic tag
   push using the same version.

## Additional Checks

- To inspect a local package, run `./build/Create-ReleasePackage.ps1`. The
  output is `artifacts/TarkovHelper.zip`.
- If `gh` is not on `PATH`, check `C:\Program Files\GitHub CLI\gh.exe`.
- Never activate `update.xml` until `TarkovHelper.zip` is visible in the GitHub
  release assets.
