#!/usr/bin/env bash
# Checks that the repository is ready for a release. Read-only.
#   check_release.sh X.Y.Z      after `/release bump`: the working tree carries version X.Y.Z everywhere
#   check_release.sh --publish  before the tag: origin/main carries the version and the tag is still free
set -u
cd "$(git rev-parse --show-toplevel)" || exit 2

failed=0
ok()   { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failed=1; }

if [ "${1:-}" = "--publish" ]; then
  ref="origin/main"
  show() { git show "$ref:$1" 2>/dev/null; }
  version=$(show Directory.Build.props | sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p')
  projects=$(git ls-tree -r --name-only "$ref" -- src | grep '\.csproj$')
elif [[ "${1:-}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  ref="working tree"
  show() { cat "$1" 2>/dev/null; }
  version=$1
  projects=$(ls src/*/*.csproj)
else
  echo "usage: check_release.sh X.Y.Z | --publish" >&2
  exit 2
fi
echo "Checking version $version in $ref"

props=$(show Directory.Build.props | sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p')
[ "$props" = "$version" ] && ok "Directory.Build.props has $version" \
  || fail "Directory.Build.props has '$props', expected $version"

for p in $projects; do
  show "$p" | grep -q '<Version>' && fail "$p sets its own <Version>; remove it"
done
[ $failed -eq 0 ] && ok "no project sets its own <Version>"

heading=$(show CHANGELOG.md | grep -m1 '^## ')
[ "$heading" = "## $version" ] && ok "CHANGELOG.md starts with '## $version'" \
  || fail "CHANGELOG.md starts with '$heading', expected '## $version'"

for p in $projects; do
  notes=$(show "$p" | grep '<PackageReleaseNotes>')
  stale=$(echo "$notes" | grep -oE 'Rony\.Net [0-9]+\.[0-9]+\.[0-9]+' | grep -v "Rony.Net $version")
  [ -n "$stale" ] && fail "$p: release notes still say '$stale'"
done

[ -n "$(show "docs/releases/$version.md")" ] && ok "docs/releases/$version.md has the release description" \
  || fail "docs/releases/$version.md is missing or empty (template: .claude/skills/release/templates/release-notes.md)"

show CLAUDE.md | grep -q "tests at $version" && ok "CLAUDE.md test count is for $version" \
  || fail "CLAUDE.md: the 'dotnet test' line does not say 'tests at $version'"

if git ls-remote --exit-code --tags origin "refs/tags/v$version" >/dev/null 2>&1; then
  fail "tag v$version already exists on origin"
else
  ok "tag v$version is free on origin"
fi

if [ "$ref" = "origin/main" ]; then
  last=$(git describe --tags --abbrev=0 "$ref" 2>/dev/null)
  [ "$last" = "v$version" ] && fail "origin/main is already tagged $last: bump the version first (/release bump)"
  echo "Commits since ${last:-the start}: $(git rev-list --count "${last:+$last..}$ref")"
fi

[ $failed -eq 0 ] && echo "Ready." || echo "Not ready."
exit $failed
