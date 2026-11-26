#!/usr/bin/env bash
# tcsvm tests (docs/tcs/vm-design.md §16): runs tests/*.cs, the 6 samples, and the digest kernels on the VM (tcs vm → tcsvm),
# and checks that they match the expected stdout (tests/expected/*.txt, the expected strings of the samples, the fixed digests of the kernels).
# For inputs that the C backend (tcs c → cc) can run, the C stdout and success / failure must also match the VM.
# Fault tests (Fault*.cs) also check that the stdout up to the fault matches the expectation, that both the VM and C fail, and that the VM trace points to the C# file and line.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
# The .NET tcs (scripts/tcs.sh). Built once here unless the caller passes LUB_TCS
LUB_TCS="${LUB_TCS:-$("$ROOT/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
TCS="$ROOT/scripts/tcs.sh"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/tcsvm-tests.XXXXXX")"
trap 'rm -rf -- "$WORK"' EXIT

# The VM (src/tcs-vm/CMakeLists.txt): the tcsvm command, and the library that the test hosts below link
VM_BUILD="$ROOT/build-tcs-vm"
cmake -B "$VM_BUILD" -DCMAKE_BUILD_TYPE=Release -S "$ROOT/src/tcs-vm" >/dev/null
cmake --build "$VM_BUILD" --target tcsvm --parallel >/dev/null
VM_LIBS=(-I"$ROOT/src/tcs-vm" "$VM_BUILD/libtcs-vm.a" "$VM_BUILD/tcs-runtime/libtcs-runtime.a")

TCSVM="$VM_BUILD/tcsvm"
CC_CMD="${CC:-cc}"

# Inputs that the C backend cannot run (the C comparison is skipped and only the expected stdout is checked)
#   OutCalls: tcs c does not handle the discarded out of `int.TryParse(s, out _)` (unbound IL variable: _)
NO_C=" OutCalls "

# FNV-1a digest of a kernel's stdout (one float per line)
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
  # expect is file:<file with the expected stdout> / text:<expected stdout> / digest:<FNV-1a digest of stdout>
  local src="$1" entry="$2" name="$3" expect="$4" extra="${5:-}"
  local srcs=("$src")
  [ -z "$extra" ] || srcs+=("$extra")
  local vm_rc=0
  if ! "$TCS" vm --entry "$entry" "${srcs[@]}" -o "$WORK/$name.tcsb" 2> "$WORK/$name.emit.err"; then
    echo "FAIL $name: $(cat "$WORK/$name.emit.err")"
    fail=1
    return
  fi
  "$TCSVM" "$WORK/$name.tcsb" > "$WORK/$name.vm.out" 2> "$WORK/$name.vm.err" || vm_rc=$?
  # Under GC stress (a collection at every allocation site), stdout and success / failure must also be the same. Missing roots show up as output differences
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
    if ! "$TCS" c --entry "$entry" "${srcs[@]}" -o "$WORK/$name.c" 2> "$WORK/$name.c.err" \
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

for src in "$SCRIPT_DIR"/*.cs; do
  name="$(basename "$src" .cs)"
  run_case "$src" "$name" "$name" "file:$SCRIPT_DIR/expected/$name.txt"
done
# tcs samples: the same entry expressions and expected strings as SampleParityTests in tests/tcs-backends
run_sample() { # sample expr expected
  local name="sample_$1"
  printf 'public static class TcsEntry { public static void Main() { System.Console.WriteLine(%s); } }\n' "$2" \
    > "$WORK/$name.entry.cs"
  run_case "$ROOT/tests/tcs/samples/$1.cs" TcsEntry "$name" "text:$3" "$WORK/$name.entry.cs"
}
run_sample hello 'Hello.Greet("TinyC#") + "," + Hello.Add(2, 3).ToString()' 'Hello, TinyC#!,5'
run_sample game 'Battle.Run()' 'Dragon: HP=145 [attacking] | alive=2'
run_sample inventory 'Game.Test()' 'Items=4 Total=330 Best=Sword Shield=1'
run_sample entity 'EntitySample.Run()' 'Slime:enemy@6,2 HP=13'
run_sample statemachine 'StateMachineSample.Run()' 'open,open,locked,closed'
run_sample collision 'CollisionSample.Run()' 'hit,miss,hit'

# Digest kernels: the same fixed digests as tests/tcs-backends/verify-digests.sh.
# particles_struct has the same sequence of operations as particles, so the digest is the same
KERNELS="$ROOT/tests/tcs/DigestKernels"
run_case "$KERNELS/sprite_update.cs" SpriteUpdate sprite_update digest:e8814b32
run_case "$KERNELS/spawn_churn.cs" SpawnChurn spawn_churn digest:9274159d
run_case "$KERNELS/particles.cs" Particles particles digest:8bf97e09
run_case "$KERNELS/particles_struct.cs" ParticlesStruct particles_struct digest:8bf97e09

# Host integration (tests/host): host_test.c registers the stub.cs surface as host functions and calls Game's entries.
# With the Reject.cs module, checks that loading is rejected when the registration does not match the module's structs.
HOST="$SCRIPT_DIR/host"
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/host_test" "$HOST/host_test.c" "${VM_LIBS[@]}" -lm 2> "$WORK/host_test.err"; then
  echo "FAIL host: build"; head -5 "$WORK/host_test.err"; fail=1
elif ! "$TCS" vm --ref "$HOST/stub.cs" --export 'Game.*' "$HOST/Game.cs" -o "$WORK/host.tcsb" 2> "$WORK/host.emit.err" \
    || ! "$TCS" vm --ref "$HOST/stub.cs" --export 'Reject.*' "$HOST/Reject.cs" -o "$WORK/reject.tcsb" 2>> "$WORK/host.emit.err"; then
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
# (line, self) pairs from --trace (tests/trace): extracts them, excluding lines that were passed only with self being another object of the same type as the remembered object
TRACE="$SCRIPT_DIR/trace"
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/trace_test" "$TRACE/trace_test.c" "${VM_LIBS[@]}" -lm 2> "$WORK/trace_test.err"; then
  echo "FAIL trace: build"; head -5 "$WORK/trace_test.err"; fail=1
elif ! "$TCS" vm --ref "$TRACE/stub.cs" --export 'Game.*' --trace "$TRACE/Game.cs" "$TRACE/Game.cs" -o "$WORK/trace.tcsb" 2> "$WORK/trace.emit.err"; then
  echo "FAIL trace: $(cat "$WORK/trace.emit.err")"; fail=1
elif ! "$WORK/trace_test" "$WORK/trace.tcsb" > "$WORK/trace.out" 2>&1 || ! cmp -s "$WORK/trace.out" "$TRACE/expected.txt"; then
  echo "FAIL trace"; diff "$TRACE/expected.txt" "$WORK/trace.out" | head -5; fail=1
else
  echo "ok   trace (行, self) の組"
fi
# Viewing part of a large array (tests/inspect): tvm_inspect spends no effort on elements outside the requested range
INSPECT="$SCRIPT_DIR/inspect"
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/inspect_test" "$INSPECT/inspect_test.c" "${VM_LIBS[@]}" -lm 2> "$WORK/inspect_test.err"; then
  echo "FAIL inspect: build"; head -5 "$WORK/inspect_test.err"; fail=1
elif ! "$TCS" vm --export 'Game.*' "$INSPECT/Game.cs" -o "$WORK/inspect.tcsb" 2> "$WORK/inspect.emit.err"; then
  echo "FAIL inspect: $(cat "$WORK/inspect.emit.err")"; fail=1
elif ! "$WORK/inspect_test" "$WORK/inspect.tcsb" > "$WORK/inspect.out" 2>&1 || ! cmp -s "$WORK/inspect.out" "$INSPECT/expected.txt"; then
  echo "FAIL inspect"; diff "$INSPECT/expected.txt" "$WORK/inspect.out" | head -5; fail=1
else
  echo "ok   inspect (大きな配列の一部)"
fi
# Per-line cost (tests/profile): extracts the time and allocations of --trace lines and the GC pauses as fixed values using the test clock
PROF="$SCRIPT_DIR/profile"
if ! "${CC:-cc}" -std=c11 -O1 -g -ffp-contract=off -o "$WORK/profile_test" "$PROF/profile_test.c" "${VM_LIBS[@]}" -lm -pthread 2> "$WORK/profile_test.err"; then
  echo "FAIL profile: build"; head -5 "$WORK/profile_test.err"; fail=1
elif ! "$TCS" vm --ref "$PROF/stub.cs" --export 'Game.*' --trace "$PROF/Game.cs" "$PROF/Game.cs" "$PROF/Lib.cs" -o "$WORK/profile.tcsb" 2> "$WORK/profile.emit.err"; then
  echo "FAIL profile: $(cat "$WORK/profile.emit.err")"; fail=1
elif ! "$WORK/profile_test" "$WORK/profile.tcsb" > "$WORK/profile.out" 2>&1 || ! cmp -s "$WORK/profile.out" "$PROF/expected.txt"; then
  echo "FAIL profile"; diff "$PROF/expected.txt" "$WORK/profile.out" | head -10; fail=1
else
  echo "ok   profile (行ごとの負荷)"
fi
# Minimal mixed-mode test (docs/tcs/object-model.md M-4): functions in the C generated by tcs2c --lib read and write VM objects
MIXED="$SCRIPT_DIR/mixed"
if ! "$TCS" c --lib -o "$WORK/mixed.c" "$MIXED/Mixed.cs" 2> "$WORK/mixed.err" \
    || ! "$TCS" vm --export 'Game.*' "$MIXED/Mixed.cs" -o "$WORK/mixed.tcsb" 2>> "$WORK/mixed.err" \
    || ! "${CC:-cc}" -std=gnu11 -O1 -g -DMIXED_C="\"$WORK/mixed.c\"" -o "$WORK/mixed_test" "$MIXED/mixed_test.c" "${VM_LIBS[@]}" -lm 2>> "$WORK/mixed.err"; then
  echo "FAIL mixed: build"; head -5 "$WORK/mixed.err"; fail=1
elif ! "$WORK/mixed_test" "$WORK/mixed.tcsb" > "$WORK/mixed.out" 2>&1 || ! cmp -s "$WORK/mixed.out" "$MIXED/expected.txt"; then
  echo "FAIL mixed"; diff "$MIXED/expected.txt" "$WORK/mixed.out" | head -5; fail=1
else
  echo "ok   mixed (tcs2c の関数が VM の object を読み書きする)"
fi
# Mixing per module (docs/tcs/object-model.md §10): the editor is C from tcs2c --lib --vm-host, and the content is reloaded in the VM
MODS="$SCRIPT_DIR/modules"
cp "$MODS/ContentV1.cs.txt" "$WORK/Content.cs"
cp "$MODS/ContentV2.cs.txt" "$WORK/Content2.cs"
if ! "$TCS" c --lib --vm-host -o "$WORK/editor.c" "$MODS/Editor.cs" 2> "$WORK/modules.err" \
    || ! "$TCS" vm --ref "$MODS/Editor.cs" --export 'Game.*' "$WORK/Content.cs" -o "$WORK/content1.tcsb" 2>> "$WORK/modules.err" \
    || ! "$TCS" vm --ref "$MODS/Editor.cs" --export 'Game.*' --reload-from "$WORK/Content.cs" \
      --batch "$WORK/content2.batch" "$WORK/Content2.cs" -o "$WORK/content2.tcsb" 2>> "$WORK/modules.err" \
    ; then
  echo "FAIL modules: build"; head -5 "$WORK/modules.err"; fail=1
else
  # shared heap (object-model §10.2): normal, and with tcs2c's GC stress (fills the nursery with poison values and collects everything every frame)
  mods_ok=1
  for mode in "" -DTCS_GC_STRESS; do
    if ! "${CC:-cc}" -std=gnu11 -O1 -g $mode -DEDITOR_C="\"$WORK/editor.c\"" -o "$WORK/modules_test" \
        "$MODS/modules_test.c" "${VM_LIBS[@]}" -lm 2>> "$WORK/modules.err" \
        || ! "$WORK/modules_test" "$WORK/content1.tcsb" "$WORK/content2.batch" > "$WORK/modules.out" 2>&1 \
        || ! cmp -s "$WORK/modules.out" "$MODS/expected.txt"; then
      echo "FAIL modules${mode:+ ($mode)}"; diff "$MODS/expected.txt" "$WORK/modules.out" | head -5; head -3 "$WORK/modules.err"
      fail=1; mods_ok=0
    fi
  done
  [ "$mods_ok" = 0 ] || echo "ok   modules (editor は C、content は VM で reload。shared heap)"
fi
# watch stream (tcs vm --watch → vm_batch → tcsvm --reload-batch, restart_required)
mkdir -p "$WORK/watch"
if ! python3 -I "$HOST/watch_test.py" "$TCS" "$TCSVM" "$WORK/watch"; then
  echo "FAIL watch"; fail=1
fi
exit "$fail"
