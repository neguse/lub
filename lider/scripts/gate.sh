#!/usr/bin/env bash
# lider の検査: lider の player を build し、runtime テスト (tests/<name>/) とエディタの core のテスト (tests/editor/) を走らせる。
# 先に lub を build しておく (scripts/build-release.sh)。要 cargo と dotnet。
# env: JOBS 同時に走らせる runtime テストの数 (既定 4)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
"$root/scripts/build-player.sh"
logs="$(mktemp -d)"
trap 'rm -rf "$logs"' EXIT
names=()
for test_dir in "$root"/tests/*/; do
  name="$(basename "$test_dir")"
  [ "$name" = editor ] || names+=("$name")
done
printf '%s\n' "${names[@]}" | xargs -P "${JOBS:-4}" -I{} sh -c "\"$root/scripts/test.sh\" {} >\"$logs/{}.log\" 2>&1 || true"
status=0
for name in "${names[@]}"; do
  if grep -qE "^(ok|skip) $name" "$logs/$name.log"; then
    grep -E "^(ok|skip) $name" "$logs/$name.log"
  else
    echo "FAILED $name"
    sed 's/^/    /' "$logs/$name.log"
    status=1
  fi
done
"$root/scripts/test-editor.sh" || status=1
exit "$status"
