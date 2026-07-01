#!/usr/bin/env bash
set -euo pipefail

build_dir="build-release-linux"
target="lub"
configure_only=0
no_configure=0

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
  cat <<EOF
Usage: bash scripts/build-release.sh [options]

Options:
  --build-dir DIR                 Build directory (default: build-release-linux)
  --target NAME                   CMake target (default: lub)
  --configure-only                Configure but do not build
  --no-configure                  Build from an existing configure step
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

if [[ ! -f "$repo_root/third_party/SDL/CMakeLists.txt" ]]; then
  echo "submodules are missing; run: git submodule update --init" >&2
  exit 1
fi

if [[ "$no_configure" -eq 0 ]]; then
  configure_args=(-S "$repo_root" -B "$build_path" -DCMAKE_BUILD_TYPE=Release)
  if command -v ninja >/dev/null 2>&1; then
    configure_args+=(-G Ninja)
  fi

  echo "> cmake ${configure_args[*]}"
  cmake "${configure_args[@]}"
fi

if [[ "$configure_only" -eq 0 ]]; then
  echo "> cmake --build $build_path --target $target --parallel"
  cmake --build "$build_path" --target "$target" --parallel
fi
