#!/usr/bin/env bash
# Converts tcs itself to C: src/tcs/to-c.sh <out.c>
# The tcs that converts is scripts/tcs.sh (LUB_TCS if set, otherwise the .NET tcs).
set -euo pipefail
mkdir -p "$(dirname "$1")"
out="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
cd "$(dirname "$0")/../.."
mapfile -t args < <(src/tcs/sources.sh)
scripts/tcs.sh c --lib "${args[@]}" -o "$out"
