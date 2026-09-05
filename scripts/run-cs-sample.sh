#!/usr/bin/env bash
# C# (TinyC#) サンプルの check / build ヘルパー (主に CI・gate 用)。
# 対話実行は lub CLI が第一級で対応している:
#   ./build/lub samples/<name>/<Entry>.csproj   # transpile + watch + hot reload
#   ./build/lub samples/<name>/<Entry>.cs       # 同上 (単一ファイル指定)
#
# 使い方:
#   scripts/run-cs-sample.sh <sample> --check   # tcs check (診断のみ)
#   scripts/run-cs-sample.sh <sample> --build   # transpile のみ
#     出力は lub CLI と同じ samples/<sample>/.lub/<Entry>.lua
#
# entry class は <Entry>.csproj の basename (無ければ唯一の .cs の basename)。
# 入力は同ディレクトリの *.cs 全部 + cs-lib 実装ソース (lub CLI と同じ規約)。
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
if [[ ${#CS_FILES[@]} -eq 0 ]]; then
    echo "no .cs in $DIR" >&2
    exit 1
fi
mapfile -t CSPROJ < <(find "$DIR" -maxdepth 1 -name '*.csproj' | sort)
if [[ ${#CSPROJ[@]} -ge 1 ]]; then
    ENTRY_CLASS="$(basename "${CSPROJ[0]}" .csproj)"
elif [[ ${#CS_FILES[@]} -eq 1 ]]; then
    ENTRY_CLASS="$(basename "${CS_FILES[0]}" .cs)"
else
    echo "multiple .cs but no .csproj in $DIR (entry class ambiguous)" >&2
    exit 1
fi

# cs-lib の実装ソースを一律追加 (lub_stub.cs は --ref 専用なので除外)
mapfile -t -O "${#CS_FILES[@]}" CS_FILES \
    < <(find cs-lib -name '*.cs' ! -name 'lub_stub.cs' | sort)

TCS=(dotnet run --project third_party/tcs/Transpiler --)

case "$MODE" in
--check)
    exec "${TCS[@]}" check "${CS_FILES[@]}" --ref cs-lib/lub_stub.cs
    ;;
--build)
    mkdir -p "$DIR/.lub"
    exec "${TCS[@]}" "${CS_FILES[@]}" --ref cs-lib/lub_stub.cs \
        -o "$DIR/.lub/$ENTRY_CLASS.lua" --entry "$ENTRY_CLASS"
    ;;
*)
    echo "unknown mode: $MODE (--check | --build)" >&2
    exit 2
    ;;
esac
