#!/usr/bin/env bash
# Golden image regression test (lavapipe + xvfb 限定).
# Runs each sample with --capture under both backends and byte-compares
# the result to tests/golden/<sample>_<backend>.png.
#
# Usage:
#   scripts/run-golden.sh                    # check all samples × backends
#   scripts/run-golden.sh --update           # regenerate goldens (overwrite)
#   scripts/run-golden.sh --sample 01_triangle
#   scripts/run-golden.sh --backend sokol
#
# Determinism relies on:
#   - lavapipe ICD (CPU Vulkan)            — run-headless.sh enforces this
#   - fixed --capture-frame                — set below to FRAME
# Exit code is 0 only if every checked sample matches.

set -euo pipefail

cd "$(dirname "$0")/.."

SAMPLES=(00_hello 00b_clear 00c_buffer 00d_shader 01_triangle 02_vertex_color 03_texture 04_mvp 05_postprocess 06_deferred 07_compute 08_gltf)
BACKENDS=(sokol sdlgpu)
FRAME=30
BINARY=./build/lub
GOLDEN_DIR=tests/golden

update=0
sample_filter=""
backend_filter=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --update)  update=1 ;;
        --sample)  sample_filter="$2"; shift ;;
        --backend) backend_filter="$2"; shift ;;
        -h|--help)
            sed -n '2,18p' "$0"; exit 0 ;;
        *)
            echo "unknown arg: $1" >&2
            exit 2 ;;
    esac
    shift
done

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

for sample in "${SAMPLES[@]}"; do
    [[ -n "$sample_filter" && "$sample" != "$sample_filter" ]] && continue
    for backend in "${BACKENDS[@]}"; do
        [[ -n "$backend_filter" && "$backend" != "$backend_filter" ]] && continue

        out="$tmpdir/${sample}_${backend}.png"
        golden="$GOLDEN_DIR/${sample}_${backend}.png"

        # Prefer `.hxml` (Haxe pipeline entry) over the bare `.lua` script so
        # migrated samples exercise the haxe build path; fall back to .lua for
        # samples that haven't been ported yet.
        if [[ -f "samples/${sample}.hxml" ]]; then
            entry="samples/${sample}.hxml"
        else
            entry="samples/${sample}.lua"
        fi

        LUB_BACKEND="$backend" scripts/run-headless.sh "$BINARY" \
            "$entry" --capture "$out" --capture-frame "$FRAME" \
            >"$tmpdir/${sample}_${backend}.log" 2>&1

        if [[ ! -f "$out" ]]; then
            echo "FAIL ${sample} ${backend}: capture not produced (see $tmpdir/${sample}_${backend}.log)"
            fail=$((fail + 1))
            continue
        fi

        if [[ $update -eq 1 ]]; then
            cp "$out" "$golden"
            echo "UPDATED ${golden}"
            updated=$((updated + 1))
            continue
        fi

        if [[ ! -f "$golden" ]]; then
            echo "MISSING ${golden} (run with --update to create)"
            missing=$((missing + 1))
            continue
        fi

        if cmp -s "$out" "$golden"; then
            echo "PASS ${sample} ${backend}"
            pass=$((pass + 1))
        else
            echo "FAIL ${sample} ${backend}: $out != $golden"
            fail=$((fail + 1))
        fi
    done
done

echo "---"
if [[ $update -eq 1 ]]; then
    echo "updated: $updated"
    exit 0
fi
echo "pass: $pass  fail: $fail  missing: $missing"
[[ $fail -eq 0 && $missing -eq 0 ]]
