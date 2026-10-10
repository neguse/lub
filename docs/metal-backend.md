# Metal Backend

macOS / iOS 用の Metal backend("metal")。`RenderBackend` vtable
(`src/backend.h`) の実装の一つで、抽象は変更していない。Metal 固有の概念は
すべて `src/backend_metal.m` 内に閉じる。

## 選択と配置

- 実装: `src/backend_metal.m`(Objective-C、ARC)。CMake は `APPLE` のみ
  ソースを追加し、Metal / QuartzCore / Foundation をリンクする。
- 選択: `config({ backend = "metal" })` / `LUB_BACKEND=metal`。
  macOS / iOS の既定 backend(Apple 以外ではエラー)。
- window は SDL3。`SDL_WINDOW_METAL | SDL_WINDOW_HIGH_PIXEL_DENSITY` で作り、
  `SDL_Metal_CreateView` が返す `CAMetalLayer` に描く。swapchain は
  `BGRA8Unorm`。iOS では window を fullscreen にする(status bar が消える)。
- macOS では `sdlgpu` backend は動かない(SPIR-V 専用で、Vulkan の
  portability library を要求する)。`tests/lua/` のテストは既定が `sdlgpu`
  なので、`LUB_BACKEND=metal` を付けて動かす。

## Shader 経路: Slang → MSL

`SHADER_TARGET_METAL`(`shader.cpp`)が `SLANG_METAL` でコンパイルし、
MSL のソースを blob として返す。backend は `newLibraryWithSource` で
実行時にコンパイルする。entry point は `vs_main` / `fs_main` / `cs_main`。

- VS と FS は別々に compile する。stage 間の varying は semantic から付く
  `[[user(...)]]` の名前で一致する。
- reflection の slot は、Slang が stage ごとに振った `[[buffer]]` /
  `[[texture]]` / `[[sampler]]` の index そのまま。remap はせず、backend は
  その index に bind する。Metal では uniform block と StructuredBuffer が
  同じ `[[buffer]]` の index 空間を使う。
- uniform block は Metal の layout(float3 は 16 byte)になる。詰める位置は
  reflection の offset なので API 側の扱いは他 target と同じ。MSL は struct
  の大きさを alignment の倍数に丸めるので、backend は渡す data を 16 byte
  境界まで 0 で埋める。
- StructuredBuffer の要素は、他 target と同じ詰めた layout(float3 は
  12 byte)で読む。Slang は MSL に `float3` を出すので、`shader.cpp` が
  buffer から届く struct ごとに、3 成分 vector を `packed_float3` にした
  `<名前>_packed` を足し、buffer の pointer をそちらへ向ける。この struct は
  元の struct と相互に変換でき、shader の計算は元の struct のまま行う。
  詰めた layout であることの検査(`docs/manual/04-gfx.md` の規則)は、
  Slang の Metal 用 layout ではなく型から std430 の alignment を計算して行う。

## Frame model

- frame 全体を 1 本の command buffer に記録する。pass、compute、blit は
  記録した順に実行される。
- `begin_frame`: layer の `drawableSize` を window の pixel size に合わせ、
  `nextDrawable` を取る。window が隠れていて drawable が無い frame は、
  swapchain pass の描画を捨てる。
- `end_frame`: `presentDrawable` → `commit`。
- default depth は swapchain と同じ大きさの `Depth32Float` 1 枚。Apple の GPU
  には D24 が無く、lub は stencil 操作を持たないので、`DEPTH24_STENCIL8` の
  image も `Depth32Float` で作る。

## Binding と resource

- uniforms: `setVertexBytes` / `setFragmentBytes` で draw ごとに渡す。4 KB
  以上は一時 buffer。
- texture と sampler、storage buffer は `apply_bindings` / `dispatch` で
  reflection の slot に直接 bind する。
- buffer / texture の更新は、その場に書かず新しい `MTLBuffer` /
  `MTLTexture` に差し替える。記録済みの draw は commit まで実行されないので、
  その場に書くと先に記録した draw まで新しい内容を読んでしまう。古い方は
  command buffer が参照している間は生きている。
- scissor は pass の viewport からはみ出さないように切り詰める。空になった
  ときは次の `set_scissor` まで draw を捨てる。
- readback と capture は同期。texture を CPU から読める buffer へ blit し、
  そこまでの command buffer を commit して完了を待ち、frame の残りは新しい
  command buffer に記録する(`capture_before_end_frame = true`)。

## 高 pixel 密度と safe area

- window は実 pixel で描く。SDL の mouse 座標は point なので、入力は
  framebuffer pixel に直してから渡す(`lub_host_pixel_density`)。
- ノッチや home indicator のある端末では、main target を safe area にする
  (`lub_host_main_rect`)。swapchain pass は drawable 全体を clear し、
  viewport と scissor を safe area に置く。`Gfx.Size` は safe area の大きさを
  返し、入力の原点も safe area の左上になる。safe area の外には clear color
  だけが見える。

## Slang を持たない player と shader cache

iOS 用の Slang prebuilt は無いので、iOS の player は Slang をリンクしない
(CMake の `LUB_NO_SLANG`。iOS では常に ON、他の platform でも指定できる)。
この player の shader は shader cache だけから来る。

- shader cache(`src/shader_cache.c`)は、shader compile の出力(blob と
  reflection)を「生成器の版(Slang の版と revision)、target、ソース」の
  hash を名前にした `<key>.lubshader` として 1 key = 1 file で保存する。
- 読む場所は env `LUB_SHADER_CACHE`、無ければ cwd の `shader-cache/`。
  hit すれば Slang を持つ player もそれを使う。
- 書くのは env `LUB_SHADER_CACHE` があるときだけ。Slang を持つ player が
  compile のたびに書く。
- 埋め方: Mac の lub でゲームを `LUB_SHADER_CACHE=<dir>` 付きで動かし、使う
  shader を一通り作らせる。足りない shader は、Slang を持たない player で
  key を名指しする error になる。
- 位置付けは `docs/log/2026-07-23-packaging-design.md` の生成キャッシュ。

## iOS の app

- app は `src/main.c` と `lub_objs` をリンクした bundle。ゲーム側の CMake が
  lub を `add_subdirectory` して作る(例: neguse/saikyo の `ios/`)。iOS では
  SDL3 を static にし、Lua は `LUA_USE_IOS`、miniaudio は Objective-C で
  compile する。
- script の指定が無い起動では、実行ファイルの隣(bundle の resource の
  場所)の `game.lua` を動かす。iOS では cwd を bundle にするので、
  `samples/boot.lua`、`third_party/lume/lume.lua`、ゲームのデータ、
  `shader-cache/` を dev と同じ相対パスで bundle に置く。
- SDL は 3.4 以降が要る。iOS 27 は UIScene life cycle を使わない app を
  起動直後に止め、SDL がそれを使うのは 3.4.0 から。
- 音は AVAudioSession の ambient(消音スイッチに従い、他の app の音と
  混ざる)、48 kHz。
- 保存は `Io.SaveText`。app の中から書ける場所は env `HOME` の下
  (`Library/Application Support/` など)。

## 検証

`scripts/apple-gate.sh` が CI の macos / ios job (`--part mac` / `--part ios`)
と手元の Mac (既定で両方) で同じ内容を回す: Release build、C の smoke test、
`tests/lua/` の runtime テスト、視覚 golden、shader cache の往復、iOS の build
と simulator での実行。

- 視覚 golden: Metal には機材に依存しない CPU rasterizer が無いので、Metal
  用の golden は持たない。`scripts/run-golden.sh` は macOS では metal backend
  で capture し、Linux の golden(`*_sdlgpu.png`)と許容差で比べる
  (`scripts/png-diff.py`。pixel の差は RGB の最大差で、平均が 3 以下、かつ
  16 を超える pixel が 1% 以下)。`--update` は macOS からは書かない。
- capture の大きさ: `--capture` を付けた run は、window の実 pixel ではなく
  要求した大きさ(`config` の width / height、無ければ 1280x720)で描く。
  画面より大きい window は縮められ、Retina では pixel が倍になるので、
  window に合わせると capture の大きさが機材で変わるため。drawable だけを
  その大きさにし、表示は layer が拡縮する。
- shader cache の往復: Slang を持つ player に cache を書かせ、`LUB_NO_SLANG`
  の player が同じ frame を cache だけから描いて byte 一致すること、cache が
  空なら key を名指しする error になることを確かめる。
- iOS: Ninja で player の app(`lub.app`、bundle id `dev.neguse.lub`)を
  simulator 向けに build する(Xcode generator は configure の機能検査ごとに
  xcodebuild を起こして遅い。Info.plist は CMake の変数で書いてあるので、
  実機向けに Xcode generator で build しても同じ bundle になる)。その app に boot.lua、
  lume、`tests/lua/`、Mac の player が埋めた shader cache を入れて simulator
  で動かす。描画テストは capture を Linux の golden と許容差で比べる。
  simctl は app の終了 code を返さないので、終了 code で合否が決まるテストは
  `tests/lua/run_marked.lua` で包み、出力の `LUB_TEST_EXIT` で判定する。

## 制約 / 未対応

- Metal の golden は持たない(上の「検証」)。
- iOS の検証は simulator まで。実機は CI に無い。
- buffer の要素の float3 を、要素を local に受けずに直接行列と演算する式
  (`mul(m, verts[i].nrm)` の形)は MSL の compile error になる。
  `V v = verts[i];` と一度受けてから使う。
