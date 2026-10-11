#!/usr/bin/env bash
# Run lub without needing a visible window.
# - With Mesa lavapipe (CPU Vulkan) installed, VK_ICD_FILENAMES forces it
#   instead of any GPU driver (golden runs match CI's lavapipe this way). The
#   window goes to DISPLAY/WAYLAND_DISPLAY, else to an X server from xvfb-run,
#   else to SDL_VIDEO_DRIVER=offscreen.
# - Without lavapipe lub runs on the machine's Vulkan driver with
#   SDL_VIDEO_DRIVER=offscreen, so nothing waits on a compositor. Captures then
#   come from that driver and don't match the goldens (run-golden.sh and
#   native-gate.sh refuse to compare them).
#
# Usage:
#   scripts/run-headless.sh [args passed to ./build/lub]
#   scripts/run-headless.sh ./build/lub samples/01_triangle.lua
# If first arg starts with "./" or "/", it is taken as the binary;
# otherwise we default to ./build/lub.

set -euo pipefail

# Arch: lvp_icd.json / Debian・Ubuntu: lvp_icd.x86_64.json
ICD="$(compgen -G '/usr/share/vulkan/icd.d/lvp_icd*.json' | head -1 || true)"
if [[ -n "$ICD" ]]; then
    export VK_ICD_FILENAMES="$ICD"
    # Modern Vulkan loader honors this; older may need the legacy var, set both.
    export VK_DRIVER_FILES="$ICD"
fi

# SDL assertions open a message box by default; nobody clicks it under Xvfb and
# the process sits in X11_ShowMessageBox until the gate's timeout. lub sets the
# same hint itself; this is the belt-and-braces for older binaries and the C
# smoke tests. An explicit SDL_ASSERT from the caller wins.
export SDL_ASSERT="${SDL_ASSERT:-abort}"

# Forward LUB_BACKEND into the wrapped process so samples can pick the backend.
[[ -n "${LUB_BACKEND:-}" ]] && export LUB_BACKEND

# Default binary if first arg looks like a sample script
binary=./build/lub
args=("$@")
if [[ ${#args[@]} -gt 0 && ( "${args[0]}" == ./* || "${args[0]}" == /* ) && -x "${args[0]}" ]]; then
    binary="${args[0]}"
    args=("${args[@]:1}")
fi

if [[ -n "$ICD" ]]; then
    if [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]]; then
        exec "$binary" "${args[@]}"
    fi
    if command -v xvfb-run >/dev/null; then
        # xvfb-run -a races when instances start concurrently (two pick the
        # same display, one X server dies under the other's client). Parallel
        # drivers must hand each job a unique LUB_XVFB_SERVERNUM instead.
        if [[ -n "${LUB_XVFB_SERVERNUM:-}" ]]; then
            exec xvfb-run -n "$LUB_XVFB_SERVERNUM" "$binary" "${args[@]}"
        fi
        exec xvfb-run -a "$binary" "${args[@]}"
    fi
else
    echo "run-headless: lavapipe ICD not found under /usr/share/vulkan/icd.d/;" \
        "using this machine's Vulkan driver (captures may differ from goldens)" >&2
fi
export SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}"
exec "$binary" "${args[@]}"
