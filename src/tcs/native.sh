#!/bin/bash
# Builds the C tcs (src/tcs/build-native.sh) into src/tcs/out/native/ (not checked into git) and checks it against the .NET tcs.
# The built tcs must produce the same output as the .NET tcs: check diagnostics and exit codes for samples and tcs itself,
# C for samples (tcs c), tcs VM .tcsb for samples and tcs itself (tcs vm), reload batches (tcs vm --reload-from), and file operations.
# Also checks that the C tcs produces its own C (tcs.c) identically (fixed point).
# CC selects the C compiler (see build-native.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/src/tcs/out/native"
EXE=""
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) EXE=".exe" ;;
esac
mkdir -p "$OUT/check"
unset TCS_HOME

# The .NET tcs (scripts/tcs.sh). Built once here unless the caller passes LUB_TCS
LUB_TCS="${LUB_TCS:-$("$ROOT/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
DOTNET_TCS=("$ROOT/scripts/tcs.sh")

cd "$ROOT"
src/tcs/build-native.sh "$OUT"
mapfile -t ARGS < <(src/tcs/sources.sh)
NATIVE="$OUT/tcs$EXE"
echo "native: $NATIVE ($("$NATIVE" --version))"

fail=0
same() {
  if cmp -s "$1" "$2"; then
    echo "  same: $3"
  else
    echo "  DIFFERENT: $3" >&2
    diff "$1" "$2" | head -20 >&2 || true
    fail=1
  fi
}

# Compares check diagnostics and exit codes (stdout, stderr, and the exit code in one file)
check_same() {
  local name="$1"
  shift
  set +e
  "${DOTNET_TCS[@]}" check "$@" >"$OUT/check/$name.check.dotnet.txt" 2>&1
  echo "exit $?" >>"$OUT/check/$name.check.dotnet.txt"
  "$NATIVE" check "$@" >"$OUT/check/$name.check.native.txt" 2>&1
  echo "exit $?" >>"$OUT/check/$name.check.native.txt"
  set -e
  same "$OUT/check/$name.check.dotnet.txt" "$OUT/check/$name.check.native.txt" "tcs check $name"
}

# samples: check, C (tcs c), tcs VM .tcsb (tcs vm; exports the static methods of the entry class)
for pair in hello:Hello game:Battle inventory:Game entity:EntitySample statemachine:StateMachineSample collision:CollisionSample host_api_game:HostApiSample; do
  name="${pair%%:*}"
  cls="${pair##*:}"
  f="tests/tcs/samples/$name.cs"
  refs=()
  [ "$name" = host_api_game ] && refs=(--ref tests/tcs/samples/host_api_stub.cs)
  check_same "$name" "$f" ${refs[@]+"${refs[@]}"}
  "${DOTNET_TCS[@]}" c "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.dotnet.c" 2>/dev/null
  "$NATIVE" c "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.native.c" 2>/dev/null
  same "$OUT/check/$name.dotnet.c" "$OUT/check/$name.native.c" "tcs c $f"
  "${DOTNET_TCS[@]}" vm --export "$cls.*" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --export "$cls.*" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.native.tcsb" 2>/dev/null
  same "$OUT/check/$name.dotnet.tcsb" "$OUT/check/$name.native.tcsb" "tcs vm $f"
done

# check error diagnostics
printf 'public class Bad { public static int F() { try { return 1; } finally { } } }\n' > src/tcs/out/native/check/Bad.cs
check_same bad src/tcs/out/native/check/Bad.cs

# check of tcs itself
start=$(date +%s)
check_same self "${ARGS[@]}"
echo "  tcs check itself: $(( $(date +%s) - start )) s"

# .tcsb of tcs itself (functions reachable from the CLI entry point)
"${DOTNET_TCS[@]}" vm --export 'Tcs.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.dotnet.tcsb" 2>/dev/null
start=$(date +%s)
"$NATIVE" vm --export 'Tcs.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.native.tcsb" 2>/dev/null
echo "  tcs vm itself: $(( $(date +%s) - start )) s"
same "$OUT/check/self.dotnet.tcsb" "$OUT/check/self.native.tcsb" "tcs vm itself"

# tcs VM .tcsb. The digest kernel (entry is Main)
for k in particles:Particles spawn_churn:SpawnChurn sprite_update:SpriteUpdate; do
  file="tests/tcs/DigestKernels/${k%%:*}.cs"
  "${DOTNET_TCS[@]}" vm --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.native.tcsb" 2>/dev/null
  same "$OUT/check/${k%%:*}.dotnet.tcsb" "$OUT/check/${k%%:*}.native.tcsb" "tcs vm $file"
done

# Reload module and batch (tcs vm --reload-from)
HOST_TESTS=tests/tcs-vm/host
"${DOTNET_TCS[@]}" vm --export 'Game.*' --reload-from "$HOST_TESTS/ReloadV1.cs.txt" "$HOST_TESTS/ReloadV2.cs.txt" \
  -o "$OUT/check/reload.dotnet.tcsb" --batch "$OUT/check/reload.dotnet.batch" 2>/dev/null
"$NATIVE" vm --export 'Game.*' --reload-from "$HOST_TESTS/ReloadV1.cs.txt" "$HOST_TESTS/ReloadV2.cs.txt" \
  -o "$OUT/check/reload.native.tcsb" --batch "$OUT/check/reload.native.batch" 2>/dev/null
same "$OUT/check/reload.dotnet.batch" "$OUT/check/reload.native.batch" "tcs vm --reload-from (batch)"

# File operations: the C implementation (Native/files.c) and the .NET one (Dotnet/Files.cs) behave the same.
# Each tcs runs the same command in its own copy of the same files, and the output, the exit code and the files left behind are compared
FILES="$OUT/check/files"
rm -rf "$FILES"
for side in dotnet native; do
  mkdir -p "$FILES/$side"
  printf 'public static class A { public static int F() { return 1; } }\n' > "$FILES/$side/a.cs"
  # A UTF-8 byte order mark is not part of the text
  printf '\357\273\277public static class Bom { public static int F() { return 2; } }\n' > "$FILES/$side/bom.cs"
  mkdir "$FILES/$side/dir.cs"
done
# files_run NAME ARGS...: runs tcs with ARGS in each copy. The output and the exit code go to NAME.<side>.txt
files_run() {
  local name="$1" side
  shift
  for side in dotnet native; do
    set +e
    if [ "$side" = dotnet ]; then
      (cd "$FILES/$side" && "${DOTNET_TCS[@]}" "$@") > "$FILES/$name.$side.txt" 2>&1
    else
      (cd "$FILES/$side" && "$NATIVE" "$@") > "$FILES/$name.$side.txt" 2>&1
    fi
    echo "exit $?" >> "$FILES/$name.$side.txt"
    set -e
  done
}
files_same() {
  files_run "$@"
  same "$FILES/$1.dotnet.txt" "$FILES/$1.native.txt" "files: $1"
}
# Only the exit code (the reason of a failed write names the OS error, in words that differ between C and .NET)
files_same_exit() {
  files_run "$@"
  local side
  for side in dotnet native; do tail -n 1 "$FILES/$1.$side.txt" > "$FILES/$1.$side.exit"; done
  same "$FILES/$1.dotnet.exit" "$FILES/$1.native.exit" "files: $1 (exit code)"
}
files_same_file() {
  same "$FILES/dotnet/$1" "$FILES/native/$1" "files: $1"
}
files_same check-bom check bom.cs
files_same missing-input c missing.cs -o out.c
files_same directory-input c dir.cs -o out.c
files_same output-is-input c a.cs -o a.cs
files_same_file a.cs
# The directories of the output path are made
files_same new-directories vm --export 'A.*' a.cs -o new/deep/a.tcsb
files_same_file new/deep/a.tcsb
# No temporary file is left next to the output
for side in dotnet native; do (cd "$FILES/$side/new/deep" && ls -a) > "$FILES/left.$side.txt"; done
same "$FILES/left.dotnet.txt" "$FILES/left.native.txt" "files: nothing left next to the output"
if [ -z "$EXE" ]; then
  # A link to an input is the same file as the input
  for side in dotnet native; do ln -s a.cs "$FILES/$side/link.cs"; done
  files_same output-is-link-to-input c a.cs -o link.cs
  files_same_file a.cs
  # Replacing an output keeps its permissions
  for side in dotnet native; do
    printf 'old\n' > "$FILES/$side/keep.c"
    chmod 640 "$FILES/$side/keep.c"
  done
  files_same replace c a.cs -o keep.c
  files_same_file keep.c
  for side in dotnet native; do ls -l "$FILES/$side/keep.c" | cut -c1-10 > "$FILES/mode.$side.txt"; done
  same "$FILES/mode.dotnet.txt" "$FILES/mode.native.txt" "files: replacing keeps the permissions"
  # An output that cannot be written fails, and stays as it was
  for side in dotnet native; do
    printf 'locked\n' > "$FILES/$side/locked.c"
    chmod 444 "$FILES/$side/locked.c"
  done
  files_same_exit read-only-output c a.cs -o locked.c
  files_same_file locked.c
fi

start=$(date +%s)
"$NATIVE" c --lib "${ARGS[@]}" -o "$OUT/check/self.native.c" 2>/dev/null
echo "  tcs c itself: $(( $(date +%s) - start )) s"
same "$OUT/tcs.c" "$OUT/check/self.native.c" "tcs c itself (fixed point)"

if [ "$fail" != 0 ]; then
  echo "native tcs differs from the .NET tcs" >&2
  exit 1
fi
echo "native tcs matches the .NET tcs"
