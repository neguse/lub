#!/usr/bin/env bash
# cs-lib/lub_stub.cs (API の記述) から生成物を作る。
#   include/lub/lub_api.h        C API の header
#   src/gen/lua_api_gen.c        Lua binding
#   tests/lua/test_api_surface.lua  prelude が全 member を持つかの Lua テスト
# --check は再生成せず、checkin 済みの生成物との差分を検査する (native gate)。
set -euo pipefail
cd "$(dirname "$0")/.."
check=0
[ "${1:-}" = "--check" ] && check=1
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
dotnet run --project tools/lub-gen -- check
dotnet run --project tools/lub-gen -- header -o "$tmp/lub_api.h"
dotnet run --project tools/lub-gen -- lua -o "$tmp/lua_api_gen.c"
dotnet run --project tools/lub-gen -- surface-test -o "$tmp/test_api_surface.lua"
# checkin 済みの生成物は scripts/format.sh と同じ整形を通した形にする
clang-format --style=LLVM -i "$tmp/lub_api.h" "$tmp/lua_api_gen.c"
npm --prefix web exec --no -- stylua --no-editorconfig "$tmp/test_api_surface.lua"
status=0
for pair in "lub_api.h:include/lub/lub_api.h" "lua_api_gen.c:src/gen/lua_api_gen.c" "test_api_surface.lua:tests/lua/test_api_surface.lua"; do
  src="$tmp/${pair%%:*}"
  dst="${pair#*:}"
  if [ "$check" = 1 ]; then
    if ! diff -q "$src" "$dst" > /dev/null; then
      echo "gen-api: $dst is stale (run scripts/gen-api.sh)"
      status=1
    fi
  else
    cp "$src" "$dst"
    echo "gen-api: wrote $dst"
  fi
done
exit $status
