#!/usr/bin/env bash
# lider の player を、x86_64 の Linux の上で、Linux arm64 (aarch64、glibc) 向けに build する。
#   scripts/build-linux-arm64.sh <sysroot>
# コンパイラは zig (zig cc)。jamo-core は Rust の aarch64-unknown-linux-gnu で作る (rustup target add aarch64-unknown-linux-gnu)。
# <sysroot>/lib に、動かす機械から写した arm64 の共有 library を置く (link と、SDL が実行時に開く library の名前を決めるのに使う):
#   libvulkan.so.1 libasound.so.2 libX11.so.6 libXext.so.6 libXcursor.so.1 libXi.so.6 libXfixes.so.3 libXrandr.so.2
# header は、この機械の /usr/include から <sysroot>/include に写す (Vulkan、ALSA、X11、xcb。機械の種類によらない)。
# 出力は build/linux-arm64/player/lider_player。実行時に要る Slang の library は build/linux-arm64/slang/lib にある。
# SDL と OpenXR の loader は、player に静的に link する。
# env: ZIG    zig の実行 file (既定 zig)
#      GLIBC  link する glibc の版 (既定 2.38)。動かす機械の glibc 以下で、sysroot の library が要る版以上にする
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
sysroot="$(cd "${1:?usage: build-linux-arm64.sh <sysroot>}" && pwd)"
zig="$(command -v "${ZIG:-zig}")"
out="$root/build/linux-arm64"
mkdir -p "$out/toolchain" "$sysroot/include"

# link のときに名前で探せるよう、版の付かない名前を足す
for lib in "$sysroot"/lib/lib*.so.*; do
  name="$(basename "$lib")"
  ln -sf "$name" "$sysroot/lib/${name%%.so.*}.so"
done
for header in vulkan vk_video alsa X11 xcb; do
  [ -e "$sysroot/include/$header" ] || cp -r "/usr/include/$header" "$sysroot/include/"
done

# zig を、CMake が呼ぶコンパイラと ar の形にする
for tool in cc c++; do
  printf '#!/bin/sh\nexec %s %s -target aarch64-linux-gnu.%s "$@"\n' "$zig" "$tool" "${GLIBC:-2.38}" >"$out/toolchain/zig-$tool"
done
for tool in ar ranlib; do
  printf '#!/bin/sh\nexec %s %s "$@"\n' "$zig" "$tool" >"$out/toolchain/zig-$tool"
done
chmod +x "$out"/toolchain/zig-*
cat >"$out/toolchain/toolchain.cmake" <<TOOLCHAIN
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)
set(CMAKE_C_COMPILER $out/toolchain/zig-cc)
set(CMAKE_CXX_COMPILER $out/toolchain/zig-c++)
set(CMAKE_AR $out/toolchain/zig-ar CACHE FILEPATH "")
set(CMAKE_RANLIB $out/toolchain/zig-ranlib CACHE FILEPATH "")
set(CMAKE_FIND_ROOT_PATH $sysroot)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
# zig の linker は、CMake が付ける --dependency-file で落ちる
set(CMAKE_LINK_DEPENDS_USE_LINKER OFF)
TOOLCHAIN

# この機械 (x86_64) の library を pkg-config から拾わせない
export PKG_CONFIG_LIBDIR=/nonexistent
cmake -S "$lub" -B "$out/lub" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE="$out/toolchain/toolchain.cmake" \
  -DSLANG_ROOT="$out/slang" \
  -DVulkan_LIBRARY="$sysroot/lib/libvulkan.so" -DVulkan_INCLUDE_DIR="$sysroot/include" \
  -DSDL_SHARED=OFF -DSDL_STATIC=ON -DSDL_TESTS=OFF -DSDL_UNIX_CONSOLE_BUILD=ON \
  -DSDL_X11=ON -DSDL_X11_SHARED=ON -DSDL_X11_XSCRNSAVER=OFF -DSDL_X11_XTEST=OFF -DSDL_WAYLAND=OFF \
  -DDYNAMIC_LOADER=OFF -DBUILD_WITH_XCB_HEADERS=OFF -DBUILD_WITH_WAYLAND_HEADERS=OFF
cmake --build "$out/lub" --target lub_vm_player
# find_package(lub) は、lub の build のディレクトリを、この機械の側から探す
cmake -S "$root/native" -B "$out/player" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$out/toolchain/toolchain.cmake" -DCMAKE_FIND_ROOT_PATH_MODE_PACKAGE=BOTH \
  -Dlub_DIR="$out/lub" -DJAMO_TARGET=aarch64-unknown-linux-gnu
cmake --build "$out/player" --target lider_player
echo "built $out/player/lider_player"
