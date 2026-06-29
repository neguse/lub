#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$REPO_ROOT/samples/analyzer-demo/analyzer-demo.csproj"
RUN_PRECHECKS=1

source "$SCRIPT_DIR/rider-env.sh"

usage() {
  cat <<'EOF'
Usage: samples/analyzer-demo/open-rider-demo.sh [--no-precheck]

Runs the Rider verification pre-checks, then opens analyzer-demo.csproj in Rider.

Options:
  --no-precheck  Open Rider without running verify-rider-prechecks.sh first.
  -h, --help     Show this help.

Environment:
  TCS_RIDER_COMMAND=/path/to/rider.sh  Override Rider command detection.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --no-precheck)
      RUN_PRECHECKS=0
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Error: unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
  shift
done

if [ "$RUN_PRECHECKS" -eq 1 ]; then
  "$SCRIPT_DIR/verify-rider-prechecks.sh"
fi

rider_command="$(find_rider_command || true)"
if [ -z "$rider_command" ]; then
  echo "Error: Rider command not found." >&2
  echo "Set TCS_RIDER_COMMAND=/path/to/rider.sh or install Rider in a standard JetBrains Toolbox location." >&2
  exit 1
fi

if ! has_display; then
  echo "Error: no GUI display was detected from this shell." >&2
  echo "Set DISPLAY or WAYLAND_DISPLAY, then rerun this script from the desktop session." >&2
  exit 1
fi

echo "Opening $PROJECT"
echo "Rider command: $rider_command"
"$rider_command" "$PROJECT" >/dev/null 2>&1 &
