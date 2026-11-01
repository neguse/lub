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
run_case() { # path entry
  local src="$1" entry="$2" name
  name="$(basename "$src" .cs)"
  "$DOTNET" "$TR_DLL" "$src" -o "$WORK/$name.lua" >/dev/null 2>&1
  printf '\n%s.Main()\n' "$entry" >> "$WORK/$name.lua"
  local lua_rc=0 vm_rc=0
  "$LUA32" "$WORK/$name.lua" > "$WORK/$name.lua.out" 2> "$WORK/$name.lua.err" || lua_rc=$?
  "$DOTNET" "$TCS2VM_DLL" --entry "$entry" "$src" -o "$WORK/$name.tcsb"
  "$TCSVM" "$WORK/$name.tcsb" > "$WORK/$name.vm.out" 2> "$WORK/$name.vm.err" || vm_rc=$?
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
      if [ "$vm_rc" -eq 0 ] || ! grep -q "at $entry\.[A-Za-z]* ($name.cs:[0-9]*)" "$WORK/$name.vm.err"; then
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
run_case "$ROOT/Transpiler.Tests/DigestKernels/sprite_update.cs" SpriteUpdate
run_case "$ROOT/Transpiler.Tests/DigestKernels/spawn_churn.cs" SpawnChurn
run_case "$ROOT/Transpiler.Tests/DigestKernels/particles.cs" Particles
run_case "$ROOT/Transpiler.Tests/DigestKernels/particles_struct.cs" ParticlesStruct
exit "$fail"
