#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

# With no arguments, runs everything.
# --part <name> runs only one part (CI splits the parts into separate jobs run in parallel):
#   tcs-1, tcs-2 (and tests/tcs-analyzers), backends-1, backends-2, backends-3 are parts of dotnet test; scripts is the checks other than dotnet test.
# dotnet test is split by the first letter of the class name. The last part is all classes not in earlier parts, so every test is in exactly one part.
part=all
case "${1:-}" in
  "") ;;
  --part) part="${2:?usage: run-tests.sh [--part <name>]}" ;;
  *)
    echo "usage: run-tests.sh [--part <name>]" >&2
    exit 2
    ;;
esac
tcs_1=(A B C D E F G H I J K L)
backends_1=(A B C D E F G)
backends_2=(H I J K L M N)
# Classes directly under namespace $1 whose name starts with one of the letters from $2 on
filter_in() {
  local ns=$1 f="" l
  shift
  for l in "$@"; do f+="${f:+|}FullyQualifiedName~$ns.$l"; done
  printf '%s' "$f"
}
# Classes directly under namespace $1 whose name starts with none of the letters from $2 on
filter_out() {
  local ns=$1 f="" l
  shift
  for l in "$@"; do f+="${f:+&}FullyQualifiedName!~$ns.$l"; done
  printf '%s' "$f"
}

# Run dotnet tests (including the spec conformance sweep and corpus differential.
# The per-chapter report is written under build-tcs-tests/ — behavior changes show up as baseline diffs)
# Verification is comparison with .NET execution (TCS_DIFFERENTIAL, TCS_FUZZ) and judging C# acceptance with Roslyn (spec conformance)
export TCS_SPEC_CONFORMANCE=1
export TCS_SPEC_REPORT="$ROOT/build-tcs-tests/spec-conformance-report.md"
export TCS_DIFFERENTIAL=1
export TCS_FUZZ=1
echo "Running dotnet tests (with spec conformance + differential, part: $part)..."
case "$part" in
  all) dotnet test "$ROOT/tcs.slnx" --verbosity minimal ;;
  tcs-1)
    dotnet test "$ROOT/tests/tcs" --verbosity minimal --filter "$(filter_in TinyCs.Tests "${tcs_1[@]}")"
    ;;
  tcs-2)
    # tests/tcs-analyzers is a single class, so it runs alongside the rest of tests/tcs. Build first so the same project is not built concurrently
    dotnet build "$ROOT/tests/tcs" --verbosity minimal
    dotnet build "$ROOT/tests/tcs-analyzers" --verbosity minimal
    dotnet test "$ROOT/tests/tcs-analyzers" --no-build --verbosity minimal &
    analyzers=$!
    dotnet test "$ROOT/tests/tcs" --no-build --verbosity minimal --filter "$(filter_out TinyCs.Tests "${tcs_1[@]}")"
    wait "$analyzers"
    ;;
  backends-1) dotnet test "$ROOT/tests/tcs-backends" --verbosity minimal --filter "$(filter_in TinyCs.Tcs2c.Tests "${backends_1[@]}")" ;;
  backends-2) dotnet test "$ROOT/tests/tcs-backends" --verbosity minimal --filter "$(filter_in TinyCs.Tcs2c.Tests "${backends_2[@]}")" ;;
  backends-3) dotnet test "$ROOT/tests/tcs-backends" --verbosity minimal --filter "$(filter_out TinyCs.Tcs2c.Tests "${backends_1[@]}" "${backends_2[@]}")" ;;
  scripts) ;;
  *)
    echo "unknown part: $part" >&2
    exit 2
    ;;
esac
if [ "$part" != all ] && [ "$part" != scripts ]; then
  echo "All tests passed (part: $part)."
  exit 0
fi

echo "Running tcs check on samples..."
sample_checks=(
  "samples/hello.cs"
  "samples/game.cs"
  "samples/inventory.cs"
  "samples/entity.cs"
  "samples/statemachine.cs"
  "samples/collision.cs"
)
# The .NET tcs (scripts/tcs.sh), built once for the checks below
LUB_TCS="$("$ROOT/scripts/build-tcs-dotnet.sh")"
export LUB_TCS
for sample in "${sample_checks[@]}"; do
  "$ROOT/scripts/tcs.sh" check "$SCRIPT_DIR/$sample"
done
"$ROOT/scripts/tcs.sh" \
  check "$SCRIPT_DIR/samples/host_api_game.cs" \
  --ref "$SCRIPT_DIR/samples/host_api_stub.cs"

# 2-backend digest match (IL→C backend, only where cc is available)
if command -v cc >/dev/null 2>&1 || command -v gcc >/dev/null 2>&1; then
  echo "Running the C-only checks..."
  bash "$ROOT/tests/tcs-backends/verify-digests.sh"
  bash "$ROOT/tests/tcs-backends/verify-gc.sh"
  bash "$ROOT/tests/tcs-backends/verify-game-core.sh"
  bash "$ROOT/tests/tcs-backends/verify-object-values.sh"
  bash "$ROOT/tests/tcs-backends/verify-host.sh"
  echo "Running tcsvm differential tests..."
  bash "$ROOT/tests/tcs-vm/run-tests.sh"
else
  echo "Skipping the C-only checks (no C compiler)."
fi

echo "Running analyzer demo build..."
set +e
analyzer_output="$(dotnet build "$ROOT/tests/tcs-analyzers-demo/tcs-analyzers-demo.csproj" --no-incremental 2>&1)"
analyzer_exit=$?
set -e
printf '%s\n' "$analyzer_output"
if [ "$analyzer_exit" -ne 0 ]; then
  exit "$analyzer_exit"
fi

count_diagnostic() {
  local id="$1"
  printf '%s\n' "$analyzer_output" \
    | awk -v id="$id" 'index($0, "warning " id ":") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }'
}

assert_contains() {
  local label="$1"
  local output="$2"
  local needle="$3"
  # With a pipe, pipefail would catch as a failure the SIGPIPE of printf after grep -q closes early
  if ! grep -Fq -- "$needle" <<<"$output"; then
    echo "Error: $label did not contain expected diagnostic text: $needle" >&2
    exit 1
  fi
}

assert_expected_diagnostic_texts() {
  local label="$1"
  local output="$2"
  for needle in \
      "StructMember" \
      "LocalFunctionStatement" \
      "TryStatement" \
      "ThrowStatement" \
      "ListPattern" \
      "System.IO.File.ReadAllText" \
      "Dictionary<K,V> cannot store null values"; do
    assert_contains "$label" "$output" "$needle"
  done
}

tcs1001_count="$(count_diagnostic TCS1001)"
tcs1002_count="$(count_diagnostic TCS1002)"
tcs1003_count="$(count_diagnostic TCS1003)"
if [ "$tcs1001_count" -ne 5 ] || [ "$tcs1002_count" -ne 1 ] || [ "$tcs1003_count" -ne 1 ]; then
  echo "Error: analyzer demo expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count / TCS1003 x$tcs1003_count" >&2
  exit 1
fi
assert_expected_diagnostic_texts "analyzer demo" "$analyzer_output"

echo "All tests passed."
