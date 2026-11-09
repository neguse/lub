#!/bin/bash
# tcs (SelfHost/Compiler、SelfHost/CBackend、SelfHost/VmBackend、SelfHost/Tool) を C にして build し、固定点を確かめる。
#   C: .NET の tcs (同じソースを .NET で動かしたもの) が tcs を C にし (out/tcs.c)、Native/host.c と build した実行ファイル (out/tcs)
#   固定点: out/tcs が同じソースを C にした結果 (out/stageC.c) が out/tcs.c と byte で一致すること。
#           out/tcs.c から作った tcs が同じ C を作るので、次の世代も同じ C になる
#   .NET と C: tcs vm の .tcsb と tcs check の診断と終了コードが、.NET の tcs と out/tcs で byte で一致すること
# 出力は SelfHost/out/ (git に入れない)。out/ には tcs と一緒に配るファイル (runtime/、lib/) も置き、out/tcs はそこから読む。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/SelfHost/out"
TCS="$ROOT/Transpiler/bin/Debug/net10.0/Transpiler.dll"
CC="${CC:-cc}"
mkdir -p "$OUT/runtime" "$OUT/lib/TinySystem"
unset TCS_HOME

# 成功したときの出力は出さず、失敗したときだけ stderr を見せる
run() {
  if ! "$@" 2>"$OUT/last.log"; then
    cat "$OUT/last.log" >&2
    exit 1
  fi
}

same() {
  if cmp -s "$1" "$2"; then
    echo "$3: same"
  else
    echo "$3: DIFFERENT ($1 != $2)" >&2
    diff "$1" "$2" | head -40 >&2 || true
    exit 1
  fi
}

if [ ! -f "$TCS" ] || [ -n "${TCS_BUILD:-}" ]; then
  dotnet build "$ROOT/Transpiler" -v q -nologo >/dev/null
fi
for d in core math fmt model utf8; do
  mkdir -p "$OUT/runtime/$d"
  cp "$ROOT/runtime/$d"/*.c "$ROOT/runtime/$d"/*.h "$OUT/runtime/$d/"
done
cp "$ROOT/TinySystem/Linq.cs" "$ROOT/TinySystem/ValueTuple.cs" "$OUT/lib/TinySystem/"

SRC=("$ROOT"/SelfHost/Compiler/*.cs "$ROOT"/SelfHost/CBackend/*.cs "$ROOT"/SelfHost/VmBackend/*.cs)
for f in "$ROOT"/SelfHost/Tool/*.cs; do
  [ "$(basename "$f")" = TcsHost.cs ] || SRC+=("$f")
done
ARGS=(--ref "$ROOT/SelfHost/Tool/TcsHost.cs" "${SRC[@]}")

run dotnet "$TCS" c --lib "${ARGS[@]}" -o "$OUT/tcs.c"
"$CC" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard -I "$OUT" "$ROOT/SelfHost/Native/host.c" -o "$OUT/tcs" -lm
echo "C: .NET -> $OUT/tcs.c -> $OUT/tcs"

t0=$SECONDS
run "$OUT/tcs" c --lib "${ARGS[@]}" -o "$OUT/stageC.c"
echo "tcs c: $OUT/tcs -> $OUT/stageC.c ($((SECONDS - t0)) s)"
same "$OUT/tcs.c" "$OUT/stageC.c" "fixed point (tcs c)"

# tcs VM の .tcsb (tcs vm)
KERNEL="$ROOT/Transpiler.Tests/DigestKernels/particles.cs"
run dotnet "$TCS" vm --entry Particles "$KERNEL" -o "$OUT/vm.dotnet.tcsb"
run "$OUT/tcs" vm --entry Particles "$KERNEL" -o "$OUT/vm.stageC.tcsb"
same "$OUT/vm.dotnet.tcsb" "$OUT/vm.stageC.tcsb" ".NET and C (tcs vm)"

# tcs check の診断と終了コード (tcs 自身)
set +e
dotnet "$TCS" check "${ARGS[@]}" >"$OUT/check.dotnet.txt" 2>&1
echo "exit $?" >>"$OUT/check.dotnet.txt"
"$OUT/tcs" check "${ARGS[@]}" >"$OUT/check.stageC.txt" 2>&1
echo "exit $?" >>"$OUT/check.stageC.txt"
set -e
same "$OUT/check.dotnet.txt" "$OUT/check.stageC.txt" ".NET and C (tcs check)"
