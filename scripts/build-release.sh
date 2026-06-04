#!/usr/bin/env bash
set -euo pipefail

build_dir="build-release-linux"
target="lub"
configure_only=0
no_configure=0
download_timeout_sec=1200
dep_roots=("build-release/_deps" "build/_deps")

lua_version="5.5.0"
lua_sha256="57ccc32bbbd005cab75bcc52444052535af691789dba2b9016d5c50640d68b3d"
lua_sf_sha256="98d99ea54561843f36b5edb86255824fc81d072c42f22ae18f873eb0d0c2a05e"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
  cat <<EOF
Usage: bash scripts/build-release.sh [options]

Options:
  --build-dir DIR                 Build directory (default: build-release-linux)
  --target NAME                   CMake target (default: lub)
  --configure-only                Configure but do not build
  --no-configure                  Build from an existing configure step
  --dependency-source-root DIR    Dependency source root; can be repeated
  --download-timeout-sec SEC      Download timeout per URL (default: 1200)
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build-dir)
      build_dir="$2"
      shift 2
      ;;
    --target)
      target="$2"
      shift 2
      ;;
    --configure-only)
      configure_only=1
      shift
      ;;
    --no-configure)
      no_configure=1
      shift
      ;;
    --dependency-source-root)
      dep_roots+=("$2")
      shift 2
      ;;
    --download-timeout-sec)
      download_timeout_sec="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

abs_path() {
  local path="$1"
  if [[ "$path" = /* ]]; then
    printf '%s\n' "$path"
  else
    printf '%s\n' "$repo_root/$path"
  fi
}

build_path="$(abs_path "$build_dir")"

find_dependency_source() {
  local subdir="$1"
  local marker="$2"
  local root candidate
  for root in "${dep_roots[@]}"; do
    candidate="$(abs_path "$root")/$subdir"
    if [[ -f "$candidate/$marker" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

hash_ok() {
  local file="$1"
  local expected="$2"
  [[ -f "$file" ]] || return 1
  local actual
  actual="$(sha256sum "$file" | awk '{print $1}')"
  [[ "$actual" == "$expected" ]]
}

download_file() {
  local url="$1"
  local out="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -L --fail --connect-timeout "$download_timeout_sec" \
      --max-time "$download_timeout_sec" \
      -A "lub-build-release" \
      -o "$out" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget --timeout="$download_timeout_sec" --tries=1 \
      --user-agent="lub-build-release" \
      -O "$out" "$url"
  else
    echo "curl or wget is required to fetch Lua sources" >&2
    return 1
  fi
}

ensure_lua_source() {
  local existing
  if existing="$(find_dependency_source "lua-src" "src/lapi.c")"; then
    printf '%s\n' "$existing"
    return 0
  fi

  local deps_dir="$build_path/_deps"
  local lua_source="$deps_dir/lua-src"
  local archive="$deps_dir/lua-$lua_version.tar.gz"
  local download="$archive.download"
  local extract_dir="$deps_dir/lua-extract"
  mkdir -p "$deps_dir"

  local have_archive=0
  if hash_ok "$archive" "$lua_sha256" || hash_ok "$archive" "$lua_sf_sha256"; then
    have_archive=1
  fi

  if [[ "$have_archive" -eq 0 ]]; then
    local urls=(
      "https://www.lua.org/ftp/lua-$lua_version.tar.gz"
      "https://sourceforge.net/projects/lua.mirror/files/v$lua_version/Lua%20$lua_version%20source%20code.tar.gz/download"
      "https://fossies.org/linux/misc/lua-$lua_version.tar.gz"
    )
    local url
    for url in "${urls[@]}"; do
      echo "fetching Lua $lua_version from $url" >&2
      rm -f "$download"
      if download_file "$url" "$download"; then
        if hash_ok "$download" "$lua_sha256" || hash_ok "$download" "$lua_sf_sha256"; then
          mv -f "$download" "$archive"
          have_archive=1
          break
        fi
        echo "Lua archive hash mismatch from $url" >&2
      fi
    done
  fi

  if [[ "$have_archive" -eq 0 ]]; then
    echo "failed to fetch Lua $lua_version sources" >&2
    exit 1
  fi

  rm -rf "$extract_dir" "$lua_source"
  mkdir -p "$extract_dir"
  (cd "$extract_dir" && cmake -E tar xzf "$archive")
  local lapi
  lapi="$(find "$extract_dir" -name lapi.c -print -quit)"
  if [[ -z "$lapi" ]]; then
    echo "Lua archive did not contain lapi.c" >&2
    exit 1
  fi

  local src_dir
  src_dir="$(dirname "$lapi")"
  if [[ "$(basename "$src_dir")" == "src" ]]; then
    mv "$(dirname "$src_dir")" "$lua_source"
  else
    mkdir -p "$lua_source/src"
    cp -R "$src_dir"/. "$lua_source/src/"
  fi
  rm -rf "$extract_dir"
  printf '%s\n' "$lua_source"
}

if [[ "$no_configure" -eq 0 ]]; then
  configure_args=(-S "$repo_root" -B "$build_path" -DCMAKE_BUILD_TYPE=Release)
  if command -v ninja >/dev/null 2>&1; then
    configure_args+=(-G Ninja)
  fi
  if sdl_source="$(find_dependency_source "sdl3-src" "CMakeLists.txt")"; then
    configure_args+=("-DFETCHCONTENT_SOURCE_DIR_SDL3=$sdl_source")
  fi
  lua_source="$(ensure_lua_source)"
  configure_args+=("-DFETCHCONTENT_SOURCE_DIR_LUA=$lua_source")

  echo "> cmake ${configure_args[*]}"
  cmake "${configure_args[@]}"
fi

if [[ "$configure_only" -eq 0 ]]; then
  echo "> cmake --build $build_path --target $target --parallel"
  cmake --build "$build_path" --target "$target" --parallel
fi
