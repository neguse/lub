#!/usr/bin/env bash
# Run sglua with Mesa lavapipe (CPU Vulkan) ICD forced.
# - VK_ICD_FILENAMES selects lavapipe instead of any GPU driver.
# - If DISPLAY/WAYLAND_DISPLAY are unset, wrap in xvfb-run so SDL3 can
#   create a Vulkan-capable window (the SDL3 offscreen/dummy drivers
#   don't support SDL_WINDOW_VULKAN; lavapipe still runs even though
#   the X server only exists for window creation).
#
# Usage:
#   scripts/run-headless.sh [args passed to ./build/sglua]
#   scripts/run-headless.sh ./build/sglua samples/01_triangle.lua
# If first arg starts with "./" or "/", it is taken as the binary;
# otherwise we default to ./build/sglua.

set -euo pipefail

ICD=/usr/share/vulkan/icd.d/lvp_icd.json
if [[ ! -f "$ICD" ]]; then
    echo "lavapipe ICD not found at $ICD" >&2
    echo "Install with: sudo pacman -S vulkan-swrast (Arch) or" >&2
    echo "             sudo apt install mesa-vulkan-drivers (Debian)" >&2
    exit 1
fi
export VK_ICD_FILENAMES="$ICD"
# Modern Vulkan loader honors this; older may need the legacy var, set both.
export VK_DRIVER_FILES="$ICD"

# Default binary if first arg looks like a sample script
binary=./build/sglua
args=("$@")
if [[ ${#args[@]} -gt 0 && ( "${args[0]}" == ./* || "${args[0]}" == /* ) && -x "${args[0]}" ]]; then
    binary="${args[0]}"
    args=("${args[@]:1}")
fi

# If we have a display, run directly; otherwise wrap in xvfb-run.
if [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]]; then
    exec "$binary" "${args[@]}"
else
    if ! command -v xvfb-run >/dev/null; then
        echo "no DISPLAY/WAYLAND_DISPLAY and no xvfb-run found" >&2
        echo "install xorg-server-xvfb (Arch) or xvfb (Debian)" >&2
        exit 2
    fi
    exec xvfb-run -a "$binary" "${args[@]}"
fi
