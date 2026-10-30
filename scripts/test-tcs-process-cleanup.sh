#!/usr/bin/env bash
# lub <Entry>.csproj が起動した tcs --watch が、lub の終了後に残らないことを見る。
# tcs の起動は 2 通り: 1 process で動く形 (release の一式の Transpiler と同じ) と、Transpiler を子に持つ dotnet run。
# 終わり方も 2 通り: capture の後の正常終了と、Windows では強制終了 (TerminateProcess)。
# Linux / macOS の SIGKILL では tcs が残る。
# 子を道連れにする仕組みが OS に無く、tcs 側で親の終了を検知する必要があるので、ここでは見ない。
# process は command line に入る entry class の名前で探す。
# 使い方: scripts/test-tcs-process-cleanup.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$OSTYPE" in
msys* | cygwin*) windows=1 ;;
*) windows=0 ;;
esac
# Linux の CI には画面が無い。窓は要らない
[[ "$OSTYPE" == linux* ]] && export SDL_VIDEO_DRIVER=offscreen

# LUB_TCS は空白で区切られる。
# Windows では dotnet に渡す path を MSYS の形にしない
dotnet build third_party/tcs/Transpiler -c Release -nologo -v q -o "$work/tcs"
tcs_dll="$work/tcs/Transpiler.dll"
[[ $windows -eq 1 ]] && tcs_dll="$(cygpath -m "$tcs_dll")"

# "<pid> <command line>" の行を出す
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

failed=0

# $1 = tcs の起動 (direct | dotnet-run), $2 = 終わり方 (exit | kill)
run_case() {
  local launcher="$1" how="$2" token pid dir
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
  if [[ $launcher == direct ]]; then
    LUB_TCS="dotnet $tcs_dll" "$binary" "$dir/$token.csproj" \
      --capture "$dir/out.png" --capture-frame 5 --fixed-dt 0.0166666666666667 \
      >"$dir/log" 2>&1 &
  else
    # LUB_TCS が無ければ lub は cwd の third_party/tcs を dotnet run で起動する
    env -u LUB_TCS "$binary" "$dir/$token.csproj" \
      --capture "$dir/out.png" --capture-frame 5 --fixed-dt 0.0166666666666667 \
      >"$dir/log" 2>&1 &
  fi
  pid=$!

  if [[ $how == kill ]]; then
    for _ in $(seq 1 1800); do
      [[ -e "$dir/.lub/$token.lua" ]] && break
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    if [[ ! -e "$dir/.lub/$token.lua" ]] || ! kill -0 "$pid" 2>/dev/null; then
      cat "$dir/log"
      echo "FAIL $launcher $how: lub ended before tcs wrote its output" >&2
      failed=1
      return
    fi
    force_kill "$(cat "/proc/$pid/winpid")"
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
  [[ $windows -eq 1 ]] && run_case "$launcher" kill
done

exit "$failed"
