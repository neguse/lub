# shellcheck shell=bash
# Picks the player used by lider's tests (sourced by scripts/test.sh and scripts/test-editor.sh).
# The default is the lider player with lider's native extensions (jamo, file system) statically linked (build/player/lider_player, built by scripts/build-player.sh).
# If it is missing, uses the lub player (lub) with src/Jamo.so, src/Fs.so and src/Tcs.so next to src/*.ref.cs (scripts/build-native.sh).
# env:
#   PLAYER   player executable (lider_player or lub)
#   LUB_TCS  tcs launch command (default: the .NET tcs, built by scripts/build-tcs-dotnet.sh of lub)
lub_root="$(cd "$root/.." && pwd)"
player="${PLAYER:-}"
if [ -z "$player" ]; then
  if [ -x "$root/build/player/lider_player" ]; then
    player="$root/build/player/lider_player"
  else
    player="$lub_root/build-release-linux/lub"
  fi
fi
# lub does not have lider's extensions, so src/Jamo.so and src/Fs.so are needed
if [ "$(basename "$player")" = lub ] && { [ ! -f "$root/src/Jamo.so" ] || [ ! -f "$root/src/Fs.so" ] || [ ! -f "$root/src/Tcs.so" ]; }; then
  echo "src/Jamo.so, src/Fs.so or src/Tcs.so is missing: run scripts/build-player.sh (lider_player) or scripts/build-native.sh (for lub)" >&2
  exit 1
fi
LUB_TCS="${LUB_TCS:-$("$lub_root/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
# The lider player is not next to the lub executable, so tell it where lub's cs-lib is
export LUB_CS_LIB="${LUB_CS_LIB:-$lub_root/cs-lib}"
