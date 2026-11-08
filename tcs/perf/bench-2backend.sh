#!/usr/bin/env bash
# PC 上で同一 TinyC# kernel を 2 つの実行系で実行して比較する:
#   vm      = tcs → IL → tcs2vm → .tcsb → tcsvm (dev の実行系。doc/vm-design.md)
#   release = tcs → IL → tcs2c → C → cc -O2
# vm の stdout の digest と release の digest の一致が fail ゲート (数値意味論の一致)。実行時間はレポートのみ
# (CI runner の絶対時間はぶれるため閾値ゲートにしない)。
# 環境変数: PERF_BENCH_RUNS (default 7) / PERF_BENCH_FRAMES (default 5000)

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
RUNS="${PERF_BENCH_RUNS:-7}"
FRAMES="${PERF_BENCH_FRAMES:-5000}"
CC_CMD="${CC:-gcc}"
DOTNET="${DOTNET:-dotnet}"
TCSVM="$ROOT/build/tcsvm/tcsvm"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/perf-bench.XXXXXX")"
trap 'rm -rf -- "$WORK"' EXIT

# tcsvm (root の CMake)。tcsvm の変更に追従させるため毎回 build する
cmake -B "$ROOT/build" -DCMAKE_BUILD_TYPE=Release -S "$ROOT" >/dev/null
cmake --build "$ROOT/build" --target tcsvm --parallel >/dev/null

# tcs2c と tcs2vm は project の参照を build しないので、参照する compiler と library を先に作る
"$DOTNET" build "$ROOT/Transpiler/Transpiler.csproj" \
  -p:NuGetAudit=false -v:minimal >/dev/null
"$DOTNET" restore "$ROOT/tcs2c/tcs2c.csproj" \
  -p:TcsRoot="$ROOT" -p:NuGetAudit=false -v:minimal >/dev/null
"$DOTNET" build "$ROOT/tcs2c/tcs2c.csproj" --no-restore \
  -p:TcsRoot="$ROOT" -p:BuildProjectReferences=false \
  -p:NuGetAudit=false -v:minimal >/dev/null

"$DOTNET" restore "$ROOT/tcsvm/Emitter/tcs2vm.csproj" \
  -p:TcsRoot="$ROOT" -p:NuGetAudit=false -v:minimal >/dev/null
"$DOTNET" build "$ROOT/tcsvm/Emitter/tcs2vm.csproj" --no-restore \
  -p:TcsRoot="$ROOT" -p:BuildProjectReferences=false \
  -p:NuGetAudit=false -v:minimal >/dev/null

TCS2VM_DLL="$ROOT/tcsvm/Emitter/bin/Debug/net10.0/tcs2vm.dll"
TCS2C_DLL="$ROOT/tcs2c/bin/Debug/net10.0/tcs2c.dll"

# bench 用コピー: frames を増やしてループ支配にする (digest 期待値は固定せず
# vm と release の相互一致で検証する)
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

echo 'public class Empty { public static void Main() { } }' > "$WORK/empty.cs"
"$DOTNET" "$TCS2VM_DLL" "$WORK/empty.cs" -o "$WORK/empty.tcsb"
vm_base=$(time_runs "$TCSVM" "$WORK/empty.tcsb")

rows=()
for i in "${!kernels[@]}"; do
  k=${kernels[$i]}
  e=${entries[$i]}

  # vm backend
  "$DOTNET" "$TCS2VM_DLL" --entry "$e" "$WORK/$k.cs" -o "$WORK/$k.tcsb"
  "$TCSVM" "$WORK/$k.tcsb" > "$WORK/$k.vm.out"
  vm_digest=$(fnv_digest "$WORK/$k.vm.out")
  vm_s=$(time_runs "$TCSVM" "$WORK/$k.tcsb")
  vm_ms=$(python3 -c "print('%.6f' % ((float('$vm_s') - float('$vm_base')) * 1000.0 / $FRAMES))")

  # release backend。digest が vm と一致することを要求する
  "$DOTNET" "$TCS2C_DLL" --digest-f32 --entry "$e" "$WORK/$k.cs" -o "$WORK/$k.c"
  "$CC_CMD" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
    "$WORK/$k.c" -o "$WORK/$k.bin"
  rel_digest=$("$WORK/$k.bin")
  if [ "$vm_digest" != "$rel_digest" ]; then
    echo "DIGEST MISMATCH $k: vm=$vm_digest release=$rel_digest" >&2
    exit 1
  fi
  rel_s=$(time_runs "$WORK/$k.bin")
  rel_ms=$(python3 -c "print('%.6f' % (float('$rel_s') * 1000.0 / $FRAMES))")
  ratio=$(python3 -c "print('%.1fx' % (float('$vm_ms') / float('$rel_ms')))")

  rows+=( "$k,$vm_ms,$rel_ms,$ratio,$vm_digest" )
done

echo "kernel,vm_ms_per_frame,release_ms_per_frame,vm/release,digest"
printf '%s\n' "${rows[@]}"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "## perf bench (vm / release) (frames=$FRAMES, ${RUNS}-run median)"
    echo ""
    echo "| kernel | vm ms/frame | release ms/frame | vm/release | digest |"
    echo "|---|---|---|---|---|"
    for row in "${rows[@]}"; do
      IFS=, read -r a v c d e2 <<< "$row"
      echo "| $a | $v | $c | $d | $e2 |"
    done
  } >> "$GITHUB_STEP_SUMMARY"
fi
