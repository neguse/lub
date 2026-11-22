#!/usr/bin/env bash
# Runs lub_tcs_pipeline_smoke (tests/c/tcs_pipeline_smoke.c) without tcs.
# Instead of tcs vm --watch, a fake that only writes frames and waits is passed via LUB_TCS (dotnet is not needed).
# Usage: scripts/test-tcs-pipeline-smoke.sh [lub_tcs_pipeline_smoke binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub_tcs_pipeline_smoke}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Output a frame of an unknown kind, a diff, and a compile error frame in a single write.
# The body of the unknown frame contains lines that look like frame headers and UTF-8 multi-byte characters (if lub does not skip the body by its byte count, a fake diff appears).
cat > "$work/fake-tcs.sh" <<'FAKE'
export LC_ALL=C # Make ${#s} count bytes
frame() { printf '@@tcs_%s %d\n%s' "$1" "${#2}" "$2"; }
printf '%s%s%s' "$(frame future_kind $'@@tcs_vm_batch 5\nWRONG\n\xe2\x9c\x93')" \
  "$(frame vm_batch QUJDREVG)" "$(frame build_failed 'oops!')"
exec sleep 600
FAKE
# Windows CreateProcess cannot read msys paths, and bash also looks at System32 (WSL) before PATH, so pass a Windows path without spaces
bash_cmd=bash
script="$work/fake-tcs.sh"
if command -v cygpath >/dev/null; then
  bash_cmd="$(cygpath -ms "$(command -v bash)")"
  script="$(cygpath -ms "$script")"
fi
LUB_TCS="$bash_cmd $script" "$binary"
