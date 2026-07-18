#!/usr/bin/env bash
# PC 上で同一 TinyC# kernel を 2 backend で実行して比較する:
#   dev     = tcs transpiler → Lua → lua32 (LUA_32BITS)
#   release = tcs → IL → tcs2c → C → cc -O2
# digest の相互一致が fail ゲート (数値意味論の一致)。実行時間はレポートのみ
# (CI runner の絶対時間はぶれるため閾値ゲートにしない)。
# 環境変数: PERF_BENCH_RUNS (default 7) / PERF_BENCH_FRAMES (default 5000)

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
RUNS="${PERF_BENCH_RUNS:-7}"
FRAMES="${PERF_BENCH_FRAMES:-5000}"
CC_CMD="${CC:-gcc}"
DOTNET="${DOTNET:-dotnet}"
LUA32="$ROOT/deps/lua/lua32"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/perf-bench.XXXXXX")"
trap 'rm -rf -- "$WORK"' EXIT

if [ ! -x "$LUA32" ]; then
  echo "Building lua32..." >&2
  cmake -B "$ROOT/build" -DCMAKE_BUILD_TYPE=Release -S "$ROOT" >/dev/null
  cmake --build "$ROOT/build" --parallel >/dev/null
fi

"$DOTNET" build "$ROOT/Transpiler/Transpiler.csproj" \
  -p:NuGetAudit=false -v:minimal >/dev/null
"$DOTNET" restore "$ROOT/tcs2c/tcs2c.csproj" \
  -p:TcsRoot="$ROOT" -p:NuGetAudit=false -v:minimal >/dev/null
"$DOTNET" build "$ROOT/tcs2c/tcs2c.csproj" --no-restore \
  -p:TcsRoot="$ROOT" -p:BuildProjectReferences=false \
  -p:NuGetAudit=false -v:minimal >/dev/null

TR_DLL="$ROOT/Transpiler/bin/Debug/net10.0/Transpiler.dll"
TCS2C_DLL="$ROOT/tcs2c/bin/Debug/net10.0/tcs2c.dll"

# bench 用コピー: frames を増やしてループ支配にする (digest 期待値は固定せず
# 両 backend の相互一致で検証する)
sed "s/int n = 256;/int n = 1024;/; s/int frames = 1000;/int frames = $FRAMES;/" \
  "$ROOT/Transpiler.Tests/DigestKernels/sprite_update.cs" > "$WORK/sprite_update.cs"
for k in spawn_churn particles particles_struct; do
  sed "s/int frames = 1000;/int frames = $FRAMES;/" \
    "$ROOT/Transpiler.Tests/DigestKernels/$k.cs" > "$WORK/$k.cs"
done

kernels=(sprite_update spawn_churn particles particles_struct)
entries=(SpriteUpdate SpawnChurn Particles ParticlesStruct)

fnv_digest() {
  python3 - "$1" << 'EOF'
import struct, sys
h = 2166136261
with open(sys.argv[1]) as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        bits = struct.unpack('<I', struct.pack('<f', float(line)))[0]
        for i in range(4):
            h = ((h ^ ((bits >> (8 * i)) & 0xFF)) * 16777619) & 0xFFFFFFFF
print(f'{h:08x}')
EOF
}

time_runs() { # cmd... -> median 秒を出力
  local times=()
  local t0 t1
  for _ in $(seq 1 "$RUNS"); do
    t0=$(date +%s%N)
    "$@" > /dev/null
    t1=$(date +%s%N)
    times+=( $(( t1 - t0 )) )
  done
  printf '%s\n' "${times[@]}" | sort -n \
    | awk -v n="$RUNS" 'NR == int((n+1)/2) { printf "%.6f", $1 / 1e9 }'
}

: > "$WORK/empty.lua"
lua_base=$(time_runs "$LUA32" "$WORK/empty.lua")

rows=()
for i in "${!kernels[@]}"; do
  k=${kernels[$i]}
  e=${entries[$i]}

  "$DOTNET" "$TR_DLL" "$WORK/$k.cs" -o "$WORK/$k.lua" >/dev/null
  printf '\n%s.Main()\n' "$e" >> "$WORK/$k.lua"
  "$LUA32" "$WORK/$k.lua" > "$WORK/$k.dev.out"
  dev_digest=$(fnv_digest "$WORK/$k.dev.out")
  dev_s=$(time_runs "$LUA32" "$WORK/$k.lua")
  dev_ms=$(python3 -c "print(f'{(float('$dev_s') - float('$lua_base')) * 1000.0 / $FRAMES:.6f}')")

  # release backend。tcs2c 未対応 kernel (struct 系) は dev のみレポート
  if "$DOTNET" "$TCS2C_DLL" --digest-f32 --entry "$e" \
      "$WORK/$k.cs" -o "$WORK/$k.c" 2> "$WORK/$k.tcs2c.err"; then
    "$CC_CMD" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
      "$WORK/$k.c" -o "$WORK/$k.bin"
    rel_digest=$("$WORK/$k.bin")
    if [ "$dev_digest" != "$rel_digest" ]; then
      echo "DIGEST MISMATCH $k: dev=$dev_digest release=$rel_digest" >&2
      exit 1
    fi
    rel_s=$(time_runs "$WORK/$k.bin")
    rel_ms=$(python3 -c "print(f'{float('$rel_s') * 1000.0 / $FRAMES:.6f}')")
    ratio=$(python3 -c "print(f'{float('$dev_ms') / float('$rel_ms'):.1f}x')")
  else
    rel_ms="-"
    ratio="-"
    echo "note: $k は tcs2c 未対応のため dev のみ ($(head -1 "$WORK/$k.tcs2c.err"))" >&2
  fi

  rows+=( "$k,$dev_ms,$rel_ms,$ratio,$dev_digest" )
done

echo "kernel,dev_ms_per_frame,release_ms_per_frame,dev/release,digest"
printf '%s\n' "${rows[@]}"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "## perf 2-backend bench (frames=$FRAMES, ${RUNS}-run median)"
    echo ""
    echo "| kernel | dev ms/frame | release ms/frame | dev/release | digest |"
    echo "|---|---|---|---|---|"
    for row in "${rows[@]}"; do
      IFS=, read -r a b c d e2 <<< "$row"
      echo "| $a | $b | $c | $d | $e2 |"
    done
  } >> "$GITHUB_STEP_SUMMARY"
fi
