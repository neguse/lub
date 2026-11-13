# shellcheck shell=bash
# lider の test が使う player を決める (scripts/test.sh と scripts/test-editor.sh が source する)。
# 既定は lider の native の拡張 (jamo、file system) を静的に link した lider の player (build/player/lider_player。scripts/build-player.sh が作る)。
# 無ければ lub の player (lub) と、src/*.ref.cs の隣の src/Jamo.so と src/Fs.so (scripts/build-native.sh) を使う。
# env:
#   PLAYER   player の実行ファイル (lider_player か lub)
#   LUB_TCS  tcs の起動コマンド (既定: tcs/ の Transpiler を dotnet で)
lub_root="$(cd "$root/.." && pwd)"
player="${PLAYER:-}"
if [ -z "$player" ]; then
  if [ -x "$root/build/player/lider_player" ]; then
    player="$root/build/player/lider_player"
  else
    player="$lub_root/build-release-linux/lub"
  fi
fi
# lub は lider の拡張を持たないので、src/Jamo.so と src/Fs.so が要る
if [ "$(basename "$player")" = lub ] && { [ ! -f "$root/src/Jamo.so" ] || [ ! -f "$root/src/Fs.so" ]; }; then
  echo "src/Jamo.so or src/Fs.so is missing: run scripts/build-player.sh (lider_player) or scripts/build-native.sh (for lub)" >&2
  exit 1
fi
if [ -z "${LUB_TCS:-}" ]; then
  dll="$lub_root/tcs/Transpiler/bin/Release/net10.0/Transpiler.dll"
  [ -f "$dll" ] || dotnet build "$lub_root/tcs/Transpiler" -c Release >/dev/null
  export LUB_TCS="dotnet $dll"
fi
# lider の player は lub の実行ファイルの隣に無いので、lub の cs-lib を教える
export LUB_CS_LIB="${LUB_CS_LIB:-$lub_root/cs-lib}"
