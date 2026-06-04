#!/usr/bin/env bash
# Haxe 5.0.0-preview.1(公式 linux64 バイナリ)をローカルに導入する。
# native player / web playground のコンパイラを Haxe 5 に揃えるため
# (system パッケージの haxe を壊さない。LUB_HAXE で project から参照する)。
#
#   scripts/install-haxe5.sh [INSTALL_DIR]   # 既定: $HOME/haxe5
#
# 導入後の使い方:
#   export LUB_HAXE="$HOME/haxe5/haxe"        # native player が使う haxe バイナリ
#   ./build/lub samples/01_triangle/01_triangle.hxml
#   # HAXE_STD_PATH は haxe_server.c が LUB_HAXE の隣の std/ から自動補完する。
set -euo pipefail

VER="5.0.0-preview.1"
DIR="${1:-${HAXE5_DIR:-$HOME/haxe5}}"
URL="https://github.com/HaxeFoundation/haxe/releases/download/${VER}/haxe-${VER}-linux64.tar.gz"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [ -x "$DIR/haxe" ] && "$DIR/haxe" --version 2>/dev/null | grep -q "$VER"; then
  echo "Haxe $VER は既に $DIR にあります。"
else
  echo "==> downloading Haxe $VER -> $DIR"
  tmp="$(mktemp)"
  curl -fsSL -o "$tmp" "$URL"
  mkdir -p "$DIR"
  tar -xzf "$tmp" -C "$DIR" --strip-components=1
  rm -f "$tmp"
  echo "==> installed: $("$DIR/haxe" --version)"
fi

if ! HAXE_STD_PATH="$DIR/std" "$DIR/haxelib" config >/dev/null 2>&1; then
  HAXELIB_REPO_DIR="${HAXELIB_REPO_DIR:-$HOME/haxelib}"
  echo "==> haxelib setup $HAXELIB_REPO_DIR"
  mkdir -p "$HAXELIB_REPO_DIR"
  HAXE_STD_PATH="$DIR/std" "$DIR/haxelib" setup "$HAXELIB_REPO_DIR" >/dev/null
fi

# lub extern を haxelib に dev 登録(共有 haxelib repo。既存なら上書きで害なし)。
echo "==> haxelib dev lub $REPO_ROOT/haxe-lib/lub"
HAXE_STD_PATH="$DIR/std" "$DIR/haxelib" dev lub "$REPO_ROOT/haxe-lib/lub" >/dev/null 2>&1 || \
  echo "   (haxelib dev 登録は手動で: $DIR/haxelib dev lub $REPO_ROOT/haxe-lib/lub)"

cat <<EOF

完了。native player を Haxe 5 で動かすには:

  export LUB_HAXE="$DIR/haxe"
  ./build/lub samples/01_triangle/01_triangle.hxml

web playground 用アセットの再生成は: cd web && npm run gen-haxe
EOF
