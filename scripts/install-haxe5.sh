#!/usr/bin/env bash
# Haxe 5.0.0-preview.1(公式バイナリ)をローカルに導入する。
# native player / web playground のコンパイラを Haxe 5 に揃えるため
# (system パッケージの haxe を壊さない。LUB_HAXE で project から参照する)。
# Linux と Windows (git bash) の両対応。
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
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

case "$(uname -s)" in
  MINGW* | MSYS*) windows=1 ;;
  *) windows=0 ;;
esac

if [ -x "$DIR/haxe" ] || [ -x "$DIR/haxe.exe" ]; then
  if "$DIR/haxe" --version 2>/dev/null | grep -q "$VER"; then
    echo "Haxe $VER は既に $DIR にあります。"
    installed=1
  fi
fi
if [ "${installed:-0}" -ne 1 ]; then
  echo "==> downloading Haxe $VER -> $DIR"
  if [ "$windows" -eq 1 ]; then
    URL="https://github.com/HaxeFoundation/haxe/releases/download/${VER}/haxe-${VER}-win64.zip"
    tmp="$(mktemp -d)"
    curl -fsSL -o "$tmp/haxe.zip" "$URL"
    unzip -q "$tmp/haxe.zip" -d "$tmp/x"
    # zip は haxe-<ver>/ を1段挟むので剥がして配置する。
    inner="$(find "$tmp/x" -maxdepth 1 -mindepth 1 -type d | head -1)"
    mkdir -p "$DIR"
    cp -r "$inner"/. "$DIR"/
    rm -rf "$tmp"
  else
    URL="https://github.com/HaxeFoundation/haxe/releases/download/${VER}/haxe-${VER}-linux64.tar.gz"
    tmp="$(mktemp)"
    curl -fsSL -o "$tmp" "$URL"
    mkdir -p "$DIR"
    tar -xzf "$tmp" -C "$DIR" --strip-components=1
    rm -f "$tmp"
  fi
  echo "==> installed: $("$DIR/haxe" --version)"
fi

if ! HAXE_STD_PATH="$DIR/std" "$DIR/haxelib" config >/dev/null 2>&1; then
  HAXELIB_REPO_DIR="${HAXELIB_REPO_DIR:-$HOME/haxelib}"
  echo "==> haxelib setup $HAXELIB_REPO_DIR"
  mkdir -p "$HAXELIB_REPO_DIR"
  HAXE_STD_PATH="$DIR/std" "$DIR/haxelib" setup "$HAXELIB_REPO_DIR" >/dev/null
fi

# lub extern を haxelib に dev 登録(共有 haxelib repo。既存なら上書きで害なし)。
# Windows: haxelib.exe が読む dev パスは Windows 形式でなければならないので
# mixed 形式 (C:/...) に変換して渡す。
LUB_DEV_PATH="$REPO_ROOT/haxe-lib/lub"
if [ "$windows" -eq 1 ] && command -v cygpath >/dev/null; then
  LUB_DEV_PATH="$(cygpath -m "$LUB_DEV_PATH")"
fi
echo "==> haxelib dev lub $LUB_DEV_PATH"
HAXE_STD_PATH="$DIR/std" "$DIR/haxelib" dev lub "$LUB_DEV_PATH" >/dev/null 2>&1 || \
  echo "   (haxelib dev 登録は手動で: $DIR/haxelib dev lub $LUB_DEV_PATH)"

cat <<EOF

完了。native player を Haxe 5 で動かすには:

  export LUB_HAXE="$DIR/haxe"
  ./build/lub samples/01_triangle/01_triangle.hxml

web playground 用アセットの再生成は: cd web && npm run gen-haxe
EOF
