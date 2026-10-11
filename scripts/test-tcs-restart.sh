#!/usr/bin/env bash
# lub <Entry>.csproj が tcs --watch の標準出力の frame を正しく読むことを見る。
# tcs の代わりに frame を手で書く fake を LUB_TCS で起動し、次を順に送る:
#   1. 知らない種類の frame と reload chunk (X = 1)。知らない frame は本文の
#      byte 数ごと読み飛ばし、chunk は当たる
#   2. -o の出力を v2 に書き換えてから、古い build 向けの reload chunk (X = 7) と
#      restart_required。VM は v2 の出力で作り直され、古い chunk は当たらない
#   3. 再起動後の reload chunk (X = 2)。新しい VM に当たる
# 使い方: scripts/test-tcs-restart.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
work="$(mktemp -d)"
pid=
cleanup() {
  [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

touch "$work/Restart.csproj" "$work/Restart.cs"

# v1 は world に Lua の filter callback を持たせ、begin も step もしない
# (callback は次の宣言か step まで runtime が持つ)。v2 は同じ world を callback 無しで宣言し直すので、
# runtime は閉じた VM の callback を手放す
game() { # $1 = version
  cat <<EOF
__tcs_build = $1
local M = {}
local seen
function M.on_init()
  lub.app.config({ width = 64, height = 64 })
  print("RESTART_TEST init v$1")
  io.stdout:flush()
end
function M.on_frame(dt)
  local world = lub.phys2d.world("restart", {
    callbacks = $1 == 1 and { filter = function() return true end } or nil,
  })
  if $1 == 2 then
    world:begin()
    for i = 1, 2 do
      local body = world:body("b" .. i, { type = lub.phys2d.DYNAMIC, initial = { x = i * 0.5, y = 0 } })
      body:box("s", { hx = 1, hy = 1, density = 1 })
    end
    world:step(1 / 60)
  end
  local x = X or 0
  if x ~= seen then
    seen = x
    print("RESTART_TEST v$1 x=" .. x)
    io.stdout:flush()
  end
  if $1 == 2 and x == 2 then
    lub.app.quit()
  end
end
return M
EOF
}
game 1 > "$work/game1.lua"
game 2 > "$work/game2.lua"

cat > "$work/fake-tcs.sh" <<'EOF'
# tcs --watch --reload-chunks の代わり。引数から -o を拾い、go<N> を待って frame を書く
work="$(dirname "$0")"
while [[ $# -gt 0 ]]; do
  [[ "$1" == -o ]] && out="$2"
  shift
done
frame() { printf '@@tcs_%s %d\n%s' "$1" "${#2}" "$2"; }
wait_go() { while [[ ! -e "$work/go$1" ]]; do sleep 0.05; done; }
cp "$work/game1.lua" "$out"
wait_go 1
{ frame future_kind $'two\nlines\n'; frame reload_chunk 'X = 1'; }
wait_go 2
cp "$work/game2.lua" "$out"
{ frame reload_chunk 'X = 7'; frame restart_required $'static initializer changed: Restart.Speed\nbase class changed: Enemy\n'; }
wait_go 3
frame reload_chunk 'X = 2'
exec sleep 600
EOF

LUB_TCS="bash $work/fake-tcs.sh" scripts/run-headless.sh "$binary" "$work/Restart.csproj" > "$work/log" 2>&1 &
pid=$!

wait_for() {
  local pattern="$1"
  for _ in $(seq 1 600); do
    grep -q "$pattern" "$work/log" && return 0
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.1
  done
  grep -q "$pattern" "$work/log"
}
step() { # $1 = go の番号, $2 = 待つ行
  touch "$work/go$1"
  if ! wait_for "$2"; then
    cat "$work/log"
    echo "FAIL: step $1 did not reach '$2'" >&2
    exit 1
  fi
}

step 0 "RESTART_TEST v1 x=0"
step 1 "RESTART_TEST v1 x=1"
step 2 "RESTART_TEST v2 x=0"
step 3 "RESTART_TEST v2 x=2"
wait "$pid" || true
pid=
fail=0
grep -q "RESTART_TEST init v2" "$work/log" || fail=1
grep -q "static initializer changed: Restart.Speed" "$work/log" || fail=1
grep -q "base class changed: Enemy" "$work/log" || fail=1
if grep -q "RESTART_TEST v2 x=7\|ignoring stdout line" "$work/log"; then fail=1; fi
if [[ $fail -ne 0 ]]; then
  cat "$work/log"
  echo "FAIL: restart reasons missing, a stale chunk reached the new VM, or a frame was misread" >&2
  exit 1
fi
echo "tcs restart: unknown frames are skipped, restart_required rebuilds the VM from the output and later chunks apply to it"
