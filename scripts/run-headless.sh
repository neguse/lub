#!/usr/bin/env bash
# Run lub with Mesa lavapipe (CPU Vulkan) ICD forced.
# - VK_ICD_FILENAMES selects lavapipe instead of any GPU driver.
# - If DISPLAY/WAYLAND_DISPLAY are unset, wrap in xvfb-run so SDL3 can
#   create a Vulkan-capable window (the SDL3 offscreen/dummy drivers
#   don't support SDL_WINDOW_VULKAN; lavapipe still runs even though
#   the X server only exists for window creation).
#
# Usage:
#   scripts/run-headless.sh [args passed to ./build/lub]
#   scripts/run-headless.sh ./build/lub samples/01_triangle.lua
# If first arg starts with "./" or "/", it is taken as the binary;
# otherwise we default to ./build/lub.

set -euo pipefail

# Arch: lvp_icd.json / Debian・Ubuntu: lvp_icd.x86_64.json
ICD="$(compgen -G '/usr/share/vulkan/icd.d/lvp_icd*.json' | head -1 || true)"
if [[ -z "$ICD" ]]; then
    echo "lavapipe ICD not found under /usr/share/vulkan/icd.d/" >&2
    echo "Install with: sudo pacman -S vulkan-swrast (Arch) or" >&2
    echo "             sudo apt install mesa-vulkan-drivers (Debian)" >&2
    exit 1
fi
export VK_ICD_FILENAMES="$ICD"
# Modern Vulkan loader honors this; older may need the legacy var, set both.
export VK_DRIVER_FILES="$ICD"

# Forward LUB_BACKEND into the wrapped process so samples can pick the backend.
[[ -n "${LUB_BACKEND:-}" ]] && export LUB_BACKEND

# Default binary if first arg looks like a sample script
binary=./build/lub
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
    # xvfb-run -a races when instances start concurrently (two pick the same
    # display, one X server dies under the other's client). Parallel drivers
    # must hand each job a unique LUB_XVFB_SERVERNUM instead.
    if [[ -n "${LUB_XVFB_SERVERNUM:-}" ]]; then
        exec xvfb-run -n "$LUB_XVFB_SERVERNUM" "$binary" "${args[@]}"
    fi
    exec xvfb-run -a "$binary" "${args[@]}"
fi
