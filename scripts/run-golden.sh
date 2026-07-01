#!/usr/bin/env bash
# Golden image regression test (lavapipe + xvfb limited).
# Runs visual entries with --capture under both backends and byte-compares
# the result to tests/golden/<name>_<backend>.png.
#
# Usage:
#   scripts/run-golden.sh                    # check all visual goldens × backends
#   scripts/run-golden.sh --update           # regenerate checked goldens
#   scripts/run-golden.sh --sample 01_triangle
#   scripts/run-golden.sh --test indexed_draw
#   scripts/run-golden.sh --tests-only
#   scripts/run-golden.sh --backend sokol
#
# Determinism relies on:
#   - lavapipe ICD (CPU Vulkan)            - run-headless.sh enforces this
#   - fixed --capture-frame                - set below to FRAME
# Exit code is 0 only if every checked visual golden matches.

set -euo pipefail

cd "$(dirname "$0")/.."

SAMPLES=(00_hello 00b_clear 00c_buffer 00d_shader 01_triangle 02_vertex_color 03_texture 04_mvp 05_postprocess 06_deferred 07_compute 08_gltf 09_breakout 10_breakout3d 11_shadow 12_sfb 16_box2d 18_coin_pusher)
VISUAL_TESTS=(indexed_draw)
BACKENDS=(sokol sdlgpu)
FRAME=30
BINARY="${BINARY:-./build/lub}"
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
trap 'rm -rf "$tmpdir"' EXIT

pass=0
fail=0
missing=0
updated=0

check_entry() {
    local label="$1"
    local entry="$2"
    local golden_name="$3"
    local backend="$4"
    local frame="$FRAME"
    case "$golden_name" in
        16_box2d) frame=120 ;;
        18_coin_pusher) frame=240 ;;
    esac

    local out="$tmpdir/${golden_name}_${backend}.png"
    local golden="$GOLDEN_DIR/${golden_name}_${backend}.png"
    local log="$tmpdir/${golden_name}_${backend}.log"

    if ! LUB_BACKEND="$backend" scripts/run-headless.sh "$BINARY" \
        "$entry" --capture "$out" --capture-frame "$frame" \
        >"$log" 2>&1; then
        echo "FAIL ${label} ${backend}: process failed (see $log)"
        fail=$((fail + 1))
        return
    fi

    if [[ ! -f "$out" ]]; then
        echo "FAIL ${label} ${backend}: capture not produced (see $log)"
        fail=$((fail + 1))
        return
    fi

    if [[ $update -eq 1 ]]; then
        cp "$out" "$golden"
        echo "UPDATED ${golden}"
        updated=$((updated + 1))
        return
    fi

    if [[ ! -f "$golden" ]]; then
        echo "MISSING ${golden} (run with --update to create)"
        missing=$((missing + 1))
        return
    fi

    if cmp -s "$out" "$golden"; then
        echo "PASS ${label} ${backend}"
        pass=$((pass + 1))
    else
        echo "FAIL ${label} ${backend}: $out != $golden"
        fail=$((fail + 1))
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

if [[ $run_samples -eq 1 ]]; then
    for sample in "${SAMPLES[@]}"; do
        [[ -n "$sample_filter" && "$sample" != "$sample_filter" ]] && continue
        for backend in "${BACKENDS[@]}"; do
            [[ -n "$backend_filter" && "$backend" != "$backend_filter" ]] && continue
            # Each sample is self-contained at samples/<name>/<name>.hxml.
            check_entry "$sample" "samples/${sample}/${sample}.hxml" \
                "$sample" "$backend"
        done
    done
fi

if [[ $run_tests -eq 1 ]]; then
    for t in "${VISUAL_TESTS[@]}"; do
        [[ -n "$test_filter" && "$t" != "$test_filter" ]] && continue
        local_name="test_${t}"
        for backend in "${BACKENDS[@]}"; do
            [[ -n "$backend_filter" && "$backend" != "$backend_filter" ]] && continue
            check_entry "$local_name" "tests/lua/${local_name}.lua" \
                "$local_name" "$backend"
        done
    done
fi

echo "---"
if [[ $update -eq 1 ]]; then
    echo "updated: $updated"
    exit 0
fi
echo "pass: $pass  fail: $fail  missing: $missing"
[[ $fail -eq 0 && $missing -eq 0 ]]
