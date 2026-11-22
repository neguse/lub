#!/usr/bin/env bash
# lider checks: builds the lider player and runs the runtime tests (tests/<name>/) and the editor core tests (tests/editor/).
# Build lub first (scripts/build-release.sh). Requires cargo and dotnet.
# env: JOBS number of runtime tests to run in parallel (default 4)
#   LIDER_SHARD k/n  runs only the k-th of n parts of the runtime tests (default 1/1; CI splits them into parallel jobs). The editor core tests run in the 1st part
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
"$root/scripts/build-player.sh"
logs="$(mktemp -d)"
trap 'rm -rf "$logs"' EXIT
shard="${LIDER_SHARD:-1/1}"
shard_k="${shard%/*}"
shard_n="${shard#*/}"
names=()
i=0
for test_dir in "$root"/tests/*/; do
  name="$(basename "$test_dir")"
  [ "$name" = editor ] && continue
  if (( i % shard_n == shard_k - 1 )); then
    names+=("$name")
  fi
  i=$((i + 1))
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
if [ "$shard_k" = 1 ]; then
  "$root/scripts/test-editor.sh" || status=1
fi
exit "$status"
