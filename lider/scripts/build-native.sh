#!/usr/bin/env bash
# Builds lider's native extensions (the lub player loads src/Jamo.so next to src/Jamo.ref.cs; include/lub/lub_vm_extension.h).
# Jamo.so: the kana-kanji conversion (third_party/jamo) session API (native/jamo/vm_jamo.c).
# Fs.so: file system operations (native/fs/vm_fs.c).
# Tcs.so: the tcs compiler (native/tcs/vm_tcs.c; includes tcs converted to C with `tcs c`).
# The lider player (scripts/build-player.sh) has both built in statically, so this is only needed when running with the lub player (lub).
# Does not use lub symbols (lub passes a function table).
# Requirements: cargo, dotnet (to convert tcs to C).
# env:
#   TARGET  Rust target (default: host)
#   CC      C compiler (default: cc)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
jamo="$lub/third_party/jamo"
target="${TARGET:-$(rustc -vV | sed -n 's/^host: //p')}"
out="$root/build/native/$target"
mkdir -p "$out"
# Gets a staticlib without touching jamo's Cargo.toml. The build output goes on the lider side
CARGO_TARGET_DIR="$out/cargo" cargo rustc --manifest-path "$jamo/Cargo.toml" \
  -p jamo-core --release --target "$target" --crate-type staticlib
"${CC:-cc}" -shared -fPIC -O2 \
  -I"$lub/include" -I"$lub/src/tcs-vm" -I"$jamo/jamo-core/include" \
  "$root/native/jamo/vm_jamo.c" "$out/cargo/$target/release/libjamo_core.a" \
  -lpthread -ldl -lm -o "$root/src/Jamo.so"
"${CC:-cc}" -shared -fPIC -O2 \
  -I"$lub/include" -I"$lub/src/tcs-vm" \
  "$root/native/fs/vm_fs.c" -o "$root/src/Fs.so"
# Converts tcs to C (same as native/CMakeLists.txt) and embeds the files shipped with tcs in a header
"$lub/src/tcs/to-c.sh" "$out/tcs/tcs.c"
cmake -DLUB_ROOT="$lub" -DOUT="$out/tcs/tcs_tool_files.h" -P "$root/native/tcs/embed.cmake"
mapfile -t tcs_cflags < <("$lub/src/tcs/cflags.sh")
"${CC:-cc}" -shared -fPIC "${tcs_cflags[@]}" -w \
  -I"$lub/include" -I"$lub/src/tcs-vm" -I"$out/tcs" -I"$lub/src/tcs/Native" \
  "$root/native/tcs/vm_tcs.c" -lm -o "$root/src/Tcs.so"
echo "wrote src/Jamo.so, src/Fs.so and src/Tcs.so ($target)"
