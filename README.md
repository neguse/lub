# sglua (PoC)

Lua から扱える薄い 3D 描画ライブラリの PoC。SDL3 + sokol_gfx + Slang + Lua 5.4。

## ビルド

依存:
- CMake 3.20+
- C11 / C++ 対応コンパイラ (GCC / Clang)
- Linux x86_64 (現状 GL 3.3 backend のみ)

```sh
# Slang prebuilt は third_party/slang/lib に配置済み (gitignore 対象)
cmake -S . -B build
cmake --build build -j
```

## 実行

```sh
./build/sglua samples/01_triangle.lua
./build/sglua samples/02_vertex_color.lua
./build/sglua samples/03_texture.lua
./build/sglua samples/04_mvp.lua
```

ヘッドレス環境では `SDL_VIDEODRIVER=offscreen` を付ける。

## サンプル

| # | スクリプト              | 内容                                            |
|---|-------------------------|-------------------------------------------------|
| 1 | 01_triangle.lua         | 単色オレンジ三角形 (use_buffer / use_shader / draw / begin_pass) |
| 2 | 02_vertex_color.lua     | 頂点カラー補間された三角形                      |
| 3 | 03_texture.lua          | チェッカー柄テクスチャを貼った三角形 (use_texture) |
| 4 | 04_mvp.lua              | 回転行列を uniform で渡す三角形                 |

## API

- `use_buffer(key, type, data, version)` — GPU buffer 宣言。`type` は `VERTEX` / `INDEX`。`data` は float の Lua table。同 `version` なら再アップロードしない。
- `use_texture(key, w, h, format, data, version)` — image + sampler を作成。`format` は `RGBA8` / `R8`。`data` は uint8 の Lua table (省略可)。sampler は LINEAR / REPEAT 固定。
- `use_shader(key, vs_src, fs_src, version)` — Slang shader を compile (`vs_main` / `fs_main` entry points)。GLSL 3.30 へ降格してリフレクションする。
- `begin_pass({ target = main_tex, clear_color = {r,g,b,a} })` / `end_pass()` — pass 制御。`target` は今のところ `main_tex` のみ。
- `draw(count, resources, options)` — 描画コマンド。
  - `resources` は名前付き table: `{ verts = bufferRef, diffuse = textureRef, uniforms = { mvp = {...floats} } }`。テクスチャの名前はシェーダ側のリフレクションに突き合わせる。uniform は uniform block の最初のものに pack される。
  - `options` は `{ shader = shaderRef, blend, depth, depth_write, cull, primitive }`。`shader` だけ必須。

エントリポイント: Lua 側で `on_init` / `on_frame` / `on_event` / `on_quit` の global 関数を定義すると呼ばれる。

詳細は `tasks.md` 参照。

## 未実装 (将来)

- post process / MRT / deferred shading (Sample 5–7)
- ホットリロード版 (use_* の version 引数は対応済みだが Lua 側ファイル監視は未実装)
- リソース sweep (フレーム未参照の自動破棄)
- SDL3 GPU backend
- compute shader / VR / マルチスレッド描画
- Lua 側からの sampler 設定 (filter / wrap)

## アーキテクチャ

```
src/
├── main.c            SDL3 main callbacks エントリ
├── app.{h,c}         App 状態 (window, GL ctx, sokol env, lifecycle)
├── lua_api.{h,c}     Lua bindings (use_*, begin_pass, end_pass, draw)
├── enums.h           SglBufferType / SglPixelFormat / ... の C-side enum
├── enums_lua.{h,c}   それらを Lua グローバルに登録
├── pass.{h,c}        現フレームの pass state
├── resources.{h,c}   key → ResEntry のハッシュマップ (buffer/texture/shader)
├── shader.h, shader.cpp   Slang compile + GLSL 3.30 downversion + reflection
├── pipeline.{h,c}    pipeline state hash → sg_pipeline cache
└── sokol_impl.c      SOKOL_GFX_IMPL の TU
```

依存:
- `third_party/sokol/sokol_gfx.h` — single-header (vendored)
- `third_party/slang/` — Slang 2026.x prebuilt (`include/`, `lib/`)
- SDL3 — CMake FetchContent
- Lua 5.4 — CMake FetchContent (static lib build)

## ライセンス

未定。
