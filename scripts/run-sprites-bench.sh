#!/usr/bin/env bash
set -euo pipefail

build_dir="build-release-linux"
backend=""
score_frame=3600
burst=1
target_fps=60
max_sprites=200000
profile=0
profile_window=300
no_build=0

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
  cat <<EOF
Usage: bash scripts/run-sprites-bench.sh [options]

Options:
  --build-dir DIR              Build directory (default: build-release-linux)
  --backend NAME               Backend: d3d12 / vulkan / sdlgpu (default: platform default)
  --score-frame N              Frame to print score and quit (default: 3600)
  --burst N                    Sprites spawned per accepted spawn tick (default: 1)
  --target-fps N               Target FPS threshold (default: 60)
  --max-sprites N              Maximum live sprites (default: 200000)
  --profile                    Enable generic CPU profiler for the final window
  --profile-window N           Profile window before score frame (default: 300)
  --no-build                   Reuse an existing Release build
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build-dir)
      build_dir="$2"
      shift 2
      ;;
    --backend)
      backend="$2"
      shift 2
      ;;
    --score-frame)
      score_frame="$2"
      shift 2
      ;;
    --burst)
      burst="$2"
      shift 2
      ;;
    --target-fps)
      target_fps="$2"
      shift 2
      ;;
    --max-sprites)
      max_sprites="$2"
      shift 2
      ;;
    --profile)
      profile=1
      shift
      ;;
    --profile-window)
      profile_window="$2"
      shift 2
      ;;
    --no-build)
      no_build=1
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

build_path="$repo_root/$build_dir"
exe="$build_path/lub"
sample="$repo_root/samples/13_sprites/13_sprites.hxml"

if [[ "$no_build" -eq 0 ]]; then
  bash "$repo_root/scripts/build-release.sh" \
    --build-dir "$build_dir" \
    --target lub
fi

if [[ ! -x "$exe" && ! -f "$exe" ]]; then
  echo "lub executable was not found: $exe" >&2
  echo "run without --no-build first" >&2
  exit 1
fi

if [[ -n "${LUB_HAXE:-}" ]]; then
  if [[ ! -x "$LUB_HAXE" ]]; then
    echo "LUB_HAXE points to a missing haxe executable: $LUB_HAXE" >&2
    exit 1
  fi
  haxe_dir="$(cd "$(dirname "$LUB_HAXE")" && pwd)"
  export PATH="$haxe_dir:$PATH"
elif ! command -v haxe >/dev/null 2>&1; then
  echo "haxe was not found. Install Haxe 5 or set LUB_HAXE before running the .hxml benchmark." >&2
  exit 1
fi

if ! command -v haxelib >/dev/null 2>&1; then
  echo "haxelib was not found. Put haxelib in PATH or set LUB_HAXE to a Haxe install that contains haxelib." >&2
  exit 1
fi

if [[ -n "$backend" ]]; then
  export LUB_BACKEND="$backend"
else
  unset LUB_BACKEND
fi
export LUB_SPRITE_TARGET_FPS="$target_fps"
export LUB_SPRITE_BURST="$burst"
export LUB_SPRITE_SCORE_FRAME="$score_frame"
export LUB_SPRITE_MAX="$max_sprites"

if [[ "$profile" -eq 1 ]]; then
  profile_start_frame=$((score_frame - profile_window))
  if [[ "$profile_start_frame" -lt 0 ]]; then
    profile_start_frame=0
  fi
  export LUB_PROFILE=1
  export LUB_PROFILE_WINDOW="$profile_window"
  export LUB_PROFILE_START_FRAME="$profile_start_frame"
  export LUB_PROFILE_FRAME="$score_frame"
  export LUB_PROFILE_LABEL="sprites13"
else
  unset LUB_PROFILE LUB_PROFILE_WINDOW LUB_PROFILE_START_FRAME
  unset LUB_PROFILE_FRAME LUB_PROFILE_LABEL
fi

echo "running sprite benchmark:"
echo "  exe=$exe"
echo "  sample=$sample"
echo "  backend=${backend:-(default)} target_fps=$target_fps score_frame=$score_frame burst=$burst max_sprites=$max_sprites"
if [[ "$profile" -eq 1 ]]; then
  echo "  profile=on profile_window=$profile_window profile_start_frame=$profile_start_frame"
fi

"$exe" "$sample"
