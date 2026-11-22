#!/usr/bin/env bash
# Fetches the ccache that CI uses to compile lub from a release with a pinned version, checks its hash, and adds it to $GITHUB_PATH.
# CCACHE_DIR is ~/.ccache (CI caches the same path).
set -euo pipefail
ver=4.14.2
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) name=linux-x86_64-glibc ext=tar.xz sum=bd1313f5316f594443dcb4113a4191c666ac6450e0cf016ff8cb8b080f46c160 ;;
  Linux-aarch64) name=linux-aarch64-glibc ext=tar.xz sum=1f3c155db005d3d1b99ce5c8ce40a1afd45507fef0168c36a6b0fd6e628d0168 ;;
  Darwin-*) name=darwin ext=tar.gz sum=cf240009630b605b775c7c5c7b7105a3b196c79f31af322c1557969aa28003fc ;;
  MINGW*-x86_64 | MSYS*-x86_64) name=windows-x86_64 ext=zip sum=6993ce645e53192cec592ee23186200b574ac4c59097ecde8d9cfd681b47ae84 ;;
  MINGW*-aarch64 | MSYS*-aarch64) name=windows-aarch64 ext=zip sum=47ea5a0ed661bea53962d8fb8ee9a1769092d979a6f7c73d3081499b3305b2ab ;;
  *)
    echo "install-ccache: unknown platform $(uname -s)-$(uname -m)" >&2
    exit 1
    ;;
esac
dir="${RUNNER_TEMP:-$(mktemp -d)}/ccache"
archive="$dir/ccache-$ver-$name.$ext"
mkdir -p "$dir"
curl -sSfL -o "$archive" "https://github.com/ccache/ccache/releases/download/v$ver/ccache-$ver-$name.$ext"
if command -v sha256sum >/dev/null; then
  echo "$sum  $archive" | sha256sum -c -
else
  echo "$sum  $archive" | shasum -a 256 -c -
fi
case "$ext" in
  zip) unzip -q -o "$archive" -d "$dir" ;;
  *) tar -xf "$archive" -C "$dir" ;;
esac
bin="$dir/ccache-$ver-$name"
"$bin/ccache" --version | head -1
ccache_dir="$HOME/.ccache"
if [[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]]; then
  bin="$(cygpath -w "$bin")"
  ccache_dir="$(cygpath -w "$ccache_dir")"
fi
if [[ -n "${GITHUB_PATH:-}" ]]; then
  echo "$bin" >>"$GITHUB_PATH"
  echo "CCACHE_DIR=$ccache_dir" >>"$GITHUB_ENV"
fi
