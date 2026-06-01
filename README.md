# lub

`lub` は、細部までこだわったゲーム体験を作るためのコード中心のゲーム開発環境。
ゲームの動作を止めずにコード変更を即座に反映し、試行錯誤の速度を上げることを
コアな価値に置く。

runtime は C/C++ と既存ライブラリで組み、Lua を通して API を呼び出す。
上位の script layer は Haxe で書き、Lua に transpile して hot reload する想定。
特定のアセット形式や GUI editor に依存せず、ゲームを構成する状態、描画、
入力、物理、音、debug 情報をコードから制御できる環境を目指す。

現時点の実装は SDL3 + Slang + Lua 5.5 を基盤にし、GPU backend として
**sokol_gfx (Vulkan/WebGPU)** と **SDL3 GPU API** の 2 系統を持つ。
対応プラットフォームは Linux x86_64、Windows x86_64、WebAssembly/WebGPU。

## ドキュメント

- [docs/design.md](docs/design.md): lub の why / to-be / 設計原則。
- [docs/roadmap.md](docs/roadmap.md): API を固めるための実装順序。

## ビルド

依存:
- CMake 3.20+
- C11 / C++17 対応コンパイラ (GCC / Clang / MSVC)
- Vulkan SDK / loader
  - Linux — Arch: `vulkan-icd-loader`、Debian/Ubuntu: `libvulkan-dev`
  - Windows — LunarG Vulkan SDK (`winget install KhronosGroup.VulkanSDK`)

Slang prebuilt (`slang.dll` / `libslang.so` 等) は configure 時に
`third_party/slang/lib/` に無ければ GitHub release から自動取得する
(`third_party/slang/{lib,bin}/` は gitignore 対象)。

Linux:

```sh
cmake -S . -B build
cmake --build build -j
```

Windows (PowerShell, MSVC + Ninja):

```powershell
$env:VULKAN_SDK = "C:\VulkanSDK\1.4.341.1"  # winget でインストールされた SDK
& 'C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat'
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
```

CMake の POST_BUILD で `SDL3.dll` と Slang ランタイム DLL 群が `lub.exe`
の横にコピーされるので、追加の PATH 設定なしで実行できる。

## コードフォーマット

各フォーマッタの **デフォルト設定** で整形する。既存スタイルに寄せる
プロジェクト固有の設定ファイル (`.clang-format` / `hxformat.json` /
`.prettierrc`) は意図的に置かず、ツール標準のスタイルに従う。

- C/C++ — `clang-format` (LLVM default)
- Haxe — `haxelib formatter` (要 `haxelib install formatter`)
- Web TS — `prettier` (`web/` で `npm install` 後に利用可)

```sh
scripts/format.sh            # 全ソースを整形
scripts/format.sh --check    # 整形が必要か確認のみ (CI 向け / 非ゼロ終了で失敗)
```

## 実行

```sh
./build/lub samples/01_triangle/01_triangle.hxml
```

(Windows は `.\build\lub.exe samples\01_triangle\01_triangle.hxml` 形式)

サンプルは `samples/<name>/` に 1 つずつ自己完結する形で置く
(`<ClassName>.hx` + `<name>.hxml` + `data/`)。

### Haxe sample の実行

Haxe で書いた sample を `.hxml` を entry にして直接実行できる。`.hx` 編集 → 保存で hot reload される。

依存:
- Haxe 4.3+ (`haxe --version` で確認)
- 1 回だけ extern を haxelib に登録: `haxelib dev lub <repo>/haxe-lib/lub`

```sh
./build/lub samples/01_triangle/01_triangle.hxml
```

`samples/01_triangle/Triangle01.hx` を編集して保存すると、走っている game に即反映される。

#### sample の書き方

`-cp samples/<name> / -lib lub / -main <ClassName>` の 3 行 hxml + Haxe class 1 個が最小構成。

```haxe
import lub.Lub;
import lub.Gfx;

class Triangle01 {
  public static function main() {}
  public static function onInit() { Lub.config({ backend: "sokol" }); }
  public static function onFrame() {
    Gfx.beginPass({ target: Gfx.mainTex, clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0]) });
    Gfx.endPass();
  }
}
```

extern は責務別に 5 class:
- `lub.Lub` — `config()`
- `lub.Gfx` — 描画 / GPU / 定数
- `lub.Input` — `keyDown()`
- `lub.Io` — `samples/lub_io.lua` の cached loader (`loadText`/`loadPng` 等)
- `lub.Sys` — 低 level primitive (普段不要、Haxe stdlib `Sys` を import するときは衝突に注意)

#### 注意点

- **配列リテラル**: `Gfx.beginPass({ clear_color: [...] })` のように Haxe array をそのまま渡すと 0-indexed の Lua table になり lub C 側 (1-indexed 期待) と噛み合わない。`lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])` で明示変換する。named field の anonymous structure (`{ target: x }`) は対象外。
- **環境変数読み出し**: `Sys.getEnv(...)` は使えない (Haxe stdlib が `luv` を require するため)。代わりに `lua.Os.getenv(...)` を使う。

Linux ヘッドレス (Mesa lavapipe = CPU Vulkan):

```sh
# 事前: sudo pacman -S vulkan-swrast (Arch) / sudo apt install mesa-vulkan-drivers (Debian)
scripts/run-headless.sh samples/01_triangle/01_triangle.hxml
```

`scripts/run-headless.sh` は `VK_ICD_FILENAMES` で lavapipe ICD を強制し、
`DISPLAY` / `WAYLAND_DISPLAY` が無ければ自動で `xvfb-run` でラップする。
これにより CI / SSH / コンテナ環境でも native sample を走らせられる
(Mesa lavapipe / AMD radv 双方で動作)。
Windows 用のヘッドレス wrapper は無く、実 GPU で動かす前提。

スクリーンショット capture (PNG 出力):

```sh
# 30 フレーム描画後にキャプチャして即終了
scripts/run-headless.sh samples/01_triangle/01_triangle.hxml --capture out.png --capture-frame 30
```

実 GPU でも `--capture` フラグはそのまま使える。Lua 側からも `capture("path.png")`
でスケジュール可能 (次フレームで実行)。BGRA8/RGBA8 のスワップチェインから RGBA に
swizzle して `stb_image_write` で PNG 出力する。

### Golden image diff (回帰テスト)

```sh
scripts/run-golden.sh             # 全 sample × 両 backend を tests/golden と cmp
scripts/run-golden.sh --update    # golden 画像を再生成 (描画意図的変更時)
scripts/run-golden.sh --sample 01_triangle --backend sokol
```

lavapipe + xvfb 環境では capture が確定的なので `cmp -s` で完全一致判定する。
実 GPU でのドリフトは想定範囲外 (tolerance 比較は別途)。

## Backend 切替

lub は内部に 2 つの GPU backend を持つ:

- `sokol` (default) — sokol_gfx (Vulkan)
- `sdlgpu` — SDL3 GPU API (現在 Vulkan で実装、将来 Metal / D3D12 にも展開可能)

切替は Lua の `on_init` 内で `config({ backend = "sdlgpu" })` を呼ぶ。
サンプルでは `arg[1]` または環境変数 `LUB_BACKEND` を見るパターン:

```lua
function on_init()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end
```

```sh
# default = sokol
./build/lub samples/01_triangle/01_triangle.hxml

# SDL3 GPU 経路
LUB_BACKEND=sdlgpu ./build/lub samples/01_triangle/01_triangle.hxml

# headless でも同じ
LUB_BACKEND=sdlgpu scripts/run-headless.sh ./build/lub samples/01_triangle/01_triangle.hxml
```

どちらの backend でも同一 Lua API で sample と capture が動く。
lavapipe + xvfb 環境では、両 backend の capture PNG は **byte-identical** になる。

## Live edit (file watching)

サンプルは `samples/<name>/data/` 配下の外部ファイルから shader / 頂点データ / テクスチャを
読み込む。起動中にファイルを編集して保存すると次フレームから反映される。

仕組み:

- 各サンプル冒頭で `lub_io` を `require` し、`load_text` /
  `load_floats` / `load_png` を経由してリソースを取得する。
- helper は `path → {mtime, content_hash}` のキャッシュを持ち、毎フレームの
  `stat()` 1 回だけで「変化なし」を判定する。mtime 違い時のみ再読み込みして
  FNV-1a 64 ハッシュを取り、それを `version` として `use_*` に渡す。
- C 側は `version` 違いで in-place update (buffer / texture) または recompile
  (shader) を実施。shader recompile 時は旧 shader を参照する pipeline cache
  entry を sweep してリークを防ぐ。
- shader compile error 時は旧 shader を維持してログを出すのみで、クラッシュせず
  エディタで修正→保存すれば復帰する (初回 compile 失敗だけは loud に止める)。

例: `samples/01_triangle/data/01_triangle.fs.slang` の出力色をエディタで書き換えて保存すると、
起動中の `samples/01_triangle/01_triangle.hxml` の三角形の色が即座に変わる。
PNG を別画像で上書きすればテクスチャも、`*.verts.lua` を編集すれば頂点も同様。

## WASM playground (web)

ブラウザ上で動く Vite + CodeMirror ベースの playground を `web/` 配下に同梱。
sokol-gfx の WGPU backend を target に WASM へクロスコンパイルしたバイナリを iframe
で読み込み、左ペインのエディタで `.slang` / `.lua` を編集すると 300ms debounce で右ペインの
プレイヤーに同期される (`samples/<name>/data/*` の mtime/hash hot-reload 経路を再利用)。
shader compile は [slang-wasm](https://github.com/shader-slang/slang/releases) を vendor。

### Build

```sh
# 1. WASM バイナリを生成 (Linux/macOS — emsdk が必要)
source ~/emsdk/emsdk_env.sh             # emcc / emcmake を PATH に
emcmake cmake -S . -B build/wasm        # WGPU + emdawnwebgpu port が configure される
cmake --build build/wasm -j             # lub.{js,wasm,data} が生成

# 2. JS 側の依存と slang-wasm を取得
cd web
npm install                             # postinstall で web/scripts/fetch-slang-wasm.sh が
                                        # web/public/slang/ に slang-wasm.{js,wasm} を取得
npm run dev                             # http://localhost:5173/ で dev server 起動
npm run verify                          # 別端末: playwright + swiftshader で headless 検証
npm run build                           # web/dist/ に production bundle 生成
```

### アーキテクチャ

```
            parent (index.html / main.ts)              iframe (player.html / player.ts)
            ┌────────────────────────────┐             ┌──────────────────────────────────┐
            │ CodeMirror editor          │   setFiles  │ slang-bridge.ts                  │
            │   path -> content table    │  ────────▶  │   window.slangCompile() を export │
            │ sample dropdown / restart  │  syncFiles  │ WebGPU device 取得 → preinit     │
            │ debounce 300ms             │  ────────▶  │ lub.js (Emscripten module)     │
            └────────────────────────────┘  ◀─player──│   ↑ EM_ASYNC_JS bridge            │
                                            Ready/log │   ↑ FS.writeFile で MEMFS overlay │
                                                       │ sokol_gfx (WGPU) — canvas へ描画 │
                                                       └──────────────────────────────────┘
```

postMessage プロトコル:

- `parent → iframe`: `setFiles {files, entry}` (初回ブート時 1 回), `syncFiles {files}` (編集毎)
- `iframe → parent`: `playerReady` (ハンドシェイク), `log {level, msg}` (console relay)

shader compile は C 側 (`src/shader.cpp`) の `EM_ASYNC_JS` shim から
`window.slangCompile(src, entry, stage)` を呼び、`{wgsl, reflectJson}` を `'\x01'`
区切りで pack して戻す。エラーは `'\x02' + msg` 形式で Slang diagnostic として
err_buf に届く。

MEMFS sync: iframe 側で Emscripten の data file package (`lub.data`) をマウント
した直後に `FS.writeFile` でエディタ内容を上書きする (`player.ts` の `postPreload`
hook)。実行中の `syncFiles` も同じ `FS.writeFile` 経路で、C 側は次フレームの
`stat()` で mtime 違いを検知して reload する (native と同じ hot-reload コード)。

### サンプル対応状況 (web)

web playground の対象 sample は `web/` 側の sample list と verify script で管理する。

### Browser requirements

- WebGPU が利用可能なブラウザ:
  - **Chrome / Edge** (primary、137+) — 既定で WebGPU 有効。
  - **Firefox Nightly** — `dom.webgpu.enabled` を `about:config` で有効化。
- ローカル開発: Vite dev server が emdawnwebgpu に必要な CORS/MIME 設定を済ませる。
- production bundle (`npm run build`) は `web/dist/` 配下、`/wasm/`,
  `/slang/` への絶対パス前提なので site root に置く。

### Headless verification

`npm run verify` は playwright + chromium (swiftshader Vulkan) で:

1. sample 01 の初期描画 (orange triangle on dark blue clear) を pixel bucket で確認
2. fragment shader を編集 → green になる
3. lua の clear_color を編集 → 背景が red になる
4. verts を縮小編集 → green pixel 数が減る
5. 登録済み sample を順に切替 → 各サンプルの非黒描画を確認

スクリーンショットは `/tmp/lub-verify/` に出力される。CI 利用時は dev server を
別ジョブで立ち上げてから `LUB_URL=http://...` を指定すること。

### Live edit caveats

- shader に syntax error がある場合: 既存の shader を維持して Slang diagnostic を
  iframe log に流すのみ (next save で復帰)。初回 compile 失敗のみ load を止める。
- 300ms debounce: 入力後 300ms 静止してから `syncFiles` を送る。連打中は更新されない。
- サンプル切替時に dirty な編集があると `confirm()` で警告する。

### Known limitations

- **capture / golden image は native のみ**。WebGPU の `mapAsync` 経路で readback
  は可能だが capture API が同期 sync なので未実装 (`backend_sokol.c` の `sk_capture`
  が `false` を返す)。
- **sdlgpu backend は web 非対応**。WGPU backend の sokol のみ。

## ライセンス

未定。
