#!/usr/bin/env bash
# 全ソースを各フォーマッタの「デフォルト設定」で整形する。
# プロジェクト固有の設定ファイル (.clang-format / hxformat.json / .prettierrc)
# は意図的に置かず、各ツール標準のスタイルに従う。
#
#   clang-format     : C/C++  (LLVM default)
#   haxelib formatter: Haxe   (default)        ← `haxelib install formatter`
#   prettier         : Web TS (default)        ← web/ で `npm install`
#
# Usage:
#   scripts/format.sh            # 全部整形
#   scripts/format.sh --check    # 整形が必要か確認のみ (CI 向け, 非ゼロ終了で失敗)
set -euo pipefail
cd "$(dirname "$0")/.."

check=0
[[ "${1:-}" == "--check" ]] && check=1

C_FILES=(src/*.c src/*.h src/*.cpp tests/c/*.c)

if [[ $check -eq 1 ]]; then
    clang-format --dry-run --Werror "${C_FILES[@]}"
    haxelib run formatter --check -s samples -s haxe-lib
    ( cd web && npx prettier --check 'playground/**/*.ts' vite.config.ts )
else
    clang-format -i "${C_FILES[@]}"
    haxelib run formatter -s samples -s haxe-lib
    ( cd web && npx prettier --write 'playground/**/*.ts' vite.config.ts )
fi
