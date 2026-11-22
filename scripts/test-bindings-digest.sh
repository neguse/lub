#!/usr/bin/env bash
# Run TestBindingsDigest with new every time / reuse / re-Clear, and check that the per-frame digests (the structure of the C API calls) match.
# When given the .NET execution executable (lub-sample-tests built from dotnet/SampleRunner with -p:Tests=true), it also runs the same way with the .NET execution Bindings (held in managed arrays) and checks that the digest matches the tcs VM player (the shared library is passed with LUB_NATIVE_LIB).
# Build tests/cs/.lub/all.tcsb first (cs_tests_vm_build of scripts/cs-tests.sh).
# Usage: scripts/test-bindings-digest.sh [lub binary] [.NET lub-sample-tests]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
dotnet_tests="${2:-}"
dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT

runners=(vm)
[[ -n $dotnet_tests ]] && runners+=(dotnet)
for runner in "${runners[@]}"; do
  if [[ $runner == vm ]]; then
    cmd=("$binary" tests/cs/.lub/all.tcsb)
  else
    cmd=("$dotnet_tests")
  fi
  for mode in new reuse clear; do
    log="$dir/$runner-$mode.log"
    LUB_TEST_BINDINGS_MODE="$mode" scripts/run-headless.sh "${cmd[@]}" --entry TestBindingsDigest \
      --fixed-dt 0.0166666666666667 --digest >"$log" 2>&1 || { cat "$log"; echo "FAIL: $runner $mode exited non-zero" >&2; exit 1; }
    grep '^digest ' "$log" >"$dir/$runner-$mode.digest"
    [[ -s "$dir/$runner-$mode.digest" ]] || { cat "$log"; echo "FAIL: $runner $mode printed no digest" >&2; exit 1; }
    echo "$runner $mode: $(wc -l <"$dir/$runner-$mode.digest") frames, last $(tail -1 "$dir/$runner-$mode.digest")"
    cmp "$dir/vm-new.digest" "$dir/$runner-$mode.digest"
  done
done
echo "PASS"
