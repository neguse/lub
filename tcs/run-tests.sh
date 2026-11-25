#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# With no arguments, runs everything.
# --part <name> runs only one part (CI splits the parts into separate jobs run in parallel):
#   transpiler-1, transpiler-2 (and Analyzers.Tests), tcs2c-1, tcs2c-2, tcs2c-3 are parts of dotnet test; scripts is the checks other than dotnet test.
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
transpiler_1=(A B C D E F G H I J K L)
tcs2c_1=(A B C D E F G)
tcs2c_2=(H I J K L M N)
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
# The per-chapter reports are also regenerated — behavior changes show up as report/baseline diffs)
# Verification is comparison with .NET execution (TCS_DIFFERENTIAL, TCS_FUZZ) and judging C# acceptance with Roslyn (spec conformance)
export TCS_SPEC_CONFORMANCE=1
export TCS_SPEC_REPORT="$SCRIPT_DIR/doc/spec-conformance-report.md"
export TCS_DIFFERENTIAL=1
export TCS_FUZZ=1
echo "Running dotnet tests (with spec conformance + differential, part: $part)..."
case "$part" in
  all) dotnet test "$SCRIPT_DIR" --verbosity minimal ;;
  transpiler-1)
    dotnet test "$SCRIPT_DIR/Transpiler.Tests" --verbosity minimal --filter "$(filter_in TinyCs.Tests "${transpiler_1[@]}")"
    ;;
  transpiler-2)
    # Analyzers.Tests is a single class, so it runs alongside the rest of Transpiler.Tests. Build first so the same project is not built concurrently
    dotnet build "$SCRIPT_DIR/Transpiler.Tests" --verbosity minimal
    dotnet build "$SCRIPT_DIR/TinyCs.Analyzers.Tests" --verbosity minimal
    dotnet test "$SCRIPT_DIR/TinyCs.Analyzers.Tests" --no-build --verbosity minimal &
    analyzers=$!
    dotnet test "$SCRIPT_DIR/Transpiler.Tests" --no-build --verbosity minimal --filter "$(filter_out TinyCs.Tests "${transpiler_1[@]}")"
    wait "$analyzers"
    ;;
  tcs2c-1) dotnet test "$SCRIPT_DIR/tcs2c.Tests" --verbosity minimal --filter "$(filter_in TinyCs.Tcs2c.Tests "${tcs2c_1[@]}")" ;;
  tcs2c-2) dotnet test "$SCRIPT_DIR/tcs2c.Tests" --verbosity minimal --filter "$(filter_in TinyCs.Tcs2c.Tests "${tcs2c_2[@]}")" ;;
  tcs2c-3) dotnet test "$SCRIPT_DIR/tcs2c.Tests" --verbosity minimal --filter "$(filter_out TinyCs.Tcs2c.Tests "${tcs2c_1[@]}" "${tcs2c_2[@]}")" ;;
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
for sample in "${sample_checks[@]}"; do
  dotnet run --project "$SCRIPT_DIR/Transpiler" -- check "$SCRIPT_DIR/$sample"
done
dotnet run --project "$SCRIPT_DIR/Transpiler" -- \
  check "$SCRIPT_DIR/samples/host_api_game.cs" \
  --ref "$SCRIPT_DIR/samples/host_api_stub.cs"

# 2-backend digest match (IL→C backend, only where cc is available)
if command -v cc >/dev/null 2>&1 || command -v gcc >/dev/null 2>&1; then
  echo "Running tcs2c digest verification..."
  bash "$SCRIPT_DIR/tcs2c/verify-digests.sh"
  bash "$SCRIPT_DIR/tcs2c/verify-gc.sh"
  bash "$SCRIPT_DIR/tcs2c/verify-game-core.sh"
  bash "$SCRIPT_DIR/tcs2c/verify-object-values.sh"
  bash "$SCRIPT_DIR/tcs2c/verify-host.sh"
  echo "Running tcsvm differential tests..."
  bash "$SCRIPT_DIR/tcsvm/run-tests.sh"
else
  echo "Skipping tcs2c digest verification (no C compiler)."
fi

echo "Running analyzer demo build..."
set +e
analyzer_output="$(dotnet build "$SCRIPT_DIR/samples/analyzer-demo/analyzer-demo.csproj" --no-incremental 2>&1)"
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
