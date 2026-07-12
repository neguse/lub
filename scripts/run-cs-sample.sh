#!/usr/bin/env bash
# C# (TinyC#) サンプルを tcs で transpile して lub で実行する。
#
# 使い方:
#   scripts/run-cs-sample.sh <sample> [--build|--check|--watch] [lub args...]
#     --build: transpile のみ (samples/<sample>/.lub/<sample>.lua を生成)
#     --check: tcs check (診断のみ、Lua 出力なし)
#     --watch: transpile 後、tcs --watch を背後に起動して lub を実行
#              (保存 -> 再変換 -> lub の mtime poll で hot reload)
#     指定なし: transpile して lub を起動。lub args はそのまま渡す
#
# 要件: dotnet SDK + third_party/tcs submodule
#   git submodule update --init third_party/tcs
set -euo pipefail
cd "$(dirname "$0")/.."

NAME="${1:?usage: run-cs-sample.sh <sample> [--build|--check|--watch] [lub args...]}"
shift

DIR="samples/$NAME"
if [[ ! -d "$DIR" ]]; then
    echo "sample not found: $DIR" >&2
    exit 1
fi
if [[ ! -f third_party/tcs/Transpiler/Transpiler.csproj ]]; then
    echo "third_party/tcs submodule not initialized:" >&2
    echo "  git submodule update --init third_party/tcs" >&2
    exit 1
fi

# entry class = サンプル直下の唯一の .cs (共有 stub は cs-lib/)
mapfile -t CS_FILES < <(find "$DIR" -maxdepth 1 -name '*.cs' | sort)
if [[ ${#CS_FILES[@]} -ne 1 ]]; then
    echo "expected exactly one .cs in $DIR, found ${#CS_FILES[@]}" >&2
    exit 1
fi
ENTRY_CS="${CS_FILES[0]}"
ENTRY_CLASS="$(basename "$ENTRY_CS" .cs)"
OUT="$DIR/.lub/$NAME.lua"

TCS=(dotnet run --project third_party/tcs/Transpiler --)

MODE="${1:-}"
case "$MODE" in
--check)
    exec "${TCS[@]}" check "$ENTRY_CS" --ref cs-lib/lub_stub.cs --no-naming-check
    ;;
--build | --watch | *) ;;
esac
[[ "$MODE" == "--build" || "$MODE" == "--watch" ]] && shift || true

mkdir -p "$DIR/.lub"
"${TCS[@]}" "$ENTRY_CS" --ref cs-lib/lub_stub.cs -o "$OUT" \
    --entry "$ENTRY_CLASS" --no-naming-check

if [[ "$MODE" == "--build" ]]; then
    echo "built: $OUT"
    exit 0
fi

if [[ "$MODE" == "--watch" ]]; then
    "${TCS[@]}" "$ENTRY_CS" --ref cs-lib/lub_stub.cs -o "$OUT" \
        --entry "$ENTRY_CLASS" --no-naming-check --watch &
    WATCH_PID=$!
    trap 'kill $WATCH_PID 2>/dev/null || true' EXIT
fi

./build/lub "$NAME" "$@"
