#!/usr/bin/env bash
# C# (TinyC#) サンプルの check / build ヘルパー (主に CI・gate 用)。
# 対話実行は lub CLI が第一級で対応している:
#   ./build/lub samples/<name>/<Entry>.cs   # transpile + watch + hot reload
#
# 使い方:
#   scripts/run-cs-sample.sh <sample> --check   # tcs check (診断のみ)
#   scripts/run-cs-sample.sh <sample> --build   # transpile のみ
#     出力は lub CLI と同じ samples/<sample>/.lub/<Entry>.lua
#
# 要件: dotnet SDK + third_party/tcs submodule
set -euo pipefail
cd "$(dirname "$0")/.."

NAME="${1:?usage: run-cs-sample.sh <sample> --check|--build}"
MODE="${2:?usage: run-cs-sample.sh <sample> --check|--build}"

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

mapfile -t CS_FILES < <(find "$DIR" -maxdepth 1 -name '*.cs' | sort)
if [[ ${#CS_FILES[@]} -ne 1 ]]; then
    echo "expected exactly one .cs in $DIR, found ${#CS_FILES[@]}" >&2
    exit 1
fi
ENTRY_CS="${CS_FILES[0]}"
ENTRY_CLASS="$(basename "$ENTRY_CS" .cs)"

TCS=(dotnet run --project third_party/tcs/Transpiler --)

case "$MODE" in
--check)
    exec "${TCS[@]}" check "$ENTRY_CS" --ref cs-lib/lub_stub.cs --no-naming-check
    ;;
--build)
    mkdir -p "$DIR/.lub"
    exec "${TCS[@]}" "$ENTRY_CS" --ref cs-lib/lub_stub.cs \
        -o "$DIR/.lub/$ENTRY_CLASS.lua" --entry "$ENTRY_CLASS" \
        --no-naming-check
    ;;
*)
    echo "unknown mode: $MODE (--check | --build)" >&2
    exit 2
    ;;
esac
