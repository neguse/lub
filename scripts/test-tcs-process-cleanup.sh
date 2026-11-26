#!/usr/bin/env bash
# Checks that the tcs vm --watch started by lub <Entry>.csproj (the tcs VM player) does not remain after lub exits.
# tcs is started in 2 ways: as a single process (the same as the tcs of the release kit), and as dotnet run with tcs as a child.
# There are also 2 ways to end: a normal exit after capture, and a forced kill (TerminateProcess on Windows, SIGKILL on Linux / macOS).
# Processes are found by the entry class name in the command line.
# Usage: scripts/test-tcs-process-cleanup.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$OSTYPE" in
msys* | cygwin*) windows=1 ;;
*) windows=0 ;;
esac
# Linux CI has no display. No window is needed
[[ "$OSTYPE" == linux* ]] && export SDL_VIDEO_DRIVER=offscreen

# LUB_TCS is split on spaces.
# On Windows, do not turn the path passed to dotnet into MSYS form
dotnet build src/tcs -c Release -nologo -v q -o "$work/tcs"
tcs_dll="$work/tcs/tcs.dll"
[[ $windows -eq 1 ]] && tcs_dll="$(cygpath -m "$tcs_dll")"

# Print lines of "<pid> <command line>"
processes_of() {
  if [[ $windows -eq 1 ]]; then
    powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*$1*' -and \$_.ProcessId -ne \$PID } | ForEach-Object { '{0} {1}' -f \$_.ProcessId, \$_.CommandLine }" | tr -d '\r'
  else
    ps -axo pid=,command= | grep -F "$1" | grep -vF grep || true
  fi
}

force_kill() {
  if [[ $windows -eq 1 ]]; then
    taskkill //F //PID "$1" >/dev/null
  else
    kill -9 "$1"
  fi
}

# Turn $! (the MSYS pid) into the pid passed to force_kill
os_pid() {
  if [[ $windows -eq 1 ]]; then
    cat "/proc/$1/winpid"
  else
    echo "$1"
  fi
}

failed=0

# $1 = how tcs is started (direct | dotnet-run), $2 = how it ends (exit | kill)
run_case() {
  local launcher="$1" how="$2" token pid dir out
  token="TcsCleanup$RANDOM$RANDOM"
  dir="$work/$token"
  mkdir -p "$dir"
  touch "$dir/$token.csproj"
  cat >"$dir/$token.cs" <<CS
using Lub;

public static class $token
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
    }
}
CS
  # For a forced kill, do not add capture, so that lub does not exit by itself
  local args=()
  [[ $how == exit ]] && args=(--capture "$dir/out.png" --capture-frame 5 --fixed-dt 0.0166666666666667)
  # The first output tcs writes (once it appears, tcs is running)
  out="$dir/.lub/$token.tcsb"
  if [[ $launcher == direct ]]; then
    LUB_TCS="dotnet $tcs_dll" "$binary" "$dir/$token.csproj" "${args[@]}" >"$dir/log" 2>&1 &
  else
    # Without LUB_TCS, lub starts the tcs in cwd with dotnet run
    env -u LUB_TCS "$binary" "$dir/$token.csproj" "${args[@]}" >"$dir/log" 2>&1 &
  fi
  pid=$!

  if [[ $how == kill ]]; then
    for _ in $(seq 1 1800); do
      [[ -e "$out" ]] && break
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    if [[ ! -e "$out" ]] || ! kill -0 "$pid" 2>/dev/null; then
      cat "$dir/log"
      echo "FAIL $launcher $how: lub ended before tcs wrote its output" >&2
      failed=1
      return
    fi
    force_kill "$(os_pid "$pid")"
    wait "$pid" 2>/dev/null || true
  else
    local waited=0
    while kill -0 "$pid" 2>/dev/null && ((waited < 1800)); do
      sleep 0.1
      waited=$((waited + 1))
    done
    if kill -0 "$pid" 2>/dev/null; then
      cat "$dir/log"
      echo "FAIL $launcher $how: lub did not exit" >&2
      kill "$pid" 2>/dev/null || true
      failed=1
    fi
    wait "$pid" 2>/dev/null || true
  fi

  local left=""
  for _ in $(seq 1 20); do
    left="$(processes_of "$token")"
    [[ -z "$left" ]] && break
    sleep 0.5
  done
  if [[ -z "$left" ]]; then
    echo "PASS $launcher $how"
  else
    echo "FAIL $launcher $how: still running after lub ended:"
    echo "$left" | cut -c1-200 | sed 's/^/    /'
    echo "$left" | while read -r leftover _; do force_kill "$leftover" 2>/dev/null || true; done
    failed=1
  fi
}

for launcher in direct dotnet-run; do
  run_case "$launcher" exit
  run_case "$launcher" kill
done

exit "$failed"
