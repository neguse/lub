#!/usr/bin/env bash
# lider の native の拡張を build する (lub の player が src/Jamo.ref.cs の隣の src/Jamo.so を読む。include/lub/lub_vm_extension.h)。
# Jamo.so: かな漢字変換 (third_party/jamo) の session API (native/jamo/vm_jamo.c)。
# Fs.so: file system の操作 (native/fs/vm_fs.c)。
# lider の player (scripts/build-player.sh) はどちらも静的に持つので、これは lub の player (lub) で動かすときだけ要る。
# lub の symbol は使わない (lub が関数の表を渡す)。
# 要るもの: cargo。
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
echo "wrote src/Jamo.so and src/Fs.so ($target)"
