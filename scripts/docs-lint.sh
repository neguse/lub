#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "${1:-}" in
  -h|--help)
    cat <<'EOF'
Usage: scripts/docs-lint.sh
Check docs:
  - no bold emphasis
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

# Ban ** (bold) emphasis. "**/" and "/**" glob
# patterns inside code spans are not emphasis and are ignored.
current_docs=(README.md AGENTS.md CLAUDE.md web/README.md
  docs/*.md docs/manual/*.md)
for f in "${current_docs[@]}"; do
  [[ -f "$f" ]] || continue
  line="$(sed 's|\*\*/||g; s|/\*\*||g' "$f" | grep -n '\*\*' | head -1 || true)"
  if [[ -n "$line" ]]; then
    err "$f uses ** emphasis (line ${line%%:*}); current-state docs ban bold"
  fi
done

if [[ "$fail" -ne 0 ]]; then
  echo "docs-lint: policy is docs/README.md" >&2
  exit 1
fi
echo "docs-lint OK"
