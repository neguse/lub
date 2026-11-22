#!/usr/bin/env bash
# Turns a tcs2c game into an APK for Android (Meta Quest) (docs/android.md).
# Builds the player (libmain.so) with the NDK, puts it into the APK together with the Java code of SDL (SDLActivity), the OpenXR loader and the game data, and signs it.
# Does not use Gradle; assembles it with the build-tools of the Android SDK (aapt2, d8, zipalign, apksigner).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
  cat <<EOF
Usage: bash scripts/build-android.sh --game DIR --package NAME --label TEXT -o OUT.apk [options]

  --game DIR          game.c と binding.c (tcs2c と lub-gen tcs の出力) がある dir
  --assets DIR        APK に入れるデータ。player はこれを cwd に置いて動く (dev と同じ相対パス)
  --package NAME      Android の package 名 (例: dev.neguse.lub.hello)
  --label TEXT        app の名前
  -o, --output FILE   出力の APK
  --version-code N    既定 1
  --version-name S    既定 1.0
  --build-dir DIR     既定 build-android
  --keystore FILE     署名の keystore。無ければ debug 用を作って使う (既定 ~/.android/debug.keystore)
  -DNAME=VALUE        CMake の configure に渡す (例: -DLUB_TCS_HOST=...)

環境変数: ANDROID_HOME (SDK)、ANDROID_NDK_HOME (無ければ SDK の ndk/ の最新)、JAVA_HOME (無ければ PATH の javac)。
EOF
}

game="" assets="" package="" label="" output=""
version_code=1 version_name=1.0
build_dir="build-android"
keystore="${HOME}/.android/debug.keystore"
defines=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --game) game="$2"; shift 2 ;;
    --assets) assets="$2"; shift 2 ;;
    --package) package="$2"; shift 2 ;;
    --label) label="$2"; shift 2 ;;
    -o | --output) output="$2"; shift 2 ;;
    --version-code) version_code="$2"; shift 2 ;;
    --version-name) version_name="$2"; shift 2 ;;
    --build-dir) build_dir="$2"; shift 2 ;;
    --keystore) keystore="$2"; shift 2 ;;
    -D*) defines+=("$1"); shift ;;
    -h | --help) usage; exit 0 ;;
    *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done
if [[ -z "$game" || -z "$package" || -z "$label" || -z "$output" ]]; then
  usage >&2
  exit 2
fi

# min is 29 (Android 10), well older than the OS of Quest 2. target follows the requirements of the Meta store.
min_sdk=29
target_sdk=32
abi=arm64-v8a

sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
if [[ -z "$sdk" || ! -d "$sdk" ]]; then
  echo "ANDROID_HOME is not set to an Android SDK" >&2
  exit 2
fi
ndk="${ANDROID_NDK_HOME:-}"
if [[ -z "$ndk" ]]; then
  ndk="$(ls -d "$sdk"/ndk/*/ 2>/dev/null | sort -V | tail -1)"
fi
ndk="${ndk%/}"
if [[ ! -f "$ndk/build/cmake/android.toolchain.cmake" ]]; then
  echo "no Android NDK (set ANDROID_NDK_HOME)" >&2
  exit 2
fi
build_tools="$(ls -d "$sdk"/build-tools/*/ 2>/dev/null | sort -V | tail -1)"
build_tools="${build_tools%/}"
platform="$(ls -d "$sdk"/platforms/android-*/ 2>/dev/null | sort -V | tail -1)"
android_jar="${platform%/}/android.jar"
if [[ ! -x "$build_tools/aapt2" || ! -f "$android_jar" ]]; then
  echo "the Android SDK needs build-tools and a platform" >&2
  exit 2
fi
javac="${JAVA_HOME:+$JAVA_HOME/bin/}javac"
keytool="${JAVA_HOME:+$JAVA_HOME/bin/}keytool"
host_tag="$(ls "$ndk/toolchains/llvm/prebuilt" | head -1)"
llvm_bin="$ndk/toolchains/llvm/prebuilt/$host_tag/bin"

abs_path() {
  (cd "$(dirname "$1")" && echo "$(pwd)/$(basename "$1")")
}
game="$(cd "$game" && pwd)"
[[ -n "$assets" ]] && assets="$(cd "$assets" && pwd)"
output="$(abs_path "$output")"
build_dir="$(mkdir -p "$build_dir" && cd "$build_dir" && pwd)"

# --- player (libmain.so), SDL, and the OpenXR loader
cmake -S "$repo_root" -B "$build_dir/native" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$min_sdk" \
  -DANDROID_STL=c++_shared -DCMAKE_BUILD_TYPE=Release \
  -DLUB_TCS_GAME="$game/game.c" -DLUB_TCS_BINDING="$game/binding.c" \
  ${defines[@]+"${defines[@]}"}
cmake --build "$build_dir/native" --target lub

stage="$build_dir/apk"
rm -rf "$stage"
mkdir -p "$stage/root/lib/$abi" "$stage/classes" "$stage/assets"
libs=(
  "$build_dir/native/libmain.so"
  "$build_dir/native/deps/lib/libSDL3.so"
  "$build_dir/native/deps/lib/libopenxr_loader.so"
  "$ndk/toolchains/llvm/prebuilt/$host_tag/sysroot/usr/lib/aarch64-linux-android/libc++_shared.so"
)
for lib in "${libs[@]}"; do
  "$llvm_bin/llvm-strip" --strip-unneeded -o "$stage/root/lib/$abi/$(basename "$lib")" "$lib"
done

# --- Java code of SDL
java_src="$repo_root/third_party/SDL/android-project/app/src/main/java"
find "$java_src" -name '*.java' > "$stage/java-sources.txt"
"$javac" -nowarn -source 11 -target 11 -encoding UTF-8 \
  -classpath "$android_jar" \
  -d "$stage/classes" @"$stage/java-sources.txt" 2>&1 | grep -v "^warning: \[options\]\|^Note: \|^[0-9]* warning" || true
find "$stage/classes" -name '*.class' > "$stage/classes.txt"
[[ -s "$stage/classes.txt" ]] || { echo "javac produced no classes" >&2; exit 1; }
"$build_tools/d8" --release --min-api "$min_sdk" --lib "$android_jar" \
  --output "$stage/root" @"$stage/classes.txt"

# --- Data: at startup the player copies the files listed in lub-files.txt to internal storage (src/android.c)
if [[ -n "$assets" ]]; then
  cp -R "$assets"/. "$stage/assets/"
  (
    cd "$stage/assets"
    files="$(find . -type f ! -name lub-files.txt | sed 's|^\./||' | LC_ALL=C sort)"
    stamp="$( (printf '%s\n' "$files"; printf '%s\n' "$files" | tr '\n' '\0' | xargs -0 cat) | sha256sum | cut -c1-32)"
    { echo "$stamp"; printf '%s\n' "$files"; } > lub-files.txt
  )
fi

# --- manifest, signing
sed -e "s|@PACKAGE@|$package|g" -e "s|@LABEL@|$label|g" \
  "$repo_root/src/android/AndroidManifest.xml" > "$stage/AndroidManifest.xml"
"$build_tools/aapt2" link -o "$stage/base.apk" -I "$android_jar" \
  --manifest "$stage/AndroidManifest.xml" \
  --min-sdk-version "$min_sdk" --target-sdk-version "$target_sdk" \
  --version-code "$version_code" --version-name "$version_name" \
  -A "$stage/assets"
(cd "$stage/root" && zip -q -r "$stage/base.apk" classes.dex lib)
"$build_tools/zipalign" -f -P 16 4 "$stage/base.apk" "$stage/aligned.apk"

if [[ ! -f "$keystore" ]]; then
  mkdir -p "$(dirname "$keystore")"
  "$keytool" -genkeypair -keystore "$keystore" -storepass android -keypass android \
    -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 \
    -dname "CN=Android Debug,O=Android,C=US" >/dev/null
fi
mkdir -p "$(dirname "$output")"
"$build_tools/apksigner" sign --ks "$keystore" --ks-pass pass:android \
  --key-pass pass:android --v4-signing-enabled false \
  --out "$output" "$stage/aligned.apk"
"$build_tools/apksigner" verify "$output"
echo "$output"
