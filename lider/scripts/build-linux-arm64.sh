#!/usr/bin/env bash
# Builds the lider player for Linux arm64 (aarch64, glibc) on x86_64 Linux.
#   scripts/build-linux-arm64.sh <sysroot>
# The compiler is zig (zig cc). jamo-core is built with Rust's aarch64-unknown-linux-gnu (rustup target add aarch64-unknown-linux-gnu).
# Put the arm64 shared libraries copied from the target machine in <sysroot>/lib (used for linking and to determine the library names SDL opens at runtime):
#   libvulkan.so.1 libasound.so.2 libX11.so.6 libXext.so.6 libXcursor.so.1 libXi.so.6 libXfixes.so.3 libXrandr.so.2
# Headers are copied from this machine's /usr/include to <sysroot>/include (Vulkan, ALSA, X11, xcb; they do not depend on the machine type).
# The output is build/linux-arm64/player/lider_player. The Slang libraries needed at runtime are in build/linux-arm64/slang/lib.
# SDL and the OpenXR loader are statically linked into the player.
# env: ZIG    zig executable (default zig)
#      GLIBC  glibc version to link against (default 2.38). Must be at most the target machine's glibc and at least the version the sysroot libraries need
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
sysroot="$(cd "${1:?usage: build-linux-arm64.sh <sysroot>}" && pwd)"
zig="$(command -v "${ZIG:-zig}")"
out="$root/build/linux-arm64"
mkdir -p "$out/toolchain" "$sysroot/include"

# Adds unversioned names so the linker can find the libraries by name
for lib in "$sysroot"/lib/lib*.so.*; do
  name="$(basename "$lib")"
  ln -sf "$name" "$sysroot/lib/${name%%.so.*}.so"
done
for header in vulkan vk_video alsa X11 xcb; do
  [ -e "$sysroot/include/$header" ] || cp -r "/usr/include/$header" "$sysroot/include/"
done

# Wraps zig in the form of the compiler and ar that CMake calls
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
# The zig linker fails on the --dependency-file that CMake adds
set(CMAKE_LINK_DEPENDS_USE_LINKER OFF)
TOOLCHAIN

# Keeps pkg-config from picking up this machine's (x86_64) libraries
export PKG_CONFIG_LIBDIR=/nonexistent
cmake -S "$lub" -B "$out/lub" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE="$out/toolchain/toolchain.cmake" \
  -DSLANG_ROOT="$out/slang" \
  -DVulkan_LIBRARY="$sysroot/lib/libvulkan.so" -DVulkan_INCLUDE_DIR="$sysroot/include" \
  -DSDL_SHARED=OFF -DSDL_STATIC=ON -DSDL_TESTS=OFF -DSDL_UNIX_CONSOLE_BUILD=ON \
  -DSDL_X11=ON -DSDL_X11_SHARED=ON -DSDL_X11_XSCRNSAVER=OFF -DSDL_X11_XTEST=OFF -DSDL_WAYLAND=OFF \
  -DDYNAMIC_LOADER=OFF -DBUILD_WITH_XCB_HEADERS=OFF -DBUILD_WITH_WAYLAND_HEADERS=OFF
cmake --build "$out/lub" --target lub_vm_player
# find_package(lub) looks for the lub build directory on this machine's side
cmake -S "$root/native" -B "$out/player" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$out/toolchain/toolchain.cmake" -DCMAKE_FIND_ROOT_PATH_MODE_PACKAGE=BOTH \
  -Dlub_DIR="$out/lub" -DJAMO_TARGET=aarch64-unknown-linux-gnu
cmake --build "$out/player" --target lider_player
echo "built $out/player/lider_player"
