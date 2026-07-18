#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
BUILD_DIR=${PERF_BUILD_DIR:-"$SCRIPT_DIR/build"}
RESULTS_FILE="$SCRIPT_DIR/results-pc.md"
TCS_LUA_SRC=${PERF_TCS_LUA_SRC:-/home/neguse/ghq/github.com/neguse/tcs/deps/lua}
LUA32_OVERRIDE=${PERF_LUA32:-"$TCS_LUA_SRC/lua32"}
RUNS=7

cmake_args=(
  -S "$SCRIPT_DIR"
  -B "$BUILD_DIR"
  -DCMAKE_BUILD_TYPE=Release
  "-DPERF_TCS_LUA_SRC=$TCS_LUA_SRC"
  "-DPERF_LUA32=$LUA32_OVERRIDE"
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
AOT_HASH=$(find_benchmark aot-hash)
AOT_SLOT=$(find_benchmark aot-slot)
LUA32=$(<"$BUILD_DIR/lua32-path.txt")

if [[ ! -x $LUA32 ]]; then
  printf 'LUA_32BITS interpreter is not executable: %s\n' "$LUA32" >&2
  exit 1
fi

lua32_probe=$(
  "$LUA32" -E -e 'io.write(math.maxinteger, ",", string.packsize("f"))'
)
if [[ $lua32_probe != "2147483647,4" ]]; then
  printf 'interpreter is not the required LUA_32BITS build: %s (%s)\n' \
    "$LUA32" "$lua32_probe" >&2
  exit 1
fi

LUAJIT=${PERF_LUAJIT:-}
if [[ -n $LUAJIT ]]; then
  if [[ ! -x $LUAJIT ]]; then
    printf 'PERF_LUAJIT is not executable: %s\n' "$LUAJIT" >&2
    exit 1
  fi
elif command -v luajit >/dev/null 2>&1; then
  LUAJIT=$(command -v luajit)
fi

variants=(native aot-hash aot-slot interp)
if [[ -n $LUAJIT ]]; then
  variants+=(jit-off)
  jit_note="jit-off included: $($LUAJIT -v 2>&1)"
else
  jit_note="jit-off skipped: luajit not found"
  printf '%s\n' "$jit_note" >&2
fi

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
    aot-hash)
      "$AOT_HASH" "$kernel" "$count"
      ;;
    aot-slot)
      "$AOT_SLOT" "$kernel" "$count"
      ;;
    interp)
      "$LUA32" -E "$SCRIPT_DIR/interp/bench.lua" \
        "$kernel" "$count" interp
      ;;
    jit-off)
      "$LUAJIT" -E -joff "$SCRIPT_DIR/interp/bench.lua" \
        "$kernel" "$count" jit-off
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

ratio() {
  local value=$1
  local baseline=$2

  awk -v value="$value" -v baseline="$baseline" \
    'BEGIN { if (baseline == 0) exit 1; printf "%.2f", value / baseline }'
}

sprite_1024_key="sprite_update|1024"
sprite_1024_native=${medians["$sprite_1024_key|native"]}
sprite_1024_slot_ratio=$(
  ratio "${medians["$sprite_1024_key|aot-slot"]}" "$sprite_1024_native"
)

if awk -v native="$sprite_1024_native" 'BEGIN { exit !(native >= 10.0) }'; then
  interpretation="PC の sprite_update N=1024 でも native が 10ms/frame 以上のため、docs/perf-ceiling.md の「native も落ちる → workload/KPI の再交渉」側に該当する。"
elif awk -v overhead="$sprite_1024_slot_ratio" \
    'BEGIN { exit !(overhead <= 1.3) }'; then
  interpretation="PC 比率では sprite_update N=1024 の aot-slot/native が ${sprite_1024_slot_ratio}x で 1.3x 以内のため、「Lua table object model を維持してよい」候補側に該当する（10ms 条件の最終判定は実機測定待ち）。"
else
  interpretation="PC 比率では sprite_update N=1024 の aot-slot/native が ${sprite_1024_slot_ratio}x で 1.3x 条件外のため、「release-lowering は IL-native 表現」側に該当する（10ms 条件の最終判定は実機測定待ち）。"
fi

RUN_TMP=$(mktemp -d "${TMPDIR:-/tmp}/t212-perf.XXXXXX")
cleanup() {
  rm -r -- "$RUN_TMP"
}
trap cleanup EXIT
summary="$RUN_TMP/results-pc.md"

{
  printf '# AOT 性能上界 perf: PC 結果\n\n'
  printf -- '- 測定: kernel/variant ごとに 7 回実行した median（ms/frame）\n'
  printf -- '- 正当性: 各 workload について全変種・全 7 run の FNV-1a digest 一致を確認\n'
  printf -- '- Lua: `%s`（LUA_32BITS）\n' "$LUA32"
  printf -- '- LuaJIT: %s\n\n' "$jit_note"
  printf '```csv\n'
  printf 'kernel,variant,N,ms_per_frame,digest\n'
  printf '%s\n' "${result_lines[@]}"
  printf '```\n\n'
  printf '## native 比\n\n'
  printf '| kernel | N | aot-slot/native | aot-hash/native | interp/native |\n'
  printf '|---|---:|---:|---:|---:|\n'
  for workload_index in "${!kernels[@]}"; do
    kernel=${kernels[$workload_index]}
    count=${counts[$workload_index]}
    workload_key="$kernel|$count"
    native_ms=${medians["$workload_key|native"]}
    slot_ratio=$(ratio "${medians["$workload_key|aot-slot"]}" "$native_ms")
    hash_ratio=$(ratio "${medians["$workload_key|aot-hash"]}" "$native_ms")
    interp_ratio=$(ratio "${medians["$workload_key|interp"]}" "$native_ms")
    printf '| %s | %s | %sx | %sx | %sx |\n' \
      "$kernel" "$count" "$slot_ratio" "$hash_ratio" "$interp_ratio"
  done
  printf '\n## 合否解釈\n\n%s\n' "$interpretation"
} >"$summary"

tee "$RESULTS_FILE" <"$summary"
