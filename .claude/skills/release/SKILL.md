---
name: release
description: Prepare a pull request, a version bump or a release of Rony.Net. Use for `/release pr`, `/release bump X.Y.Z` and `/release publish`, and whenever a hand-over needs a PR title and description. Checks everything, then gives the user the commands to run; it never commits, pushes, tags or publishes itself.
argument-hint: "pr | bump X.Y.Z | publish"
---

# Release

Mode: `$ARGUMENTS`

You prepare and check; the user runs every command that commits, pushes, tags or publishes (see "Rules" in
`CLAUDE.md`). Give each command in its own `bash` block. If a check fails, stop, say what failed and how to fix
it, and give no commands after that point.

The release itself is done by `.github/workflows/release.yml` when a lowercase `v*` tag is pushed. It fails
unless `<Version>` in `Directory.Build.props` equals the tag, so **the bump must be merged into `main` before the
tag is pushed**: that order is the reason this skill exists.

## `pr`: hand over a branch as a pull request
1. `git status`, `git log --oneline origin/main..HEAD` and `git diff origin/main...HEAD --stat`: know the whole
   branch, not only the last commit. Uncommitted work gets its commit command first.
2. Branch name follows `feature/<name>` or `fix/<name>`; otherwise give the rename command.
3. `dotnet build` (0 warnings) and `dotnet test` (full suite). Use the real numbers from this run.
4. A branch with several steps needs a `reviewer` pass over the whole branch and the user's approval before any
   PR text.
5. Hand over in the order `CLAUDE.md` gives: rename (if needed), commit command(s), push command, then the PR
   title and a description filled in from `templates/pr.md` in a `markdown` block.

## `bump X.Y.Z`: prepare the version for a release
Run on a branch named `fix/release-X.Y.Z-version` or as the last step of the feature branch being released.
1. Choose the number with the user if it is not given: patch for fixes only, minor for new features, major only
   for a breaking change (which needs the user's explicit decision).
2. Edit, and nothing else:
   - `Directory.Build.props`: `<Version>X.Y.Z</Version>`.
   - `CHANGELOG.md`: the top heading is `## X.Y.Z` and describes everything merged since the last release
     (compare with `git log --oneline v<last>..origin/main`).
   - `<PackageReleaseNotes>` in `src/Rony/Rony.csproj`: one sentence on what is new. In each framework package
     (`src/Rony.Net.*/*.csproj`): its own changes, or "Released together with Rony.Net X.Y.Z; no changes of its
     own." followed by the changelog link.
   - `CLAUDE.md`: the test count in the `dotnet test` line ("N tests at X.Y.Z").
   - `BACKLOG.md`: the header sentence that says which items shipped in which version.
   - `docs/releases/X.Y.Z.md` (new): the release description, from `templates/release-notes.md`, in the style of
     the previous file in `docs/releases/`. `release.yml` publishes it as the GitHub release body (GitHub's list
     of merged pull requests is appended automatically) and refuses to release without it. Check every code
     example against the README, wiki or samples, and show the description to the user before the commit.
3. `scripts/check_release.sh X.Y.Z` must pass, then `dotnet build` and `dotnet test`.
4. Give the commit command ("Bump the version to X.Y.Z", including `docs/releases/X.Y.Z.md`), the push command and
   the PR text.

## `publish`: tag a merged bump
1. `git fetch origin --tags`, then `scripts/check_release.sh --publish`. It checks that `origin/main` carries the
   version, that the changelog has its heading, that `docs/releases/X.Y.Z.md` exists, and that the tag does not
   exist yet.
2. `gh pr checks` or `gh run list --branch main --limit 1`: CI on `main` is green.
   `gh secret list`: `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` exist (the `image` job of `release.yml` pushes the
   Docker image to ghcr.io and Docker Hub and fails without them; NuGet is already published by then, so a failed
   `image` job is re-run with `gh run rerun <id> --failed` once the cause is fixed).
3. Give the commands, to be run in the main checkout (not a worktree):

   ```bash
   git switch main && git pull --ff-only
   ```
   ```bash
   git tag -m "Rony.Net X.Y.Z" vX.Y.Z && git push origin vX.Y.Z
   ```
   The user's git signs tags (`tag.gpgsign`), and a signed tag needs a message: without `-m` git opens an editor.
4. After the user has pushed the tag: `gh run list --workflow release.yml --limit 1` until it has finished, then
   `gh release view vX.Y.Z`: the body must start with the description from `docs/releases/X.Y.Z.md`. If a
   release was published without it, write the file and, only when the user asks, update the release with
   `gh release edit vX.Y.Z --notes-file <file with the description and the generated list>`. nuget.org lists a new version only after validation, often 10–30 minutes later;
   check `https://api.nuget.org/v3-flatcontainer/<lowercase id>/index.json` for each of the seven packages.
   The `image` job must be green too. After the first image release the package `ghcr.io/archofthings/rony` is
   private: tell the user to make it public once in its package settings on GitHub.
5. If the release run failed, read its log (`gh run view <id> --log-failed`) and fix the cause on a branch. A tag
   that points at the wrong commit is deleted and pushed again by the user only; say so and give the commands.

## A new package
Its ID must start with `Rony.Net` (the Trusted Publishing policy uses the glob `Rony.Net*`), it takes its version
from `Directory.Build.props`, and it is added to the package lists in `CLAUDE.md`, `Directory.Build.props`,
`release.yml` and the nuget.org check above.
