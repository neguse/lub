#!/usr/bin/env bash
# Golden image regression test (CPU rasterizer limited).
# Runs visual entries with --capture under each backend and byte-compares
# the result to tests/golden/<name>_<backend>.png.
#
# Usage:
#   scripts/run-golden.sh                    # check all visual goldens × backends
#   scripts/run-golden.sh --update           # regenerate checked goldens
#   scripts/run-golden.sh --sample 01_triangle
#   scripts/run-golden.sh --test indexed_draw
#   scripts/run-golden.sh --tests-only
#   scripts/run-golden.sh --backend sdlgpu
#
# Determinism relies on:
#   - a machine-independent CPU rasterizer  - Linux: lavapipe (CPU Vulkan,
#     run-headless.sh enforces it) / Windows: WARP via LUB_DX12_WARP=1
#   - fixed --capture-frame and --fixed-dt  - set below to FRAME and 1/60 s
# Exit code is 0 only if every checked visual golden matches.
#
# Platform selects the backend set: Linux checks sdlgpu and native
# (Vulkan direct), Windows (git bash) checks native (D3D12). On Linux both
# backends render through lavapipe and must produce byte-identical output,
# so they share the *_sdlgpu.png goldens — a native/sdlgpu divergence is a
# test failure by design.

set -euo pipefail

cd "$(dirname "$0")/.."

SAMPLES=(00_hello 00b_clear 00c_buffer 00d_shader 01_triangle 02_vertex_color 03_texture 04_mvp 05_postprocess 06_deferred 07_compute 08_gltf 09_breakout 10_breakout3d 11_shadow 12_sfb 16_box2d 18_coin_pusher 19_sdf 26_renderer3d)
VISUAL_TESTS=(indexed_draw load_op depth_sample)
FRAME=30
case "$(uname -s)" in
    MINGW* | MSYS*)
        windows=1
        BACKENDS=(native)
        BINARY="${BINARY:-./build-release/lub.exe}"
        ;;
    *)
        windows=0
        BACKENDS=(sdlgpu native)
        BINARY="${BINARY:-./build/lub}"
        ;;
esac
GOLDEN_DIR=tests/golden

update=0
sample_filter=""
test_filter=""
backend_filter=""
tests_only=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --update) update=1 ;;
        --sample) sample_filter="$2"; shift ;;
        --test) test_filter="$2"; shift ;;
        --tests-only) tests_only=1 ;;
        --backend) backend_filter="$2"; shift ;;
        -h | --help)
            sed -n '2,17p' "$0"
            exit 0
            ;;
        *)
            echo "unknown arg: $1" >&2
            exit 2
            ;;
    esac
    shift
done

if [[ $tests_only -eq 1 && -n "$sample_filter" ]]; then
    echo "--tests-only cannot be combined with --sample" >&2
    exit 2
fi

if [[ ! -x "$BINARY" ]]; then
    echo "binary not built: $BINARY (run: cmake --build build)" >&2
    exit 2
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
jobs_max="${LUB_GOLDEN_JOBS:-$(nproc)}"
jobs_running=0
jobs_launched=0

check_entry() {
    local label="$1"
    local entry="$2"
    local golden_name="$3"
    local backend="$4"
    local display_num="$5"
    local frame="$FRAME"
    case "$golden_name" in
        16_box2d) frame=120 ;;
        18_coin_pusher) frame=240 ;;
    esac

    # Linux native (Vulkan) shares the sdlgpu goldens: same lavapipe
    # rasterizer, byte-identical output is the contract between them.
    local golden_backend="$backend"
    if [[ $windows -eq 0 && "$backend" == native ]]; then
        golden_backend=sdlgpu
    fi
    local out="$tmpdir/${golden_name}_${backend}.png"
    local golden="$GOLDEN_DIR/${golden_name}_${golden_backend}.png"
    local log="$tmpdir/${golden_name}_${backend}.log"
    local status="$tmpdir/${golden_name}_${backend}.status"

    # LUB_GOLDEN=1: sample が golden capture 中と分かるようにする。debug UI 等
    # の非決定な overlay を描かせないため (例: 19_sdf の imgui パネル)。
    local run_ok=1
    if [[ $windows -eq 1 ]]; then
        # No xvfb/lavapipe on Windows; real windows open, WARP renders.
        LUB_BACKEND="$backend" LUB_DX12_WARP=1 LUB_GOLDEN=1 \
            "$BINARY" \
            "$entry" --capture "$out" --capture-frame "$frame" \
            --fixed-dt 0.0166666666666667 \
            >"$log" 2>&1 || run_ok=0
    else
        LUB_BACKEND="$backend" LUB_XVFB_SERVERNUM="$display_num" LUB_GOLDEN=1 \
            scripts/run-headless.sh "$BINARY" \
            "$entry" --capture "$out" --capture-frame "$frame" \
            --fixed-dt 0.0166666666666667 \
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
            # Shared golden (linux native -> sdlgpu): only the owning
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

    if cmp -s "$out" "$golden"; then
        echo "PASS ${label} ${backend}"
        echo pass >"$status"
    else
        echo "FAIL ${label} ${backend}: $out != $golden"
        echo fail >"$status"
    fi
}

# Both backends of one entry run serially inside one background job per
# entry (the captures of one sample share its generated Lua).
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

run_samples=1
run_tests=1
if [[ $tests_only -eq 1 ]]; then
    run_samples=0
elif [[ -n "$sample_filter" && -z "$test_filter" ]]; then
    run_tests=0
elif [[ -n "$test_filter" && -z "$sample_filter" ]]; then
    run_samples=0
fi

# サンプルは C# (tcs)。先に transpile して samples/<name>/.lub/<Entry>.lua を
# 作る (entry class = csproj の basename)。
entry_lua_for() {
    local sample="$1"
    local proj
    proj=$(ls "samples/$sample"/*.csproj | head -1)
    local cls
    cls=$(basename "$proj" .csproj)
    echo "samples/$sample/.lub/$cls.lua"
}

if [[ $run_samples -eq 1 ]]; then
    for sample in "${SAMPLES[@]}"; do
        [[ -n "$sample_filter" && "$sample" != "$sample_filter" ]] && continue
        scripts/run-cs-sample.sh "$sample" --build >"$tmpdir/${sample}_transpile.log" 2>&1 || {
            echo "FAIL ${sample}: transpile failed (see $tmpdir/${sample}_transpile.log)"
            keep_tmpdir=1
            echo fail >"$tmpdir/${sample}_transpile.status"
            continue
        }
    done
    for sample in "${SAMPLES[@]}"; do
        [[ -n "$sample_filter" && "$sample" != "$sample_filter" ]] && continue
        [[ -f "$tmpdir/${sample}_transpile.status" ]] && continue
        launch_entry "$sample" "$(entry_lua_for "$sample")" "$sample"
    done
fi

if [[ $run_tests -eq 1 ]]; then
    for t in "${VISUAL_TESTS[@]}"; do
        [[ -n "$test_filter" && "$t" != "$test_filter" ]] && continue
        local_name="test_${t}"
        launch_entry "$local_name" "tests/lua/${local_name}.lua" "$local_name"
    done
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
