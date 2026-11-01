#!/usr/bin/env bash
# tcsvm の差分テスト (doc/vm-design.md §16 の V1 分): tests/*.cs と digest kernel を
# Lua (tcs → lua32) と VM (tcs2vm → tcsvm) で走らせ、stdout と成否が一致することを確かめる。
# fault のテスト (Fault*.cs) は両方が失敗し、VM の trace が C# の file と行を指すことも見る。
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/tcsvm-tests.XXXXXX")"
trap 'rm -rf -- "$WORK"' EXIT

cmake -B "$ROOT/build" -DCMAKE_BUILD_TYPE=Release -S "$ROOT" >/dev/null
cmake --build "$ROOT/build" --parallel >/dev/null
"$DOTNET" build "$ROOT/tcsvm/Emitter/tcs2vm.csproj" -p:NuGetAudit=false -v:quiet -nologo >/dev/null

TR_DLL="$ROOT/Transpiler/bin/Debug/net10.0/Transpiler.dll"
TCS2VM_DLL="$ROOT/tcsvm/Emitter/bin/Debug/net10.0/tcs2vm.dll"
LUA32="$ROOT/deps/lua/lua32"
TCSVM="$ROOT/build/tcsvm/tcsvm"

fail=0
run_case() { # path entry [name extra-source]
  local src="$1" entry="$2" name="${3:-}" extra="${4:-}"
  [ -n "$name" ] || name="$(basename "$src" .cs)"
  local srcs=("$src")
  [ -z "$extra" ] || srcs+=("$extra")
  if ! "$DOTNET" "$TR_DLL" "${srcs[@]}" -o "$WORK/$name.lua" > "$WORK/$name.tr.err" 2>&1; then
    echo "FAIL $name: tcs (Lua) rejected the input"
    grep -i error "$WORK/$name.tr.err" | head -3
    fail=1
    return
  fi
  printf '\n%s.Main()\n' "$entry" >> "$WORK/$name.lua"
  local lua_rc=0 vm_rc=0
  "$LUA32" "$WORK/$name.lua" > "$WORK/$name.lua.out" 2> "$WORK/$name.lua.err" || lua_rc=$?
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
  if ! cmp -s "$WORK/$name.lua.out" "$WORK/$name.vm.out"; then
    echo "FAIL $name: stdout differs"
    diff "$WORK/$name.lua.out" "$WORK/$name.vm.out" | head -10
    fail=1
    return
  fi
  if [ $(( lua_rc != 0 )) -ne $(( vm_rc != 0 )) ]; then
    echo "FAIL $name: exit lua=$lua_rc vm=$vm_rc"
    cat "$WORK/$name.vm.err"
    fail=1
    return
  fi
  case "$name" in
    Fault*)
      if [ "$vm_rc" -eq 0 ] || ! grep -q "at $entry\.[A-Za-z<>0-9]* ($name.cs:[0-9]*)" "$WORK/$name.vm.err"; then
        echo "FAIL $name: no fault trace"
        cat "$WORK/$name.vm.err"
        fail=1
        return
      fi
      echo "ok   $name ($(head -1 "$WORK/$name.vm.err" | sed 's/^tcsvm: //'); $(sed -n 2p "$WORK/$name.vm.err" | sed 's/^ *//'))"
      ;;
    *) echo "ok   $name ($(wc -l < "$WORK/$name.vm.out") lines)" ;;
  esac
}

for src in "$ROOT"/tcsvm/tests/*.cs; do
  run_case "$src" "$(basename "$src" .cs)"
done
# tcs の samples: tcs2c.Tests の SampleParityTests と同じ entry の式と期待の文字列
run_sample() { # sample expr expected
  local name="sample_$1"
  printf 'public static class TcsEntry { public static void Main() { System.Console.WriteLine(%s); } }\n' "$2" \
    > "$WORK/$name.entry.cs"
  run_case "$ROOT/samples/$1.cs" TcsEntry "$name" "$WORK/$name.entry.cs"
  if [ -f "$WORK/$name.vm.out" ] && [ "$(cat "$WORK/$name.vm.out")" != "$3" ]; then
    echo "FAIL $name: expected '$3', got '$(cat "$WORK/$name.vm.out")'"
    fail=1
  fi
}
run_sample hello 'Hello.Greet("TinyC#") + "," + Hello.Add(2, 3).ToString()' 'Hello, TinyC#!,5'
run_sample game 'Battle.Run()' 'Dragon: HP=145 [attacking] | alive=2'
run_sample inventory 'Game.Test()' 'Items=4 Total=330 Best=Sword Shield=1'
run_sample entity 'EntitySample.Run()' 'Slime:enemy@6,2 HP=13'
run_sample statemachine 'StateMachineSample.Run()' 'open,open,locked,closed'
run_sample collision 'CollisionSample.Run()' 'hit,miss,hit'

run_case "$ROOT/Transpiler.Tests/DigestKernels/sprite_update.cs" SpriteUpdate
run_case "$ROOT/Transpiler.Tests/DigestKernels/spawn_churn.cs" SpawnChurn
run_case "$ROOT/Transpiler.Tests/DigestKernels/particles.cs" Particles
run_case "$ROOT/Transpiler.Tests/DigestKernels/particles_struct.cs" ParticlesStruct

# host 連携 (tests/host): host_test.c が stub.cs の面を host の関数として登録し、Game の entry を呼ぶ
HOST="$ROOT/tcsvm/tests/host"
VM_SRC=()
for f in "$ROOT"/tcsvm/vm/*.c; do [ "$(basename "$f")" = tcsvm_main.c ] || VM_SRC+=("$f"); done
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/host_test" "$HOST/host_test.c" "${VM_SRC[@]}" \
    "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c "$ROOT"/runtime/utf8/*.c \
    "$ROOT"/runtime/model/*.c -lm 2> "$WORK/host_test.err"; then
  echo "FAIL host: build"; head -5 "$WORK/host_test.err"; fail=1
elif ! "$DOTNET" "$TCS2VM_DLL" --ref "$HOST/stub.cs" --export 'Game.*' "$HOST/Game.cs" -o "$WORK/host.tcsb" 2> "$WORK/host.emit.err"; then
  echo "FAIL host: $(cat "$WORK/host.emit.err")"; fail=1
else
  host_ok=1
  for mode in "" --gc-stress; do
    if ! "$WORK/host_test" "$WORK/host.tcsb" $mode > "$WORK/host.out" 2> "$WORK/host.err" || ! cmp -s "$WORK/host.out" "$HOST/expected.txt"; then
      echo "FAIL host${mode:+ ($mode)}"; diff "$HOST/expected.txt" "$WORK/host.out" | head -5; cat "$WORK/host.err"; fail=1; host_ok=0
    fi
  done
  [ "$host_ok" = 0 ] || echo "ok   host ($(wc -l < "$HOST/expected.txt") lines)"
fi
# mixed-mode の最小の試験 (doc/object-model.md M-4): tcs2c --lib の生成 C の関数が VM の object を読み書きする
MIXED="$ROOT/tcsvm/tests/mixed"
TCS2C_DLL="$ROOT/tcs2c/bin/Debug/net10.0/tcs2c.dll"
"$DOTNET" build "$ROOT/tcs2c/tcs2c.csproj" -p:NuGetAudit=false -v:quiet -nologo >/dev/null
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
    || ! "${CC:-cc}" -std=gnu11 -O1 -g -I"$ROOT/tcsvm/vm" -DEDITOR_C="\"$WORK/editor.c\"" -o "$WORK/modules_test" \
      "$MODS/modules_test.c" "${VM_SRC[@]}" "$ROOT"/runtime/sort/*.c "$ROOT"/runtime/fmt/*.c "$ROOT"/runtime/math/*.c \
      "$ROOT"/runtime/utf8/*.c "$ROOT"/runtime/model/*.c -lm 2>> "$WORK/modules.err"; then
  echo "FAIL modules: build"; head -5 "$WORK/modules.err"; fail=1
else
  mods_ok=1
  for mode in "" --gc-stress; do
    if ! "$WORK/modules_test" "$WORK/content1.tcsb" "$WORK/content2.batch" $mode > "$WORK/modules.out" 2>&1 \
        || ! cmp -s "$WORK/modules.out" "$MODS/expected.txt"; then
      echo "FAIL modules${mode:+ ($mode)}"; diff "$MODS/expected.txt" "$WORK/modules.out" | head -5; fail=1; mods_ok=0
    fi
  done
  [ "$mods_ok" = 0 ] || echo "ok   modules (editor は C、content は VM で reload)"
fi
# watch の stream (tcs2vm --watch → vm_batch → tcsvm --reload-batch、restart_required)
mkdir -p "$WORK/watch"
if ! python3 -I "$HOST/watch_test.py" "$DOTNET" "$TCS2VM_DLL" "$TCSVM" "$WORK/watch"; then
  echo "FAIL watch"; fail=1
fi
exit "$fail"
