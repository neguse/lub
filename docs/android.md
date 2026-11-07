# Android (Meta Quest)

Android の player は Meta Quest で動く VR の app で、tcs2c で C にしたゲームを OpenXR (Vulkan) で動かす。
対象は Quest 2 以降のヘッドセットで、Android のスマートフォンの画面に描く player はまだ無い。

## 構成

- app は SDL の `SDLActivity` をそのまま Activity にした APK で、SDL の Java (`third_party/SDL/android-project`) を入れる。
- SDLActivity は `libSDL3.so` と `libmain.so` を読み込み、`libmain.so` の `SDL_main` を呼ぶ。
- `libmain.so` は `src/tcs_host.c` とゲームの C (`LUB_TCS_GAME` と `LUB_TCS_BINDING`) と `lub_objs` で、CMake の `lub` の target が Android ではこの共有 library になる。
- Android の lub は tcs2c のゲームだけを動かす (tcs VM の player と .NET 実行は無い)。
- 既定の backend は `openxr`。
- OpenXR の loader は `third_party/openxr` の Khronos の loader を APK に入れ、runtime は Quest の system が持つものを runtime broker で探す。
- loader には SDL の Activity と Java VM を渡す (`XR_KHR_loader_init_android` と `XR_KHR_android_create_instance`、`src/xr.c`)。
- Vulkan backend は Vulkan 1.3 の device か、1.1 で `VK_KHR_synchronization2` と `VK_KHR_timeline_semaphore` を持つ device で動く (`src/backend_vulkan.c`)。
- dynamic rendering (`VK_KHR_dynamic_rendering`) の無い device (Quest 2 の Adreno 650) では、pass を VkRenderPass と VkFramebuffer で描く。
  `LUB_VK_RENDER_PASS=1` はどの device でもこちらで描き、Linux の lavapipe の golden でこの経路を確かめられる。
- Slang は持たない (`LUB_NO_SLANG`)。
  shader は shader cache (`cache/shader/`) からだけ読むので、`lub export android` で Linux などの lub に作らせて APK に入れる (`docs/metal-backend.md` の shader cache)。

## データと保存

- ゲームのデータは APK の assets に dev と同じ相対パスで入れる。
- runtime は file を `fopen` で読むので、起動時に assets を内部ストレージの `data/` に写し、そこを cwd にする (`src/android.c`)。
- 写す file は assets の `lub-files.txt` が列挙する。
  1 行目は中身の hash で、前の起動で写したものと同じなら写し直さない。
- `save/<名前>/` は内部ストレージの直下 (`SDL_GetPrefPath`) で、`data/` とは別なので app を更新しても消えない。
  Android の `SDL_GetPrefPath` は名前を区別しないので、1 つの APK の中では `<名前>` が違っても同じ場所になる。
- Activity が止まっても SDL の main loop は止めない (`SDL_HINT_ANDROID_BLOCK_ON_PAUSE` を 0)。
  OpenXR の session の終わり (STOPPING) を処理するため。

## APK を作る

`scripts/build-android.sh` が player を NDK で build し、APK を組んで署名する。
Gradle は使わず、Android SDK の build-tools (aapt2、d8、zipalign、apksigner) を使う。

```sh
bash scripts/build-android.sh --game build/mygame --assets build/mygame-data \
  --package dev.example.mygame --label "My Game" -o build/mygame.apk
```

- `--game` は `game.c` (`tcs2c --lib`) と `binding.c` (`lub-gen tcs`) がある dir。
- `--assets` は APK に入れるデータの dir で、`lub export android` の出力の `cache/shader/` もここに入れる。
- 署名は `--keystore` の keystore で、無ければ `~/.android/debug.keystore` を作って使う (パスワードは `android`)。
- 要るもの: Android SDK (`ANDROID_HOME`) の build-tools 36 と platform 35、NDK (r27 で確かめた)、JDK、CMake、Ninja、zip。
  build-tools 34 の d8 は JDK 21 の javac の出力で内部 error になる。
- `minSdkVersion` は 29、`targetSdkVersion` は 32、ABI は arm64-v8a。

Quest への入れ方は、開発者モードにした Quest を USB でつなぎ、`adb install -r <apk>` で入れる。
app は「提供元不明」の一覧から起動する。
log は `adb logcat -s SDL SDL/APP` で見る。

## 制約 / 未対応

- 実機の検証は手で行う。
  CI は Android の build を持たず、Quest の実機での動作は自動で確かめていない。
- スマートフォンの画面 (Vulkan の surface) に描く player、タッチ入力、Android の tcs VM の player は無い。
