#!/usr/bin/env bash
# tcsvm のテスト (doc/vm-design.md §16): tests/*.cs、samples の 6 本、digest kernel を VM (tcs2vm → tcsvm) で走らせ、
# 期待の stdout (tests/expected/*.txt、samples の期待の文字列、kernel の固定の digest) と一致することを確かめる。
# C backend (tcs c → cc) が動かせる入力は、C の stdout と成否も VM と一致すること。
# fault のテスト (Fault*.cs) は fault までの stdout が期待と一致し、VM と C の両方が失敗し、VM の trace が C# の file と行を指すことも見る。
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/tcsvm-tests.XXXXXX")"
trap 'rm -rf -- "$WORK"' EXIT

cmake -B "$ROOT/build" -DCMAKE_BUILD_TYPE=Release -S "$ROOT" >/dev/null
cmake --build "$ROOT/build" --parallel >/dev/null
"$DOTNET" build "$ROOT/tcsvm/Emitter/tcs2vm.csproj" -p:NuGetAudit=false -v:quiet -nologo >/dev/null
"$DOTNET" build "$ROOT/tcs2c/tcs2c.csproj" -p:NuGetAudit=false -v:quiet -nologo >/dev/null

TCS2VM_DLL="$ROOT/tcsvm/Emitter/bin/Debug/net10.0/tcs2vm.dll"
TCS2C_DLL="$ROOT/tcs2c/bin/Debug/net10.0/tcs2c.dll"
TCSVM="$ROOT/build/tcsvm/tcsvm"
CC_CMD="${CC:-cc}"

# C backend が動かせない入力 (C の比較を外し、期待の stdout だけで見る)
#   OutCalls: tcs c が `int.TryParse(s, out _)` の捨てる out を持たない (unbound IL variable: _)
NO_C=" OutCalls "

# kernel の stdout (1 行に 1 つの float) の FNV-1a digest。Transpiler.Tests/DigestHarnessTests と perf/bench-2backend.sh と同じ計算
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

fail=0
run_case() { # path entry name expect [extra-source]
  # expect は file:<期待の stdout の file> / text:<期待の stdout> / digest:<stdout の FNV-1a digest>
  local src="$1" entry="$2" name="$3" expect="$4" extra="${5:-}"
  local srcs=("$src")
  [ -z "$extra" ] || srcs+=("$extra")
  local vm_rc=0
  if ! "$DOTNET" "$TCS2VM_DLL" --entry "$entry" "${srcs[@]}" -o "$WORK/$name.tcsb" 2> "$WORK/$name.emit.err"; then
    echo "FAIL $name: $(cat "$WORK/$name.emit.err")"
    fail=1
    return
  fi
  "$TCSVM" "$WORK/$name.tcsb" > "$WORK/$name.vm.out" 2> "$WORK/$name.vm.err" || vm_rc=$?
  # GC stress (確保の地点ごとに回収) でも同じ stdout と成否であること。root の漏れは出力の違いで現れる
  local st_rc=0
  "$TCSVM" --gc-stress "$WORK/$name.tcsb" > "$WORK/$name.st.out" 2> "$WORK/$name.st.err" || st_rc=$?
  if ! cmp -s "$WORK/$name.vm.out" "$WORK/$name.st.out" || [ $(( vm_rc != 0 )) -ne $(( st_rc != 0 )) ]; then
    echo "FAIL $name: differs under --gc-stress"
    head -5 "$WORK/$name.st.err"
    fail=1
    return
  fi
  case "$expect" in
    file:*) cp "${expect#file:}" "$WORK/$name.expected" ;;
    text:*) printf '%s\n' "${expect#text:}" > "$WORK/$name.expected" ;;
    digest:*)
      local digest
      digest="$(fnv_digest "$WORK/$name.vm.out")"
      if [ "$digest" != "${expect#digest:}" ]; then
        echo "FAIL $name: digest ${expect#digest:} expected, got $digest"
        fail=1
        return
      fi
      cp "$WORK/$name.vm.out" "$WORK/$name.expected"
      ;;
  esac
  if ! cmp -s "$WORK/$name.expected" "$WORK/$name.vm.out"; then
    echo "FAIL $name: stdout differs from the expected"
    diff "$WORK/$name.expected" "$WORK/$name.vm.out" | head -10 || true
    fail=1
    return
  fi
  case "$name" in
    Fault*) [ "$vm_rc" -ne 0 ] ;;
    *) [ "$vm_rc" -eq 0 ] ;;
  esac || {
    echo "FAIL $name: vm exited with $vm_rc"
    cat "$WORK/$name.vm.err"
    fail=1
    return
  }
  local with_c=""
  if [[ "$NO_C" != *" $name "* ]]; then
    local c_rc=0
    if ! "$DOTNET" "$TCS2C_DLL" --entry "$entry" "${srcs[@]}" -o "$WORK/$name.c" 2> "$WORK/$name.c.err" \
        || ! "$CC_CMD" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard "$WORK/$name.c" -o "$WORK/$name.cbin" -lm \
          2>> "$WORK/$name.c.err"; then
      echo "FAIL $name: C build"
      head -3 "$WORK/$name.c.err"
      fail=1
      return
    fi
    "$WORK/$name.cbin" > "$WORK/$name.c.out" 2>> "$WORK/$name.c.err" || c_rc=$?
    if ! cmp -s "$WORK/$name.vm.out" "$WORK/$name.c.out" || [ $(( vm_rc != 0 )) -ne $(( c_rc != 0 )) ]; then
      echo "FAIL $name: C differs (exit vm=$vm_rc c=$c_rc)"
      diff "$WORK/$name.vm.out" "$WORK/$name.c.out" | head -10 || true
      fail=1
      return
    fi
    with_c=", C"
  fi
  case "$name" in
    Fault*)
      if ! grep -q "at $entry\.[A-Za-z<>0-9]* ($name.cs:[0-9]*)" "$WORK/$name.vm.err"; then
        echo "FAIL $name: no fault trace"
        cat "$WORK/$name.vm.err"
        fail=1
        return
      fi
      echo "ok   $name ($(head -1 "$WORK/$name.vm.err" | sed 's/^tcsvm: //'); $(sed -n 2p "$WORK/$name.vm.err" | sed 's/^ *//')$with_c)"
      ;;
    *) echo "ok   $name ($(wc -l < "$WORK/$name.vm.out") lines$with_c)" ;;
  esac
}

for src in "$ROOT"/tcsvm/tests/*.cs; do
  name="$(basename "$src" .cs)"
  run_case "$src" "$name" "$name" "file:$ROOT/tcsvm/tests/expected/$name.txt"
done
# tcs の samples: tcs2c.Tests の SampleParityTests と同じ entry の式と期待の文字列
run_sample() { # sample expr expected
  local name="sample_$1"
  printf 'public static class TcsEntry { public static void Main() { System.Console.WriteLine(%s); } }\n' "$2" \
    > "$WORK/$name.entry.cs"
  run_case "$ROOT/samples/$1.cs" TcsEntry "$name" "text:$3" "$WORK/$name.entry.cs"
}
run_sample hello 'Hello.Greet("TinyC#") + "," + Hello.Add(2, 3).ToString()' 'Hello, TinyC#!,5'
run_sample game 'Battle.Run()' 'Dragon: HP=145 [attacking] | alive=2'
run_sample inventory 'Game.Test()' 'Items=4 Total=330 Best=Sword Shield=1'
run_sample entity 'EntitySample.Run()' 'Slime:enemy@6,2 HP=13'
run_sample statemachine 'StateMachineSample.Run()' 'open,open,locked,closed'
run_sample collision 'CollisionSample.Run()' 'hit,miss,hit'

# digest kernel: tcs2c/verify-digests.sh と Transpiler.Tests/DigestHarnessTests と同じ固定の digest。
# particles_struct は particles と同じ演算列なので同じ digest
KERNELS="$ROOT/Transpiler.Tests/DigestKernels"
run_case "$KERNELS/sprite_update.cs" SpriteUpdate sprite_update digest:e8814b32
run_case "$KERNELS/spawn_churn.cs" SpawnChurn spawn_churn digest:9274159d
run_case "$KERNELS/particles.cs" Particles particles digest:8bf97e09
run_case "$KERNELS/particles_struct.cs" ParticlesStruct particles_struct digest:8bf97e09

# host 連携 (tests/host): host_test.c が stub.cs の面を host の関数として登録し、Game の entry を呼ぶ。
# Reject.cs の module では、登録が module の struct と合わないときの読み込みの拒否を見る。
HOST="$ROOT/tcsvm/tests/host"
VM_SRC=()
for f in "$ROOT"/tcsvm/vm/*.c; do [ "$(basename "$f")" = tcsvm_main.c ] || VM_SRC+=("$f"); done
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/host_test" "$HOST/host_test.c" "${VM_SRC[@]}" \
    "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c "$ROOT"/runtime/utf8/*.c \
    "$ROOT"/runtime/model/*.c -lm 2> "$WORK/host_test.err"; then
  echo "FAIL host: build"; head -5 "$WORK/host_test.err"; fail=1
elif ! "$DOTNET" "$TCS2VM_DLL" --ref "$HOST/stub.cs" --export 'Game.*' "$HOST/Game.cs" -o "$WORK/host.tcsb" 2> "$WORK/host.emit.err" \
    || ! "$DOTNET" "$TCS2VM_DLL" --ref "$HOST/stub.cs" --export 'Reject.*' "$HOST/Reject.cs" -o "$WORK/reject.tcsb" 2>> "$WORK/host.emit.err"; then
  echo "FAIL host: $(cat "$WORK/host.emit.err")"; fail=1
else
  host_ok=1
  for mode in "" --gc-stress; do
    if ! "$WORK/host_test" "$WORK/host.tcsb" "$WORK/reject.tcsb" $mode > "$WORK/host.out" 2> "$WORK/host.err" || ! cmp -s "$WORK/host.out" "$HOST/expected.txt"; then
      echo "FAIL host${mode:+ ($mode)}"; diff "$HOST/expected.txt" "$WORK/host.out" | head -5; cat "$WORK/host.err"; fail=1; host_ok=0
    fi
  done
  [ "$host_ok" = 0 ] || echo "ok   host ($(wc -l < "$HOST/expected.txt") lines)"
fi
# --trace の (行, self) の組 (tests/trace): 覚えた object と同じ型の別の object の self で通っただけの行を除いて取り出す
TRACE="$ROOT/tcsvm/tests/trace"
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/trace_test" "$TRACE/trace_test.c" "${VM_SRC[@]}" \
    "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c "$ROOT"/runtime/utf8/*.c \
    "$ROOT"/runtime/model/*.c -lm 2> "$WORK/trace_test.err"; then
  echo "FAIL trace: build"; head -5 "$WORK/trace_test.err"; fail=1
elif ! "$DOTNET" "$TCS2VM_DLL" --ref "$TRACE/stub.cs" --export 'Game.*' --trace "$TRACE/Game.cs" "$TRACE/Game.cs" -o "$WORK/trace.tcsb" 2> "$WORK/trace.emit.err"; then
  echo "FAIL trace: $(cat "$WORK/trace.emit.err")"; fail=1
elif ! "$WORK/trace_test" "$WORK/trace.tcsb" > "$WORK/trace.out" 2>&1 || ! cmp -s "$WORK/trace.out" "$TRACE/expected.txt"; then
  echo "FAIL trace"; diff "$TRACE/expected.txt" "$WORK/trace.out" | head -5; fail=1
else
  echo "ok   trace (行, self) の組"
fi
# 大きな配列の一部を見る (tests/inspect): tvm_inspect は頼まれた範囲の外の要素に手間をかけない
INSPECT="$ROOT/tcsvm/tests/inspect"
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/inspect_test" "$INSPECT/inspect_test.c" "${VM_SRC[@]}" \
    "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c "$ROOT"/runtime/utf8/*.c \
    "$ROOT"/runtime/model/*.c -lm 2> "$WORK/inspect_test.err"; then
  echo "FAIL inspect: build"; head -5 "$WORK/inspect_test.err"; fail=1
elif ! "$DOTNET" "$TCS2VM_DLL" --export 'Game.*' "$INSPECT/Game.cs" -o "$WORK/inspect.tcsb" 2> "$WORK/inspect.emit.err"; then
  echo "FAIL inspect: $(cat "$WORK/inspect.emit.err")"; fail=1
elif ! "$WORK/inspect_test" "$WORK/inspect.tcsb" > "$WORK/inspect.out" 2>&1 || ! cmp -s "$WORK/inspect.out" "$INSPECT/expected.txt"; then
  echo "FAIL inspect"; diff "$INSPECT/expected.txt" "$WORK/inspect.out" | head -5; fail=1
else
  echo "ok   inspect (大きな配列の一部)"
fi
# 行ごとの負荷 (tests/profile): --trace の行の時間と確保、GC の止まりを、test の時計で決まった値として取り出す
PROF="$ROOT/tcsvm/tests/profile"
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/profile_test" "$PROF/profile_test.c" "${VM_SRC[@]}" \
    "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c "$ROOT"/runtime/utf8/*.c \
    "$ROOT"/runtime/model/*.c -lm -pthread 2> "$WORK/profile_test.err"; then
  echo "FAIL profile: build"; head -5 "$WORK/profile_test.err"; fail=1
elif ! "$DOTNET" "$TCS2VM_DLL" --ref "$PROF/stub.cs" --export 'Game.*' --trace "$PROF/Game.cs" "$PROF/Game.cs" "$PROF/Lib.cs" -o "$WORK/profile.tcsb" 2> "$WORK/profile.emit.err"; then
  echo "FAIL profile: $(cat "$WORK/profile.emit.err")"; fail=1
elif ! "$WORK/profile_test" "$WORK/profile.tcsb" > "$WORK/profile.out" 2>&1 || ! cmp -s "$WORK/profile.out" "$PROF/expected.txt"; then
  echo "FAIL profile"; diff "$PROF/expected.txt" "$WORK/profile.out" | head -10; fail=1
else
  echo "ok   profile (行ごとの負荷)"
fi
# mixed-mode の最小の試験 (doc/object-model.md M-4): tcs2c --lib の生成 C の関数が VM の object を読み書きする
MIXED="$ROOT/tcsvm/tests/mixed"
if ! "$DOTNET" "$TCS2C_DLL" --lib -o "$WORK/mixed.c" "$MIXED/Mixed.cs" 2> "$WORK/mixed.err" \
    || ! "$DOTNET" "$TCS2VM_DLL" --export 'Game.*' "$MIXED/Mixed.cs" -o "$WORK/mixed.tcsb" 2>> "$WORK/mixed.err" \
    || ! "${CC:-cc}" -std=gnu11 -O1 -g -DMIXED_C="\"$WORK/mixed.c\"" -o "$WORK/mixed_test" "$MIXED/mixed_test.c" "${VM_SRC[@]}" \
      "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c "$ROOT"/runtime/utf8/*.c \
      "$ROOT"/runtime/model/*.c -lm 2>> "$WORK/mixed.err"; then
  echo "FAIL mixed: build"; head -5 "$WORK/mixed.err"; fail=1
elif ! "$WORK/mixed_test" "$WORK/mixed.tcsb" > "$WORK/mixed.out" 2>&1 || ! cmp -s "$WORK/mixed.out" "$MIXED/expected.txt"; then
  echo "FAIL mixed"; diff "$MIXED/expected.txt" "$WORK/mixed.out" | head -5; fail=1
else
  echo "ok   mixed (tcs2c の関数が VM の object を読み書きする)"
fi
# module 単位の混在 (doc/object-model.md §10): editor は tcs2c --lib --vm-host の C、content は VM で reload する
MODS="$ROOT/tcsvm/tests/modules"
cp "$MODS/ContentV1.cs.txt" "$WORK/Content.cs"
cp "$MODS/ContentV2.cs.txt" "$WORK/Content2.cs"
if ! "$DOTNET" "$TCS2C_DLL" --lib --vm-host -o "$WORK/editor.c" "$MODS/Editor.cs" 2> "$WORK/modules.err" \
    || ! "$DOTNET" "$TCS2VM_DLL" --ref "$MODS/Editor.cs" --export 'Game.*' "$WORK/Content.cs" -o "$WORK/content1.tcsb" 2>> "$WORK/modules.err" \
    || ! "$DOTNET" "$TCS2VM_DLL" --ref "$MODS/Editor.cs" --export 'Game.*' --reload-from "$WORK/Content.cs" \
      --batch "$WORK/content2.batch" "$WORK/Content2.cs" -o "$WORK/content2.tcsb" 2>> "$WORK/modules.err" \
    ; then
  echo "FAIL modules: build"; head -5 "$WORK/modules.err"; fail=1
else
  # shared heap (object-model §10.2): 通常と、tcs2c の GC stress (nursery を毒値で埋め、frame ごとに全回収)
  mods_ok=1
  for mode in "" -DTCS_GC_STRESS; do
    if ! "${CC:-cc}" -std=gnu11 -O1 -g $mode -I"$ROOT/tcsvm/vm" -DEDITOR_C="\"$WORK/editor.c\"" -o "$WORK/modules_test" \
        "$MODS/modules_test.c" "${VM_SRC[@]}" "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c \
        "$ROOT"/runtime/utf8/*.c "$ROOT"/runtime/model/*.c -lm 2>> "$WORK/modules.err" \
        || ! "$WORK/modules_test" "$WORK/content1.tcsb" "$WORK/content2.batch" > "$WORK/modules.out" 2>&1 \
        || ! cmp -s "$WORK/modules.out" "$MODS/expected.txt"; then
      echo "FAIL modules${mode:+ ($mode)}"; diff "$MODS/expected.txt" "$WORK/modules.out" | head -5; head -3 "$WORK/modules.err"
      fail=1; mods_ok=0
    fi
  done
  [ "$mods_ok" = 0 ] || echo "ok   modules (editor は C、content は VM で reload。shared heap)"
fi
# watch の stream (tcs2vm --watch → vm_batch → tcsvm --reload-batch、restart_required)
mkdir -p "$WORK/watch"
if ! python3 -I "$HOST/watch_test.py" "$DOTNET" "$TCS2VM_DLL" "$TCSVM" "$WORK/watch"; then
  echo "FAIL watch"; fail=1
fi
exit "$fail"
