#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
BUILD_DIR=${PERF_BUILD_DIR:-"$SCRIPT_DIR/build"}
RUNS=7

cmake_args=(
  -S "$SCRIPT_DIR"
  -B "$BUILD_DIR"
  -DCMAKE_BUILD_TYPE=Release
)

cmake "${cmake_args[@]}" >&2
cmake --build "$BUILD_DIR" --config Release --parallel >&2

find_benchmark() {
  local name=$1
  local candidate

  for candidate in "$BUILD_DIR/$name" "$BUILD_DIR/Release/$name"; do
    if [[ -x $candidate ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  printf 'benchmark executable not found: %s\n' "$name" >&2
  return 1
}

NATIVE=$(find_benchmark native)
variants=(native)

kernels=(
  sprite_update
  sprite_update
  sprite_update
  spawn_churn_naive
  spawn_churn_pool
  particles
)
counts=(256 1024 4096 1024 1024 4096)

declare -a result_lines=()
declare -A medians=()
declare -A workload_digests=()

run_variant() {
  local variant=$1
  local kernel=$2
  local count=$3

  case $variant in
    native)
      "$NATIVE" "$kernel" "$count"
      ;;
    *)
      printf 'unknown variant: %s\n' "$variant" >&2
      return 1
      ;;
  esac
}

for workload_index in "${!kernels[@]}"; do
  kernel=${kernels[$workload_index]}
  count=${counts[$workload_index]}
  workload_key="$kernel|$count"

  for variant in "${variants[@]}"; do
    printf 'measuring %s %s N=%s (%d runs)\n' \
      "$kernel" "$variant" "$count" "$RUNS" >&2
    samples=()
    variant_digest=

    for ((run = 1; run <= RUNS; ++run)); do
      output=$(run_variant "$variant" "$kernel" "$count")
      if [[ $output == *$'\n'* ]]; then
        printf 'unexpected multiline benchmark output: %q\n' "$output" >&2
        exit 1
      fi

      IFS=, read -r got_kernel got_variant got_count ms digest extra <<<"$output"
      if [[ -n ${extra:-} || $got_kernel != "$kernel" \
          || $got_variant != "$variant" || $got_count != "$count" ]]; then
        printf 'invalid benchmark output: %s\n' "$output" >&2
        exit 1
      fi
      if [[ ! $ms =~ ^[0-9]+([.][0-9]+)?$ \
          || ! $digest =~ ^[0-9a-f]{8}$ ]]; then
        printf 'invalid timing/digest in benchmark output: %s\n' "$output" >&2
        exit 1
      fi

      samples+=("$ms")
      if [[ -z $variant_digest ]]; then
        variant_digest=$digest
      elif [[ $digest != "$variant_digest" ]]; then
        printf 'digest changed between runs for %s/%s/N=%s: %s != %s\n' \
          "$kernel" "$variant" "$count" "$variant_digest" "$digest" >&2
        exit 1
      fi
    done

    mapfile -t sorted_samples < <(printf '%s\n' "${samples[@]}" | LC_ALL=C sort -n)
    median=${sorted_samples[3]}
    medians["$workload_key|$variant"]=$median

    if [[ -z ${workload_digests[$workload_key]+set} ]]; then
      workload_digests[$workload_key]=$variant_digest
    elif [[ ${workload_digests[$workload_key]} != "$variant_digest" ]]; then
      printf 'digest mismatch for %s N=%s: expected %s, %s produced %s\n' \
        "$kernel" "$count" "${workload_digests[$workload_key]}" \
        "$variant" "$variant_digest" >&2
      exit 1
    fi

    result_lines+=(
      "$kernel,$variant,$count,$median,$variant_digest"
    )
  done
done

# 結果は stdout に出す (results-pc.md は Lua の変種と並べた 2026-07-18 の記録として残す)
printf '# perf: PC 結果 (native)\n\n'
printf -- '- 測定: kernel ごとに 7 回実行した median（ms/frame）\n'
printf -- '- 正当性: 各 workload について全 7 run の FNV-1a digest 一致を確認\n\n'
printf '```csv\n'
printf 'kernel,variant,N,ms_per_frame,digest\n'
printf '%s\n' "${result_lines[@]}"
printf '```\n'
