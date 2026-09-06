#!/usr/bin/env bash
# cs-lib/lub_stub.cs (API の記述) から生成物を作る。
#   include/lub/lub_api.h        C API の header
#   src/gen/lua_api_gen.c        Lua binding
#   tests/lua/test_api_surface.lua  prelude が全 member を持つかの Lua テスト
#   web/gen/lub-api-docs.json    API reference のデータ (web/scripts/gen-api-docs.mjs が読む)
#   dotnet/Lub/Lub.g.cs          .NET 実行の facade (P/Invoke)
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
dotnet run --project tools/lub-gen -- docs -o "$tmp/lub-api-docs.json"
dotnet run --project tools/lub-gen -- facade -o "$tmp/Lub.g.cs"
# checkin 済みの生成物は scripts/format.sh と同じ整形を通した形にする。formatter は
# 1 回で不動点にならないことがある (CJK の comment の折り返し等) ので、変わらなく
# なるまで回す。
for _ in 1 2 3 4; do
  cp "$tmp/lub_api.h" "$tmp/h.prev"; cp "$tmp/lua_api_gen.c" "$tmp/c.prev"; cp "$tmp/test_api_surface.lua" "$tmp/l.prev"
  clang-format --style=LLVM -i "$tmp/lub_api.h" "$tmp/lua_api_gen.c"
  env XDG_CONFIG_HOME=/nonexistent npm --prefix web exec --no -- stylua --no-editorconfig "$tmp/test_api_surface.lua"
  cmp -s "$tmp/lub_api.h" "$tmp/h.prev" && cmp -s "$tmp/lua_api_gen.c" "$tmp/c.prev" && cmp -s "$tmp/test_api_surface.lua" "$tmp/l.prev" && break
done
status=0
for pair in "lub_api.h:include/lub/lub_api.h" "lua_api_gen.c:src/gen/lua_api_gen.c" "test_api_surface.lua:tests/lua/test_api_surface.lua" "lub-api-docs.json:web/gen/lub-api-docs.json" "Lub.g.cs:dotnet/Lub/Lub.g.cs"; do
  src="$tmp/${pair%%:*}"
  dst="${pair#*:}"
  if [ "$check" = 1 ]; then
    if ! diff -q "$src" "$dst" > /dev/null; then
      # C の 2 ファイルは clang-format の版で折り返しが変わる。内容の差では
      # ないので、空白を落として同じなら通す (他の生成物は完全一致を要求)。
      case "$dst" in *.h|*.c) ws_tolerant=1 ;; *) ws_tolerant=0 ;; esac
      if [ "$ws_tolerant" = 1 ] && [ "$(tr -d '[:space:]' < "$src" | sha256sum)" = "$(tr -d '[:space:]' < "$dst" | sha256sum)" ]; then
        echo "gen-api: $dst differs only in formatting (formatter version); ok"
      else
        echo "gen-api: $dst is stale (run scripts/gen-api.sh)"
        status=1
      fi
    fi
  else
    cp "$src" "$dst"
    echo "gen-api: wrote $dst"
  fi
done
exit $status
