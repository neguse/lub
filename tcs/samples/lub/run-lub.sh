#!/usr/bin/env bash
# tcs 出力を lub (readonly) で実行するための staging & 起動 helper。
#
# lub は entry を cwd 相対の samples/<mod>/.lub/<mod>.lua + samples/boot.lua +
# third_party/lume/lume.lua で解決するため、lub リポジトリへ書き込む代わりに
# run/ 配下へ同じレイアウトを組み立てて cwd をそこに移して起動する。
#
# 使い方:
#   samples/lub/run-lub.sh [hello|breakout] [--build] [lub args...]
#     --build: transpile + staging のみ (起動しない)
#     lub args: そのまま lub へ渡す (--capture out.png --capture-frame 240 など)
#   LUB_ROOT=/path/to/lub samples/lub/run-lub.sh
set -euo pipefail

cd "$(dirname "$0")"
TCS_ROOT="$(cd ../.. && pwd)"
LUB_ROOT="$(cd "${LUB_ROOT:-$TCS_ROOT/../lub}" && pwd)"

NAME="${1:-hello}"
shift $(($# > 0 ? 1 : 0))

case "$NAME" in
    hello) ENTRY_CLASS=Hello ;;
    breakout) ENTRY_CLASS=Breakout ;;
    *)
        echo "unknown sample: $NAME (hello | breakout)" >&2
        exit 1
        ;;
esac

if [[ ! -x "$LUB_ROOT/build/lub" ]]; then
    echo "lub binary not found: $LUB_ROOT/build/lub (build lub first)" >&2
    exit 1
fi

mkdir -p "run/samples/$NAME/.lub" run/third_party/lume

dotnet run --project "$TCS_ROOT/Transpiler" -- "$NAME.cs" --ref lub_stub.cs \
    --prelude lub_shim.lua \
    -o "run/samples/$NAME/.lub/$NAME.lua" --entry "$ENTRY_CLASS" \
    --no-naming-check

cp "$LUB_ROOT/samples/boot.lua" run/samples/boot.lua
cp "$LUB_ROOT/samples/lub_io.lua" run/samples/lub_io.lua
cp "$LUB_ROOT/third_party/lume/lume.lua" run/third_party/lume/lume.lua

if [[ "$NAME" == "breakout" ]]; then
    # shader source は lub の 09_breakout のものをそのまま使う
    mkdir -p run/samples/09_breakout/data
    cp "$LUB_ROOT"/samples/09_breakout/data/09_breakout.*.slang \
        run/samples/09_breakout/data/
fi

if [[ "${1:-}" == "--build" ]]; then
    echo "staged: $(pwd)/run ($NAME)"
    exit 0
fi

cd run
exec "$LUB_ROOT/scripts/run-headless.sh" "$LUB_ROOT/build/lub" "$NAME" "$@"
