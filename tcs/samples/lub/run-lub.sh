#!/usr/bin/env bash
# tcs 出力を lub (readonly) で実行するための staging & 起動 helper。
#
# lub は entry を cwd 相対の samples/<mod>/.lub/<mod>.lua + samples/boot.lua +
# third_party/lume/lume.lua で解決するため、lub リポジトリへ書き込む代わりに
# run/ 配下へ同じレイアウトを組み立てて cwd をそこに移して起動する。
#
# 使い方:
#   samples/lub/run-lub.sh            # transpile + lub 起動 (headless 対応)
#   samples/lub/run-lub.sh --build    # transpile + staging のみ (起動しない)
#   LUB_ROOT=/path/to/lub samples/lub/run-lub.sh
set -euo pipefail

cd "$(dirname "$0")"
TCS_ROOT="$(cd ../.. && pwd)"
LUB_ROOT="$(cd "${LUB_ROOT:-$TCS_ROOT/../lub}" && pwd)"

if [[ ! -x "$LUB_ROOT/build/lub" ]]; then
    echo "lub binary not found: $LUB_ROOT/build/lub (build lub first)" >&2
    exit 1
fi

mkdir -p run/samples/hello/.lub run/third_party/lume

dotnet run --project "$TCS_ROOT/Transpiler" -- hello.cs --ref lub_stub.cs \
    --prelude lub_shim.lua \
    -o run/samples/hello/.lub/hello.lua --entry Hello --no-naming-check

cp "$LUB_ROOT/samples/boot.lua" run/samples/boot.lua
cp "$LUB_ROOT/third_party/lume/lume.lua" run/third_party/lume/lume.lua

if [[ "${1:-}" == "--build" ]]; then
    echo "staged: $(pwd)/run"
    exit 0
fi

cd run
exec "$LUB_ROOT/scripts/run-headless.sh" "$LUB_ROOT/build/lub" hello
