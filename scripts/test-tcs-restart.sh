#!/usr/bin/env bash
# lub <Entry>.csproj が tcs --watch の標準出力の frame を正しく読むことを見る。
# tcs の代わりに frame を手で書く fake を LUB_TCS で起動し、次を順に送る:
#   1. 知らない種類の frame と reload chunk (X = 1)。知らない frame は本文の
#      byte 数ごと読み飛ばし、chunk は当たる
#   2. -o の出力を v2 に書き換えてから、古い build 向けの reload chunk (X = 7) と
#      restart_required を 1 回の write で送る。VM は v2 の出力で作り直され、
#      古い chunk はどちらの VM にも当たらない。物理の world も作り直される
#   3. 再起動後の reload chunk (X = 2)。新しい VM に当たる
#   4. 読み込みで止まる出力 v3 と restart_required。VM は無くなり、その間の
#      chunk は log を出して落とす。出力が v4 に書き直されると作り直す
#   5. v4 への reload chunk (X = 4)
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

# world "cb" は Lua の filter callback を持ち、begin も step もしない (callback は次の宣言か step まで runtime が持つ)。
# VM を作り直すと runtime は閉じた VM の callback を手放す。
# world "move" の body は v1 で動き続け、作り直した後は初期位置 (x = 0) から始まる
game() { # $1 = version
  cat <<EOF
__tcs_build = $1
local M = {}
local seen
lub.phys2d.world("cb", { callbacks = { filter = function() return true end } })
function M.OnInit()
  lub.app.config({ width = 64, height = 64 })
  print("RESTART_TEST init v$1")
  io.stdout:flush()
end
function M.OnFrame(dt)
  lub.phys2d.world("cb", { callbacks = { filter = function() return true end } })
  local world = lub.phys2d.world("move", { gravity = { x = 0, y = 0 } })
  world:begin()
  local body = world:body("p", { type = lub.phys2d.DYNAMIC, initial = { x = 0, y = 0, vx = 60 } })
  body:box("s", { hx = 0.5, hy = 0.5, density = 1 })
  world:step(1 / 60)
  local x = X or 0
  if x ~= seen then
    seen = x
    print(string.format("RESTART_TEST v$1 x=%d body=%.1f", x, body:pose().x))
    io.stdout:flush()
  end
  if $1 == 4 and x == 4 then
    lub.app.quit()
  end
end
return M
EOF
}
game 1 > "$work/game1.lua"
game 2 > "$work/game2.lua"
{ game 3 | sed 's/^return M$/error("RESTART_TEST load failure")/'; } > "$work/game3.lua"
game 4 > "$work/game4.lua"

cat > "$work/fake-tcs.sh" <<'EOF'
# tcs --watch --reload-chunks の代わり。
# 引数から -o を拾い、go<N> を待って frame を書く
export LC_ALL=C # ${#s} を byte 数にする
work="$(dirname "$0")"
while [[ $# -gt 0 ]]; do
  [[ "$1" == -o ]] && out="$2"
  shift
done
# frame_to VAR kind body: 見出しと本文を VAR に入れる ($(...) は末尾の改行を落とす)
frame_to() { printf -v "$1" '@@tcs_%s %d\n%s' "$2" "${#3}" "$3"; }
frame() { local f; frame_to f "$1" "$2"; printf '%s' "$f"; }
wait_go() { while [[ ! -e "$work/go$1" ]]; do sleep 0.05; done; }
# tcs と同じく出力は一時ファイルから rename で置き換える
put() { cp "$work/$1" "$out.tmp" && mv "$out.tmp" "$out"; }
put game1.lua
wait_go 1
frame_to a future_kind $'two\nlines \xe2\x9c\x93\n'
frame_to b reload_chunk 'X = 1'
printf '%s' "$a$b"
wait_go 2
put game2.lua
# 1 回の write にして、lub が 2 つを同じ読み込みで受けるようにする
frame_to a reload_chunk 'X = 7'
frame_to b restart_required $'static initializer changed: Restart.速度\nbase class changed: Enemy\n'
printf '%s' "$a$b"
wait_go 3
frame reload_chunk 'X = 2'
wait_go 4
put game3.lua
frame restart_required $'static initializer changed: Restart.Broken\n'
wait_go 5
frame reload_chunk 'X = 9'
wait_go 6
put game4.lua
wait_go 7
frame reload_chunk 'X = 4'
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
step 2 "RESTART_TEST v2 x=0 body=[01]\."
step 3 "RESTART_TEST v2 x=2"
step 4 "the Lua VM is not running"
step 5 "reload chunk dropped: the Lua VM is not running"
step 6 "RESTART_TEST init v4"
step 7 "RESTART_TEST v4 x=4"
wait "$pid" || true
pid=
expect() { grep -q "$1" "$work/log" || { cat "$work/log"; echo "FAIL: missing '$1'" >&2; exit 1; }; }
reject() { if grep -q "$1" "$work/log"; then cat "$work/log"; echo "FAIL: unexpected '$1'" >&2; exit 1; fi; }
expect "RESTART_TEST init v2"
expect "static initializer changed: Restart.速度"
expect "base class changed: Enemy"
expect "RESTART_TEST load failure"
expect "RESTART_TEST v4 x=0 body=[01]\."
# X = 7 は restart_required と同じ読み込みで届き、どちらの VM にも当たらない
reject "x=7"
reject "x=9"
reject "ignoring stdout line"
reject "RESTART_TEST init v3"
echo "tcs restart: unknown frames are skipped, restart_required rebuilds the VM (and the physics worlds) from the output, later chunks apply to it, and a VM that failed to load comes back with the next output"
