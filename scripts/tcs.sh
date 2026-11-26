#!/usr/bin/env bash
# Runs tcs with the given arguments: scripts/tcs.sh check Game.cs
# Uses LUB_TCS (the command that starts tcs, split on spaces) if set. Otherwise builds the .NET tcs and runs it (scripts/build-tcs-dotnet.sh).
# A caller that runs tcs many times sets LUB_TCS once, so the build is not checked on every run.
set -euo pipefail
if [[ -z "${LUB_TCS:-}" ]]; then
  LUB_TCS="$("$(dirname "$0")/build-tcs-dotnet.sh")"
fi
# shellcheck disable=SC2086
exec $LUB_TCS "$@"
