#!/usr/bin/env bash
# lider の native の拡張を build する (lub の player が src/Jamo.ref.cs の隣の src/Jamo.so を読む。include/lub/lub_vm_extension.h)。
# Jamo.so: かな漢字変換 (third_party/jamo) の session API (native/jamo/vm_jamo.c)。
# Fs.so: file system の操作 (native/fs/vm_fs.c)。
# Tcs.so: tcs の compiler (native/tcs/vm_tcs.c。tcs を tcs2c で C にした物を含む)。
# lider の player (scripts/build-player.sh) はどちらも静的に持つので、これは lub の player (lub) で動かすときだけ要る。
# lub の symbol は使わない (lub が関数の表を渡す)。
# 要るもの: cargo、dotnet (tcs を C にする)。
# env:
#   TARGET  Rust の target (既定: host)
#   CC      C compiler (既定: cc)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
jamo="$lub/third_party/jamo"
target="${TARGET:-$(rustc -vV | sed -n 's/^host: //p')}"
out="$root/build/native/$target"
mkdir -p "$out"
# jamo の Cargo.toml を触らずに staticlib を得る。build の出力は lider の側に置く
CARGO_TARGET_DIR="$out/cargo" cargo rustc --manifest-path "$jamo/Cargo.toml" \
  -p jamo-core --release --target "$target" --crate-type staticlib
"${CC:-cc}" -shared -fPIC -O2 \
  -I"$lub/include" -I"$lub/tcs/tcsvm/vm" -I"$jamo/jamo-core/include" \
  "$root/native/jamo/vm_jamo.c" "$out/cargo/$target/release/libjamo_core.a" \
  -lpthread -ldl -lm -o "$root/src/Jamo.so"
"${CC:-cc}" -shared -fPIC -O2 \
  -I"$lub/include" -I"$lub/tcs/tcsvm/vm" \
  "$root/native/fs/vm_fs.c" -o "$root/src/Fs.so"
# tcs を C にし (native/CMakeLists.txt と同じ)、tcs と一緒に配るファイルを header に埋め込む
tcs="$lub/tcs"
dotnet build "$tcs/tcs2c" -c Release -v q -nologo >/dev/null
self=("$tcs"/SelfHost/Compiler/*.cs "$tcs"/SelfHost/CBackend/*.cs "$tcs"/SelfHost/VmBackend/*.cs)
for f in "$tcs"/SelfHost/Tool/*.cs; do
  [ "$(basename "$f")" = TcsHost.cs ] || self+=("$f")
done
mkdir -p "$out/tcs"
dotnet "$tcs/tcs2c/bin/Release/net10.0/tcs2c.dll" --lib --ref "$tcs/SelfHost/Tool/TcsHost.cs" "${self[@]}" -o "$out/tcs/tcs.c"
cmake -DTINYSYSTEM="$tcs/TinySystem" -DOUT="$out/tcs/tcs_tool_files.h" -P "$root/native/tcs/embed.cmake"
"${CC:-cc}" -shared -fPIC -O2 -ffp-contract=off -fwrapv -w \
  -I"$lub/include" -I"$lub/tcs/tcsvm/vm" -I"$out/tcs" \
  "$root/native/tcs/vm_tcs.c" -lm -o "$root/src/Tcs.so"
echo "wrote src/Jamo.so, src/Fs.so and src/Tcs.so ($target)"
