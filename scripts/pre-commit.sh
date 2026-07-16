#!/usr/bin/env bash
# Fast local gate for commits: format (changed files only) and whitespace
# checks. Seconds, not minutes — the merge gate is PR CI (linux / windows /
# web workflows). scripts/pre-push.sh is the manual full local equivalent.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "${1:-}" in
  -h|--help)
    cat <<'EOF'
Usage: scripts/pre-commit.sh

Runs the fast local pre-commit gate:
  format (changed files only) and whitespace checks.

Builds and tests run in PR CI (linux / windows / web workflows);
scripts/pre-push.sh is the manual full local equivalent.
EOF
    exit 0
    ;;
  "")
    ;;
  *)
    echo "unknown arg: $1" >&2
    exit 2
    ;;
esac

run() {
  echo
  echo "==> $*"
  "$@"
}

cleanup_files=()
cleanup() {
  if [[ ${#cleanup_files[@]} -gt 0 ]]; then
    rm -f "${cleanup_files[@]}"
  fi
}
trap cleanup EXIT INT TERM

format_before="$(mktemp)"
format_after="$(mktemp)"
cleanup_files+=("$format_before" "$format_after")
git diff --binary >"$format_before"
git diff --cached --binary >>"$format_before"
run scripts/format.sh --changed
git diff --binary >"$format_after"
git diff --cached --binary >>"$format_after"
if ! cmp -s "$format_before" "$format_after"; then
  echo
  echo "Formatter changed files. Review/stage the formatted changes, then commit again." >&2
  git status --short >&2
  exit 1
fi
run git diff --check
run git diff --cached --check

echo
echo "pre-commit gate OK (build/test gate: PR CI / scripts/pre-push.sh (手動))"
