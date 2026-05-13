#!/usr/bin/env bash
# tests/lua/test_*.lua を巡回して capture + golden cmp する runner.
# scripts/run-golden.sh の test 版。
#
# Usage:
#   scripts/run-test-golden.sh            # check all tests × backends
#   scripts/run-test-golden.sh --update   # regenerate goldens
#   scripts/run-test-golden.sh --test indexed_draw
#   scripts/run-test-golden.sh --backend sokol

set -euo pipefail
cd "$(dirname "$0")/.."

TESTS=(indexed_draw)
BACKENDS=(sokol sdlgpu)
FRAME=30
BINARY=./build/sglua
GOLDEN_DIR=tests/golden

update=0
test_filter=""
backend_filter=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --update)  update=1 ;;
        --test)    test_filter="$2"; shift ;;
        --backend) backend_filter="$2"; shift ;;
        -h|--help)
            sed -n '2,15p' "$0"; exit 0 ;;
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

pass=0; fail=0; missing=0; updated=0

for t in "${TESTS[@]}"; do
    [[ -n "$test_filter" && "$t" != "$test_filter" ]] && continue
    for backend in "${BACKENDS[@]}"; do
        [[ -n "$backend_filter" && "$backend" != "$backend_filter" ]] && continue

        name="test_${t}"
        out="$tmpdir/${name}_${backend}.png"
        golden="$GOLDEN_DIR/${name}_${backend}.png"

        SGLUA_BACKEND="$backend" scripts/run-headless.sh "$BINARY" \
            "tests/lua/${name}.lua" --capture "$out" --capture-frame "$FRAME" \
            >"$tmpdir/${name}_${backend}.log" 2>&1 || true

        if [[ ! -f "$out" ]]; then
            echo "FAIL ${name} ${backend}: capture not produced (see $tmpdir/${name}_${backend}.log)"
            fail=$((fail + 1)); continue
        fi
        if [[ $update -eq 1 ]]; then
            cp "$out" "$golden"
            echo "UPDATED ${golden}"
            updated=$((updated + 1)); continue
        fi
        if [[ ! -f "$golden" ]]; then
            echo "MISSING ${golden} (run with --update to create)"
            missing=$((missing + 1)); continue
        fi
        if cmp -s "$out" "$golden"; then
            echo "PASS ${name} ${backend}"
            pass=$((pass + 1))
        else
            echo "FAIL ${name} ${backend}: $out != $golden"
            fail=$((fail + 1))
        fi
    done
done

echo "---"
if [[ $update -eq 1 ]]; then echo "updated: $updated"; exit 0; fi
echo "pass: $pass  fail: $fail  missing: $missing"
[[ $fail -eq 0 && $missing -eq 0 ]]
