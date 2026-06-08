#!/usr/bin/env bash
# Format first-party source with each formatter's default style.
# Project-specific config files (.clang-format / hxformat.json / .prettierrc)
# are intentionally absent, so each tool's stock style is used.
#
#   clang-format     : C/C++ and Slang-as-HLSL (LLVM default)
#   haxelib formatter: Haxe                    - `haxelib install formatter`
#   prettier         : Web TS/MJS/JSON/HTML    - `npm --prefix web install`
#
# Usage:
#   scripts/format.sh            # 全部整形
#   scripts/format.sh --check    # 整形が必要か確認のみ (CI 向け, 非ゼロ終了で失敗)
set -euo pipefail
cd "$(dirname "$0")/.."

check=0
case "${1:-}" in
    --check) check=1 ;;
    -h | --help)
        sed -n '2,14p' "$0"
        exit 0
        ;;
    "") ;;
    *)
        echo "unknown arg: $1" >&2
        exit 2
        ;;
esac

git_files() {
    git ls-files -z -- "$@" \
        ':!:third_party/**' \
        ':!:spike/**' \
        ':!:web/dist/**' \
        ':!:web/public/**' \
        ':!:web/node_modules/**'
}

run_if_any() {
    local -n files_ref=$1
    shift
    if [[ ${#files_ref[@]} -gt 0 ]]; then
        "$@" "${files_ref[@]}"
    fi
}

mapfile -d '' C_FILES < <(git_files '*.c' '*.cc' '*.cpp' '*.h' '*.hpp')
mapfile -d '' HAXE_FILES < <(git_files 'haxe-lib/**/*.hx' 'samples/**/*.hx')
mapfile -d '' SLANG_FILES < <(git_files 'samples/**/*.slang' 'tests/**/*.slang')
mapfile -d '' WEB_FILES < <(git_files \
    'web/*.html' \
    'web/*.json' \
    'web/*.ts' \
    'web/playground/**/*.ts' \
    'web/scripts/**/*.mjs')

HAXE_ARGS=()
for f in "${HAXE_FILES[@]}"; do
    HAXE_ARGS+=(-s "$f")
done

if [[ $check -eq 1 ]]; then
    run_if_any C_FILES clang-format --dry-run --Werror
    if [[ ${#HAXE_ARGS[@]} -gt 0 ]]; then
        haxelib run formatter --check "${HAXE_ARGS[@]}"
    fi
    run_if_any SLANG_FILES clang-format --assume-filename=shader.hlsl --dry-run --Werror
    run_if_any WEB_FILES npx --prefix web prettier --check
else
    run_if_any C_FILES clang-format -i
    if [[ ${#HAXE_ARGS[@]} -gt 0 ]]; then
        haxelib run formatter "${HAXE_ARGS[@]}"
    fi
    run_if_any SLANG_FILES clang-format --assume-filename=shader.hlsl -i
    run_if_any WEB_FILES npx --prefix web prettier --write
fi
