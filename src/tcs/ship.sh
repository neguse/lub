#!/usr/bin/env bash
# Places the files shipped with tcs (src/tcs/shipped.txt) under <dir>: src/tcs/ship.sh <dir>
# tcs reads them from the dir of its executable.
set -euo pipefail
dir="$1"
root="$(cd "$(dirname "$0")/../.." && pwd)"
while read -r dest source; do
  case "$dest" in '' | '#'*) continue ;; esac
  mkdir -p "$dir/$(dirname "$dest")"
  cp "$root/$source" "$dir/$dest"
done < "$root/src/tcs/shipped.txt"
