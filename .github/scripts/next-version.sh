#!/usr/bin/env bash
# Derive the next semantic version from Conventional Commits since the latest v* tag.
# Prints the version, or nothing when no release is warranted.
# Usage: next-version.sh <fallback-version> | --self-test
set -euo pipefail

# 0 = no release, 1 = patch, 2 = minor, 3 = major
bump_of() {
  local header="$1" type
  # Drop leading gitmoji/punctuation so the Conventional Commit prefix comes first.
  header="$(printf '%s' "$header" | sed 's/^[^A-Za-z]*//')"
  case "$header" in
    Revert\ *) echo 1; return ;;
    Merge\ *) echo 0; return ;;
  esac
  if [[ "$header" =~ ^([A-Za-z]+)(\([^\)]*\))?(!)?: ]]; then
    type="$(printf '%s' "${BASH_REMATCH[1]}" | tr '[:upper:]' '[:lower:]')"
    if [ -n "${BASH_REMATCH[3]:-}" ]; then echo 3; return; fi
    case "$type" in
      feat) echo 2 ;;
      fix|perf|refactor|revert) echo 1 ;;
      *) echo 0 ;;
    esac
    return
  fi
  echo 0
}

has_breaking_body() {
  grep -qiE '^BREAKING[ -]CHANGE:'
}

next_version() {
  local fallback="$1" latest range max=0 subject bump
  latest="$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)"
  if [ -z "$latest" ]; then
    printf '%s\n' "$fallback"
    return
  fi
  range="$latest..HEAD"
  if git log --format=%b "$range" | has_breaking_body; then
    max=3
  fi
  while IFS= read -r subject; do
    [ -n "$subject" ] || continue
    bump="$(bump_of "$subject")"
    if [ "$bump" -gt "$max" ]; then
      max="$bump"
    fi
  done < <(git log --format=%s "$range")
  if [ "$max" -eq 0 ]; then
    return
  fi
  local base major minor patch
  base="${latest#v}"
  IFS=. read -r major minor patch <<< "$base"
  case "$max" in
    3) major=$((major + 1)); minor=0; patch=0 ;;
    2) minor=$((minor + 1)); patch=0 ;;
    1) patch=$((patch + 1)) ;;
  esac
  printf '%s.%s.%s\n' "$major" "$minor" "$patch"
}

self_test() {
  local failures=0 actual
  check() {
    actual="$(bump_of "$1")"
    if [ "$actual" != "$2" ]; then
      echo "FAIL: '$1' => $actual, expected $2" >&2
      failures=$((failures + 1))
    fi
  }
  check "✨ feat(api): add search" 2
  check "🐛 fix: handle 409" 1
  check "💥 feat!: drop v1" 3
  check "perf: faster" 1
  check "docs: update readme" 0
  check "chore(release): v0.2.0" 0
  check "Merge pull request #3" 0
  check 'Revert "feat: x"' 1
  check "no conventional prefix" 0
  check "update fix: not a prefix" 0

  if printf 'body\n\nBREAKING CHANGE: removed x\n' | has_breaking_body; then :; else
    echo "FAIL: breaking body not detected" >&2
    failures=$((failures + 1))
  fi
  if printf 'plain body\n' | has_breaking_body; then
    echo "FAIL: plain body treated as breaking" >&2
    failures=$((failures + 1))
  fi

  if [ "$failures" -eq 0 ]; then
    echo "self-test ok"
  else
    exit 1
  fi
}

if [ "${1:-}" = "--self-test" ]; then
  self_test
  exit 0
fi

if [ $# -lt 1 ]; then
  echo "usage: $0 <fallback-version> | --self-test" >&2
  exit 2
fi

next_version "$1"
