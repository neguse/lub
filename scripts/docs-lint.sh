#!/usr/bin/env bash
# Docs lint: mechanical checks for the documentation policy (docs/README.md).
#   1. Every docs/*.md is listed in the docs/README.md index.
#   2. Links in docs/README.md resolve to existing files.
#   3. docs/log/*.md are named YYYY-MM-DD-<slug>.md.
#   4. docs/log/*.md carry a leading "> 記録:" banner.
# Runs in the commit hook (scripts/pre-commit.sh) and in CI via
# scripts/native-gate.sh. Whether a doc's content matches the
# implementation cannot be checked here; that stays a review concern.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "${1:-}" in
  -h|--help)
    cat <<'EOF'
Usage: scripts/docs-lint.sh

Checks the mechanical part of the documentation policy (docs/README.md):
index completeness, index link resolution, and docs/log/ naming + banner.
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

fail=0
err() {
  echo "docs-lint: $*" >&2
  fail=1
}

index="docs/README.md"

# 1. Every docs/*.md (top level, README itself excluded) appears in the index.
for f in docs/*.md; do
  base="$(basename "$f")"
  [[ "$base" == "README.md" ]] && continue
  if ! grep -q "]($base)" "$index"; then
    err "$f is not listed in $index"
  fi
done

# 2. Relative links in the index resolve.
while IFS= read -r target; do
  [[ "$target" == http* || "$target" == "#"* ]] && continue
  if [[ ! -e "docs/$target" ]]; then
    err "$index links to missing file: $target"
  fi
done < <(grep -o '](\([^)]*\))' "$index" | sed 's/^](//; s/)$//')

# 3+4. docs/log/ naming and banner.
for f in docs/log/*.md; do
  base="$(basename "$f")"
  if [[ ! "$base" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}-[a-z0-9-]+\.md$ ]]; then
    err "$f is not named YYYY-MM-DD-<slug>.md"
  fi
  if ! head -5 "$f" | grep -q "^> 記録:"; then
    err "$f has no leading '> 記録:' banner"
  fi
done

if [[ "$fail" -ne 0 ]]; then
  echo "docs-lint: policy is docs/README.md" >&2
  exit 1
fi
echo "docs-lint OK"
