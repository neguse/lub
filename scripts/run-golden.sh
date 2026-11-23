#!/usr/bin/env bash
# Golden image regression test of the visual tests (cs_visual_tests in
# scripts/cs-tests.sh, CPU rasterizer limited).
# Compiles tests/cs to tcs VM bytecode (.tcsb), runs each visual test with
# --capture under each backend and byte-compares the result to
# tests/golden/<name>_<backend>.png.
#
# Usage:
#   scripts/run-golden.sh                    # check all visual tests × backends
#   scripts/run-golden.sh --update           # regenerate checked goldens
#   scripts/run-golden.sh --test TestIndexedDraw
#   scripts/run-golden.sh --backend sdlgpu
#
# Determinism relies on:
#   - a machine-independent CPU rasterizer  - Linux: lavapipe (CPU Vulkan,
#     required below; run-headless.sh forces it) / Windows: WARP via
#     LUB_D3D12_WARP=1
#   - fixed --capture-frame and --fixed-dt  - set below to FRAME and 1/60 s
#   - --readback-fixed-latency: a readback arrives exactly 2 frames later
# Exit code is 0 only if every checked visual golden matches.
#
# Platform selects the backend set: Linux checks sdlgpu and vulkan
# (Vulkan direct), Windows (git bash) checks d3d12 (D3D12). On Linux
# both backends render through lavapipe and must produce byte-identical
# output, so they share the *_sdlgpu.png goldens — a vulkan/sdlgpu
# divergence is a test failure by design.
#
# macOS checks metal on the real GPU. Metal has no machine-independent
# rasterizer, so there are no metal goldens: the capture is compared to
# the *_sdlgpu.png golden within a tolerance (scripts/png-diff.py), and
# --update never writes from macOS.

set -euo pipefail

cd "$(dirname "$0")/.."

FRAME=30
apple=0
case "$(uname -s)" in
    MINGW* | MSYS*)
        windows=1
        BACKENDS=(d3d12)
        BINARY="${BINARY:-./build-release/lub.exe}"
        ;;
    Darwin)
        windows=0
        apple=1
        BACKENDS=(metal)
        BINARY="${BINARY:-./build-mac/lub}"
        ;;
    *)
        windows=0
        BACKENDS=(sdlgpu vulkan)
        BINARY="${BINARY:-./build/lub}"
        ;;
esac
GOLDEN_DIR=tests/golden

update=0
test_filter=""
backend_filter=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --update) update=1 ;;
        --test) test_filter="$2"; shift ;;
        --backend) backend_filter="$2"; shift ;;
        -h | --help)
            sed -n '2,/^$/p' "$0" | sed '$d'
            exit 0
            ;;
        *)
            echo "unknown arg: $1" >&2
            exit 2
            ;;
    esac
    shift
done

if [[ ! -x "$BINARY" ]]; then
    echo "binary not built: $BINARY (run: cmake --build build)" >&2
    exit 2
fi

# Without lavapipe run-headless.sh falls back to the machine's GPU driver,
# whose captures can't match (or be written as) goldens.
if [[ $windows -eq 0 && $apple -eq 0 ]] &&
    ! compgen -G '/usr/share/vulkan/icd.d/lvp_icd*.json' >/dev/null; then
    echo "goldens need Mesa lavapipe (no /usr/share/vulkan/icd.d/lvp_icd*.json)" >&2
    echo "Install with: sudo pacman -S vulkan-swrast (Arch) or" >&2
    echo "             sudo apt install mesa-vulkan-drivers (Debian)" >&2
    exit 1
fi

mkdir -p "$GOLDEN_DIR"
tmpdir=$(mktemp -d)
keep_tmpdir=0
cleanup() {
    if [[ $keep_tmpdir -eq 1 ]]; then
        echo "failure artifacts kept in $tmpdir"
    else
        rm -rf "$tmpdir"
    fi
}
trap cleanup EXIT

# lavapipe renders on the CPU, so independent captures scale with cores.
# Entries run as background jobs; each writes its verdict to a status file
# in $tmpdir, aggregated after the final wait. LUB_GOLDEN_JOBS=1 for serial.
jobs_max="${LUB_GOLDEN_JOBS:-$(nproc 2>/dev/null || sysctl -n hw.ncpu)}"
jobs_running=0
jobs_launched=0

check_entry() {
    local label="$1"
    # entry is the launch arguments (space-separated. The bytecode and --entry)
    local entry_args
    read -r -a entry_args <<<"$2"
    local golden_name="$3"
    local backend="$4"
    local display_num="$5"

    # Linux vulkan shares the sdlgpu goldens: same lavapipe rasterizer,
    # byte-identical output is the contract between them.
    local golden_backend="$backend"
    if [[ $windows -eq 0 && "$backend" == vulkan ]]; then
        golden_backend=sdlgpu
    fi
    # macOS metal has no goldens of its own (see the header).
    if [[ $apple -eq 1 ]]; then
        golden_backend=sdlgpu
    fi
    local out="$tmpdir/${golden_name}_${backend}.png"
    local golden="$GOLDEN_DIR/${golden_name}_${golden_backend}.png"
    local log="$tmpdir/${golden_name}_${backend}.log"
    local status="$tmpdir/${golden_name}_${backend}.status"

    local run_ok=1
    if [[ $windows -eq 1 ]]; then
        # No xvfb/lavapipe on Windows; real windows open, WARP renders.
        LUB_BACKEND="$backend" LUB_D3D12_WARP=1 "$BINARY" \
            "${entry_args[@]}" --capture "$out" --capture-frame "$FRAME" \
            --fixed-dt 0.0166666666666667 --readback-fixed-latency \
            >"$log" 2>&1 || run_ok=0
    elif [[ $apple -eq 1 ]]; then
        # Real windows open and the GPU renders.
        LUB_BACKEND="$backend" "$BINARY" \
            "${entry_args[@]}" --capture "$out" --capture-frame "$FRAME" \
            --fixed-dt 0.0166666666666667 --readback-fixed-latency \
            >"$log" 2>&1 || run_ok=0
    else
        LUB_BACKEND="$backend" LUB_XVFB_SERVERNUM="$display_num" \
            scripts/run-headless.sh "$BINARY" \
            "${entry_args[@]}" --capture "$out" --capture-frame "$FRAME" \
            --fixed-dt 0.0166666666666667 --readback-fixed-latency \
            >"$log" 2>&1 || run_ok=0
    fi
    if [[ $run_ok -ne 1 ]]; then
        echo "FAIL ${label} ${backend}: process failed (see $log)"
        echo fail >"$status"
        return
    fi

    if [[ ! -f "$out" ]]; then
        echo "FAIL ${label} ${backend}: capture not produced (see $log)"
        echo fail >"$status"
        return
    fi

    if [[ $update -eq 1 ]]; then
        if [[ "$golden_backend" != "$backend" ]]; then
            # Shared golden (linux vulkan -> sdlgpu): only the owning
            # backend regenerates it; still verify below.
            :
        else
            cp "$out" "$golden"
            echo "UPDATED ${golden}"
            echo updated >"$status"
            return
        fi
    fi

    if [[ ! -f "$golden" ]]; then
        echo "MISSING ${golden} (run with --update to create)"
        echo missing >"$status"
        return
    fi

    if [[ $apple -eq 1 ]]; then
        local metrics
        if metrics=$(python3 scripts/png-diff.py "$out" "$golden"); then
            echo "PASS ${label} ${backend} ${metrics}"
            echo pass >"$status"
        else
            echo "FAIL ${label} ${backend}: $out vs $golden: ${metrics}"
            echo fail >"$status"
        fi
        return
    fi

    if cmp -s "$out" "$golden"; then
        echo "PASS ${label} ${backend}"
        echo pass >"$status"
    else
        echo "FAIL ${label} ${backend}: $out != $golden"
        echo fail >"$status"
    fi
}

# Both backends of one entry run serially inside one background job per
# entry.
run_entry_backends() {
    local label="$1"
    local entry="$2"
    local golden_name="$3"
    local base="$4"
    local bi=0
    local backend
    for backend in "${BACKENDS[@]}"; do
        if [[ -z "$backend_filter" || "$backend" == "$backend_filter" ]]; then
            # Unique X display per run: concurrent jobs must not race for
            # xvfb display numbers (run-headless.sh).
            check_entry "$label" "$entry" "$golden_name" "$backend" \
                $((100 + base * 2 + bi))
        fi
        bi=$((bi + 1))
    done
}

launch_entry() {
    run_entry_backends "$@" "$jobs_launched" &
    jobs_launched=$((jobs_launched + 1))
    jobs_running=$((jobs_running + 1))
    if ((jobs_running >= jobs_max)); then
        wait -n || true
        jobs_running=$((jobs_running - 1))
    fi
}

# tests/cs tests whose rendering is checked against goldens.
# First convert all of tests/cs into tests/cs/.lub/all.tcsb, then select the test with --entry.
# shellcheck source=scripts/cs-tests.sh
source scripts/cs-tests.sh
tests_ok=0
cs_tests_vm_build >"$tmpdir/tests_transpile.log" 2>&1 && tests_ok=1
if [[ $tests_ok -eq 1 ]]; then
    for t in "${cs_visual_tests[@]}"; do
        [[ -n "$test_filter" && "$t" != "$test_filter" ]] && continue
        launch_entry "$t" "tests/cs/.lub/all.tcsb --entry $t" "$t"
    done
else
    echo "FAIL tests: transpile failed (see $tmpdir/tests_transpile.log)"
    keep_tmpdir=1
    echo fail >"$tmpdir/tests_transpile.status"
fi

wait

pass=0
fail=0
missing=0
updated=0
for status_file in "$tmpdir"/*.status; do
    [[ -e "$status_file" ]] || continue
    case "$(<"$status_file")" in
        pass) pass=$((pass + 1)) ;;
        fail) fail=$((fail + 1)) ;;
        missing) missing=$((missing + 1)) ;;
        updated) updated=$((updated + 1)) ;;
    esac
done
if [[ $fail -gt 0 ]]; then
    keep_tmpdir=1
fi

echo "---"
if [[ $update -eq 1 ]]; then
    echo "updated: $updated"
    exit 0
fi
echo "pass: $pass  fail: $fail  missing: $missing"
[[ $fail -eq 0 && $missing -eq 0 ]]
