#!/usr/bin/env bash
# Format first-party source with each formatter's default style.
# Project-specific config files (.clang-format / hxformat.json / .prettierrc)
# are intentionally absent, so each tool's stock style is used.
#
#   clang-format     : C/C++ and Slang-as-HLSL (LLVM default)
#   stylua           : Lua                     - `npm --prefix web install`
#   prettier         : Web TS/MJS/JSON/HTML    - `npm --prefix web install`
#   dotnet format    : C# (whitespace)         - dotnet SDK 付属
#
# Usage:
#   scripts/format.sh            # 全部整形
#   scripts/format.sh --check    # 整形が必要か確認のみ (CI 向け, 非ゼロ終了で失敗)
#   scripts/format.sh --changed  # HEAD から変更のあるファイルだけを対象にする
set -euo pipefail
cd "$(dirname "$0")/.."

check=0
changed=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --check) check=1 ;;
        --changed) changed=1 ;;
        -h | --help)
            sed -n '2,15p' "$0"
            exit 0
            ;;
        *)
            echo "unknown arg: $1" >&2
            exit 2
            ;;
    esac
    shift
done

git_files() {
    if [[ $changed -eq 1 ]]; then
        # Working tree + index vs HEAD; deletions have nothing to format.
        git diff -z --name-only --diff-filter=d HEAD -- "$@" \
            ':!:third_party/**' \
            ':!:web/dist/**' \
            ':!:web/public/**' \
            ':!:web/node_modules/**'
    else
        git ls-files -z -- "$@" \
            ':!:third_party/**' \
            ':!:web/dist/**' \
            ':!:web/public/**' \
            ':!:web/node_modules/**'
    fi
}

run_if_any() {
    local -n files_ref=$1
    shift
    if [[ ${#files_ref[@]} -gt 0 ]]; then
        "$@" "${files_ref[@]}"
    fi
}

mapfile -d '' C_FILES < <(git_files '*.c' '*.cc' '*.cpp' '*.h' '*.hpp')
mapfile -d '' SLANG_FILES < <(git_files 'samples/**/*.slang' 'tests/**/*.slang')
mapfile -d '' LUA_FILES < <(git_files '*.lua')
mapfile -d '' CS_FILES < <(git_files 'samples/**/*.cs' 'cs-lib/**/*.cs' 'dotnet/**/*.cs' 'templates/**/*.cs')
mapfile -d '' WEB_FILES < <(git_files \
    'web/*.html' \
    'web/*.json' \
    'web/*.ts' \
    'web/playground/**/*.ts' \
    'web/scripts/**/*.mjs')

if [[ $check -eq 1 ]]; then
    run_if_any C_FILES clang-format --dry-run --Werror
    run_if_any SLANG_FILES clang-format --assume-filename=shader.hlsl --dry-run --Werror
    run_if_any LUA_FILES env XDG_CONFIG_HOME=/nonexistent npx --prefix web stylua --no-editorconfig --check --verify
    run_if_any WEB_FILES npx --prefix web prettier --check
    run_if_any CS_FILES dotnet format whitespace . --folder --verify-no-changes --include
else
    run_if_any C_FILES clang-format -i
    run_if_any SLANG_FILES clang-format --assume-filename=shader.hlsl -i
    run_if_any LUA_FILES env XDG_CONFIG_HOME=/nonexistent npx --prefix web stylua --no-editorconfig --verify
    run_if_any WEB_FILES npx --prefix web prettier --write
    run_if_any CS_FILES dotnet format whitespace . --folder --include
fi
