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
  - Markdown links in the docs point to existing files
  - paths to docs/ written anywhere in the repo (text, code comments) exist
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
current_docs=(README.md AGENTS.md tcs/README.md web/README.md
  docs/*.md docs/manual/*.md)
for f in "${current_docs[@]}"; do
  [[ -f "$f" ]] || continue
  line="$(sed 's|\*\*/||g; s|/\*\*||g' "$f" | grep -n '\*\*' | head -1 || true)"
  if [[ -n "$line" ]]; then
    err "$f uses ** emphasis (line ${line%%:*}); current-state docs ban bold"
  fi
done

# Markdown links in the docs above must point to existing files.
# A target is relative to the file (leading "/" = repo root), as on GitHub.
# URLs, "#anchor"-only targets, code spans, and fenced code are skipped;
# the "#anchor" part of a target is not checked. As in CommonMark, text
# in parentheses is a link only when the target is followed by nothing or by
# a quoted title, so "[0, 1](WebGPU / D3D)" is not a link.
link_docs=()
for f in "${current_docs[@]}"; do
  [[ -f "$f" ]] && link_docs+=("$f")
done
while IFS=$'\t' read -r f ln target; do
  path="${target%%#*}"
  if [[ "$path" == /* ]]; then
    resolved="${path#/}"
  else
    resolved="$(dirname "$f")/$path"
  fi
  if [[ ! -e "$resolved" ]]; then
    err "$f:$ln: link target $target does not exist ($resolved)"
  fi
done < <(awk -v q="'" '
  BEGIN { title_re = "^(\"[^\"]*\"|" q "[^" q "]*" q ")$" }
  FNR == 1 { fence = 0 }
  /^[ \t]*(```|~~~)/ { fence = !fence; next }
  fence { next }
  {
    line = $0
    gsub(/`[^`]*`/, "", line)
    while (match(line, /\]\([^)]*\)/)) {
      t = substr(line, RSTART + 2, RLENGTH - 3)
      line = substr(line, RSTART + RLENGTH)
      sub(/^[ \t]+/, "", t)
      sub(/[ \t]+$/, "", t)
      if (t ~ /^</) {
        sub(/^</, "", t)
        sub(/>.*$/, "", t)
      } else if (t ~ /[ \t]/) {
        title = t
        sub(/^[^ \t]*[ \t]+/, "", title)
        if (title !~ title_re) continue
        sub(/[ \t].*$/, "", t)
      }
      if (t == "" || t ~ /^#/ || t ~ /^[A-Za-z][A-Za-z0-9+.-]*:/) continue
      print FILENAME "\t" FNR "\t" t
    }
  }' "${link_docs[@]}")

# Paths to docs/ written in any tracked file (backticks, plain text, code
# comments) must exist. A path is looked up from the file's directory up to
# the repo root, so a path that web/README.md writes relative to web/
# (web/docs/main.ts) passes.
# Paths into other directories are not checked: docs mention example paths
# and install-relative paths (the tcs/tcs next to the executable).
path_exists_from() {
  local dir="$1" path="$2"
  while :; do
    if [[ "$dir" == "." ]]; then
      [[ -e "$path" ]] && return 0
      return 1
    fi
    [[ -e "$dir/$path" ]] && return 0
    dir="$(dirname "$dir")"
  done
}
prev=""
while IFS= read -r m; do
  f="${m%%:*}"
  rest="${m#*:}"
  ln="${rest%%:*}"
  path="docs/${rest#*:*docs/}"
  # A Markdown link repeats the path in its text and target; report it once.
  [[ "$f:$ln:$path" == "$prev" ]] && continue
  prev="$f:$ln:$path"
  if ! path_exists_from "$(dirname "$f")" "$path"; then
    err "$f:$ln: $path does not exist"
  fi
done < <(git grep -nIoE '(^|[^A-Za-z0-9_./-])docs/[A-Za-z0-9_./-]*[A-Za-z0-9_]' \
  -- ':(exclude)third_party' || true)

if [[ "$fail" -ne 0 ]]; then
  echo "docs-lint: policy is docs/README.md" >&2
  exit 1
fi
echo "docs-lint OK"
