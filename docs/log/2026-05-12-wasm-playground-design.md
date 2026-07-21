# 2026-05-12 — WASM Playground 設計

> 記録: 2026-05-12 時点の設計(workflow 産物)。現状は [../../web/README.md](../../web/README.md) を参照。

## ゴール

lub の sample 01〜07 を **ブラウザ上で走らせるデモページ** を作る。同ページ内に
Lua/Slang のライブエディタを併設し、編集の度に画面へ即時反映 (debounce auto-sync)
される PoC とする。lub3d (`../lub3d`) の playground 構成と整合させる。

## 非ゴール

- GitHub Actions / GitHub Pages の自動デプロイ — 別タスク。
- Gist 等での共有 URL、Docs パネル、Resolution セレクタといった lub3d 周辺機能 —
  価値はあるが PoC スコープ外。
- Capture / golden image diff の web 版 — 不要。
- `backend_sdlgpu` 経路の web 対応 — drop。
- Native 側の追加リファクタ (例: native での mtime-poll を entry Lua にも拡張する
  作業) — module 化と hotswap 機構は両 OS で共有するが、ユースケースの主体は web。

## 採用スタック

| 層 | 採用 | 理由 |
|----|------|------|
| ビルドツールチェーン | Emscripten | sokol_gfx の WGPU backend が emscripten 前提。lub3d 実績あり。 |
| GPU API | WebGPU (`SOKOL_WGPU`) | Slang の WGSL target と相性、sample 07 (compute) も動く。 |
| Shader compiler (web) | `@shader-slang/slang-wasm` (npm) | Slang 公式 WASM 配布。playground でも使われている。 |
| Frontend 構築 | Vite + TypeScript | lub3d と同構成。`/samples/` を dev server から serve、build 時に dist へコピー。 |
| エディタ | CodeMirror 6 (`@codemirror/legacy-modes/mode/lua` + HLSL モード流用) | 軽量、lub3d と同じ、Slang は HLSL ベース文法で十分。 |
| Player の隔離 | `<iframe src="/player.html">` | lub3d 同型。サンプル切替 / Restart で破棄、ファイル sync で再利用。 |

## ディレクトリ構成

```
lub/
├── CMakeLists.txt                  -- 既存 + EMSCRIPTEN 分岐
├── CMakePresets.json               -- 新規 (wasm-debug / wasm-release)
├── src/
│   ├── ... 既存 ...
│   ├── shader.cpp                   -- EMSCRIPTEN 分岐で slang-wasm bridge 呼び出し
│   ├── app.c                        -- entry Lua module table への callback dispatch + mtime-poll hotswap
│   └── lua_api.c                    -- 既存
├── third_party/lume/
│   └── lume.lua                     -- 新規 vendor (rxi/lume, MIT)
├── samples/
│   ├── boot.lua                     -- 新規 (lume require + module 呼び出し)
│   ├── lub_io.lua                    -- 既存
│   ├── 01_triangle.lua              -- module table 返却に書き換え
│   ├── 02_vertex_color.lua          -- 同上
│   ├── ... 03..07 ...               -- 同上
│   └── data/...                     -- 既存
├── web/
│   ├── index.html                   -- parent: editor + sample selector + log
│   ├── public/player.html           -- iframe: canvas + postMessage 受信
│   ├── playground/
│   │   ├── main.ts                  -- editor 配線 + iframe orchestration
│   │   ├── editor.ts                -- CodeMirror tab 管理 + dirty 検出 + debounce
│   │   ├── samples.ts               -- /samples/ fetch + Lua scan で tab 構成
│   │   └── slang-bridge.ts          -- slang-wasm session init + WGSL compile expose
│   ├── package.json
│   ├── vite.config.ts
│   └── tsconfig.json
└── docs/log/2026-05-12-wasm-playground-design.md  -- 本書
```

## アーキテクチャ

### 高レベル

```
┌─ Browser ─────────────────────────────────────────────────────┐
│                                                               │
│  parent (index.html)                                          │
│   ├ CodeMirror 6 (multi-tab)                                  │
│   ├ Sample selector / Restart / Log panel                     │
│   ├ Editor state: Map<path, {content, dirty, initial}>        │
│   └ debounce 300ms → postMessage to iframe                    │
│                                                               │
│  iframe (player.html)                                         │
│   ├ <canvas>                                                  │
│   ├ slang-wasm session (window.slangCompile)                  │
│   ├ Module.FS.writeFile on syncFiles message                  │
│   └ lub.{js,wasm}                                           │
│        ├ Lua 5.5 + samples (module 化)                         │
│        ├ sokol_gfx (SOKOL_WGPU)                               │
│        ├ shader.cpp → EM_ASYNC_JS(slangCompile) → WGSL        │
│        └ app.c frame loop: mtime-poll → hotswap / use_*        │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

### ファイル仮想化 (S2: parent 正本 / Run-or-edit 時 push)

不変条件:

1. **エディタ state が editable file の正本**。CodeMirror docs に保持。
2. **MEMFS は iframe 寿命と同じ ephemeral storage**。iframe 破棄でゼロから。
3. **同期は片方向のみ** (parent → MEMFS)。WASM が `FS.writeFile` しても parent は見ない。

同期点:

| イベント | 方向 | 内容 |
|----------|------|------|
| ページロード | server → editor | `/samples/<entry>.lua` fetch → Lua scan で referenced を fetch → tab 構築 |
| 初回 boot | editor → MEMFS | 新 iframe → `setFiles` postMessage → `FS.writeFile` → WASM script load |
| 編集 (debounce 300ms) | editor → MEMFS | dirty tab を `syncFiles` postMessage → `FS.writeFile` 上書き |
| サンプル切替 | server → editor → 新 MEMFS | iframe 破棄 → 新 sample fetch → tab 再構築 → 新 iframe boot |
| Restart | editor → 新 MEMFS | iframe 破棄 → 現タブのままで再 boot |
| frame ループ | C 内部 | MEMFS mtime-poll → version bump → recompile / hotswap |
| WASM → editor | (なし) | 不変条件 (3) |

### Lua entry module 化と hotswap

現行サンプルは global 関数 (`on_init`/`on_frame`/...) を define するが、これを **module
table 返却型** に書き換える:

```lua
local M = {}
function M.on_init() ... end
function M.on_frame() ... end
return M
```

#### Module 名と path 解決

- ファイル名: `samples/01_triangle.lua`
- Module 名: `"01_triangle"` (digit 始まりだが `require` は string→path 置換だけなので
  問題ない。Lua の識別子としては読まないため)
- `package.path` 設定 (`samples/boot.lua` 冒頭で):
  ```lua
  package.path = "/lume/?.lua;samples/?.lua;samples/?/init.lua;" .. package.path
  ```
- これで `require("01_triangle")` が `samples/01_triangle.lua` に解決し、
  `package.loaded["01_triangle"]` に table が入る。`lume.hotswap("01_triangle")` は
  `package.loaded` のエントリと file を見て swap する。

C 側 (`app.c`):

- 起動時に `samples/boot.lua` を `loadfile` + `lua_pcall` で実行。`boot.lua` に
  `lua_pushstring` で entry module 名 (`"01_triangle"` 等) を渡し、`boot.lua` 内で
  `require` して返す table を Lua registry の固定 ref に保存。
- on_init は **boot 直後に 1 度だけ呼ぶ**。hotswap 後は on_init を再呼出しない (state を
  保持するのが hotswap の主目的)。サンプル切替や Restart 時の cold boot で再 init される。
- 各 frame 先頭で `check_entry_hotswap(L)` → entry file の mtime が変化していたら
  `lume.hotswap(module_name)` を呼んで registry の table を更新。
- callback 呼出は `lua_rawgeti(L, LUA_REGISTRYINDEX, entry_module_ref)` した table の
  field を `lua_getfield` して呼ぶ。

```c
// app.c (擬似)
static int64_t entry_mtime_cache;
static int entry_module_ref = LUA_NOREF;
static const char *entry_module_name; // 例: "01_triangle"
static const char *entry_path;        // 例: "samples/01_triangle.lua"

static void check_entry_hotswap(lua_State *L) {
    int64_t now = file_mtime_ns(entry_path);
    if (now == entry_mtime_cache) return;
    entry_mtime_cache = now;

    lua_getglobal(L, "lume");
    lua_getfield(L, -1, "hotswap");
    lua_pushstring(L, entry_module_name);
    if (lua_pcall(L, 1, 1, 0) != LUA_OK) {
        log_warn("hotswap failed: %s", lua_tostring(L, -1));
        lua_pop(L, 2);
        return;
    }
    lua_rawseti(L, LUA_REGISTRYINDEX, entry_module_ref);
    lua_pop(L, 1); // lume
}

static void call_cb(lua_State *L, const char *name) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, entry_module_ref);
    lua_getfield(L, -1, name);
    if (lua_isfunction(L, -1)) {
        lua_pushvalue(L, -2); // self
        if (lua_pcall(L, 1, 0, 0) != LUA_OK) {
            log_warn("%s error: %s", name, lua_tostring(L, -1));
            lua_pop(L, 1);
        }
    } else {
        lua_pop(L, 1);
    }
    lua_pop(L, 1); // module
}
```

`samples/boot.lua`:

```lua
package.path = "/lume/?.lua;samples/?.lua;samples/?/init.lua;" .. package.path
local lume = require("lume")
_G.lume = lume                                  -- C 側から lua_getglobal("lume") で取れる
local entry_name = ...                          -- C から push される、例: "01_triangle"
local mod = require(entry_name)
return mod
```

C 側は `boot.lua` の返り値 (= sample module table) を `luaL_ref` で
`LUA_REGISTRYINDEX` に保存し、各 frame で `lua_rawgeti` して使う。

native も web も同コードパス。native では既存 mtime 検出が file system に対して、
web では MEMFS に対して同じく動く。

### Shader compile bridge (web only)

`src/shader.cpp` の中で:

```cpp
#ifdef __EMSCRIPTEN__
extern "C" int lub_slang_compile(
    const char* src, const char* entry, int stage,
    char** out_wgsl, size_t* out_wgsl_len,
    char** out_reflect_json);
// 実装は EM_ASYNC_JS で window.slangCompile(...) を呼ぶ
#else
// 既存の libslang in-process パス
#endif
```

`web/playground/slang-bridge.ts` (parent and/or player) で:

- `@shader-slang/slang-wasm` を import、`module.createGlobalSession()` を 1 度だけ実行
- session を `window.slangCompile = (src, entry, stage) => ({ wgsl, reflectJson })` で expose
- 失敗時は `{ error: "..." }` を返し、C 側は旧 shader を維持してログを出す (既存挙動と同じ)

reflection JSON は Slang の `--reflection-json` 出力フォーマットを使う。SPIR-V 経由の
reflection との抽象化のため、`shader.cpp` 内に `ShaderReflection` 中間 struct を切り出し、
SPIRV-Cross reflection と Slang reflection の両方から詰める helper を 2 経路用意する。

ASYNCIFY が必要 (`-sASYNCIFY`)。shader compile は edit / boot 時にしか走らないので
ASYNCIFY コストは無視できる。

### Backend 分岐 (CMake)

`backend_sokol.c`:

- 既存の Vulkan 直叩きパスを `#ifndef __EMSCRIPTEN__` で囲う
- emscripten では sokol_gfx 自体が WGPU で必要なリソースを管理するので、直叩き
  layer は何もしない (sokol の `sg_setup` だけで足りる)

`sokol_impl.c`:

- 既存: `SOKOL_VULKAN` を define
- emscripten: `SOKOL_WGPU` を define

`backend_sdlgpu.c` / `capture.c`:

- `if(NOT EMSCRIPTEN)` で sources から除外

`shader.cpp`:

- 既存の libslang リンクを `if(NOT EMSCRIPTEN)` に
- emscripten ではコード内分岐で slang-wasm bridge を使う (実装は↑)

### CMake 構成

`CMakeLists.txt` に EMSCRIPTEN 分岐を入れる:

```cmake
if(EMSCRIPTEN)
    set(LUB_WASM ON)
endif()

if(NOT LUB_WASM)
    find_package(Vulkan REQUIRED)
endif()

# Slang prebuilt: WASM では skip (slang-wasm は JS 側に npm install されたもの)
if(NOT LUB_WASM)
    # 既存の Slang prebuilt fetch
endif()

set(LUB_SOURCES
    src/main.c src/app.c src/sokol_impl.c src/lua_api.c
    src/enums_lua.c src/pass.c src/resources.c src/shader.cpp
    src/pipeline.c src/stb_impl.c src/backend_sokol.c
)
if(NOT LUB_WASM)
    list(APPEND LUB_SOURCES src/capture.c src/backend_sdlgpu.c)
endif()

add_executable(lub ${LUB_SOURCES})

if(LUB_WASM)
    target_compile_definitions(lub PRIVATE SOKOL_WGPU)
    set_target_properties(lub PROPERTIES SUFFIX ".js")
    target_link_options(lub PRIVATE
        -sASYNCIFY
        -sUSE_WEBGPU=1
        -sALLOW_MEMORY_GROWTH=1
        -sASSERTIONS=1
        -sSTACK_SIZE=1048576
        -sEXPORTED_RUNTIME_METHODS=['FS','ccall','UTF8ToString']
        -sMODULARIZE=0
        --preload-file samples
        --preload-file third_party/lume@/lume
    )
else()
    target_compile_definitions(lub PRIVATE SOKOL_VULKAN)
    target_link_libraries(lub PRIVATE slang Vulkan::Vulkan)
endif()
```

`CMakePresets.json` (新規):

```json
{
  "version": 6,
  "configurePresets": [
    { "name": "wasm-debug", "binaryDir": "${sourceDir}/build/wasm",
      "generator": "Ninja",
      "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug" },
      "description": "emcmake cmake --preset wasm-debug" },
    { "name": "wasm-release", "binaryDir": "${sourceDir}/build/wasm",
      "generator": "Ninja",
      "cacheVariables": { "CMAKE_BUILD_TYPE": "Release" } }
  ],
  "buildPresets": [
    { "name": "wasm-debug", "configurePreset": "wasm-debug" },
    { "name": "wasm-release", "configurePreset": "wasm-release" }
  ]
}
```

### Frontend (web/)

`web/playground/main.ts` の主要処理:

```ts
const samples = ['01_triangle', '02_vertex_color', /* ... */ '07_compute']
let currentSample = '01_triangle'
let editorFiles: Map<string, EditorFile> = new Map()
let playerIframe: HTMLIFrameElement | null = null
let syncTimer: number | null = null

async function loadSample(name: string) {
    const luaPath = `${name}.lua`
    const luaText = await fetch(`/samples/${luaPath}`).then(r => r.text())
    const referenced = scanLuaReferences(luaText) // load_text / load_floats のみ抽出
    const files = new Map([[luaPath, { content: luaText, dirty: false, initial: luaText }]])
    for (const ref of referenced) {
        const txt = await fetch(`/samples/${ref}`).then(r => r.ok ? r.text() : null)
        if (txt !== null) files.set(ref, { content: txt, dirty: false, initial: txt })
    }
    editorFiles = files
    rebuildTabs(files)
    await restartPlayer(name)
}

function onEditorChange(path: string, content: string) {
    const f = editorFiles.get(path)
    if (!f) return
    f.content = content
    f.dirty = f.content !== f.initial
    if (syncTimer) clearTimeout(syncTimer)
    syncTimer = setTimeout(() => {
        const dirty: Record<string, string> = {}
        for (const [p, file] of editorFiles)
            if (file.dirty) dirty[p] = file.content
        playerIframe?.contentWindow?.postMessage(
            { type: 'syncFiles', files: dirty }, '*')
    }, 300) as unknown as number
}

async function restartPlayer(sample: string) {
    if (playerIframe) playerIframe.remove()
    playerIframe = document.createElement('iframe')
    playerIframe.src = '/player.html'
    document.querySelector('#player-mount')!.appendChild(playerIframe)
    await waitForMessage('playerReady')
    const all: Record<string, string> = {}
    for (const [p, file] of editorFiles) all[p] = file.content
    playerIframe.contentWindow!.postMessage(
        { type: 'setFiles', files: all, entry: `samples.${sample}` }, '*')
}
```

`web/public/player.html` (lub3d 同型):

```html
<canvas id="canvas" tabindex="0"></canvas>
<script type="module">
  import * as slang from '/node_modules/@shader-slang/slang-wasm/dist/index.js'
  const slangModule = await slang.default()
  const slangSession = slangModule.createGlobalSession()
  window.slangCompile = (src, entry, stage) => {
      // Slang API で WGSL + reflection JSON 生成、{wgsl, reflectJson} or {error} を返す
  }

  window.Module = {
      canvas: document.getElementById('canvas'),
      print: (t) => parent.postMessage({type:'log', msg: t, level:'log'}, '*'),
      printErr: (t) => parent.postMessage({type:'log', msg: t, level:'error'}, '*'),
      onRuntimeInitialized: () => {
          // 起動完了
      },
      arguments: [],  // C main が entry module 名を必要なら別経路で読む
  }

  let pendingFiles = null
  let pendingEntry = null

  window.addEventListener('message', (e) => {
      if (e.data.type === 'setFiles') {
          pendingFiles = e.data.files
          pendingEntry = e.data.entry
          // FS が利用可能になるのは onRuntimeInitialized 後だが、Module.preRun に
          // FS.writeFile を仕込めば WASM 起動前に書ける
          window.Module.preRun = [() => {
              for (const [p, c] of Object.entries(pendingFiles))
                  writeFileEnsureDir('samples/' + p, c)
              window._lub_entry_module = pendingEntry
          }]
          const script = document.createElement('script')
          script.src = '/lub.js'
          document.body.appendChild(script)
      } else if (e.data.type === 'syncFiles') {
          for (const [p, c] of Object.entries(e.data.files))
              FS.writeFile('samples/' + p, c)
          // mtime-poll が拾うので追加 trigger なし
      }
  })

  parent.postMessage({type:'playerReady'}, '*')
</script>
```

`window._lub_entry_module` を `app.c` から `EM_JS` で取得 (例:
`const char* lub_get_entry_module(void)`)。native では argv から渡す。

### samples/ の書き換え

各 sample を module table 返却型に統一:

```lua
-- 01_triangle.lua (例)
local lub_io = require("lub_io")  -- 既存 require pattern に変更 (今は dofile)
local M = {}

function M.on_init(self)
    -- 旧 on_init の中身
end

function M.on_frame(self)
    -- 旧 on_frame の中身
end

function M.on_event(self, ev) end
function M.on_quit(self) end

return M
```

`samples/lub_io.lua` も `local M = {}; function M.load_text(...) ...; return M` 形に変更。
これで `require("lub_io")` が動き、各サンプルから dofile を撤廃できる。

### lume の vendor

- `third_party/lume/lume.lua` に rxi/lume v2.3.0 を vendor (MIT、~300 行 1 file)
- preload で `--preload-file third_party/lume@/lume` → MEMFS の `/lume/lume.lua` にマッピング
- Lua の `package.path` に `/lume/?.lua` を追加 (boot 時 C 側で設定)

## エラーハンドリングと UI 状態

- **Shader compile error**: slang-wasm が `{error: msg}` を返す → C 側で旧 shader 維持 +
  log → log panel に表示。
- **Lua syntax error** (hotswap 中): `lume.hotswap` が pcall 失敗 → 旧 module 維持 + 警告。
  次の編集で mtime 変化が走ればまた試行される (loud 失敗にしない)。
- **WebGPU device lost**: log に表示、Restart ボタンで復帰。
- **Fatal Lua error** (initial boot 中): エラーログ出力後、iframe 内 WASM はそのままで
  画面真っ黒。Restart ボタンを案内。

## テストと検証

- **ローカル開発**:
  - `cmake --preset wasm-debug && cmake --build build/wasm`
  - `cd web && pnpm i && pnpm dev` で `vite` server 起動
  - Chromium (WebGPU 有効) で `http://localhost:5173`
- **手動チェックリスト**:
  1. 全 sample 01〜07 が初期状態で描画される
  2. Lua の数値を書き換える → 300ms 後に画面反映 (hotswap 動作)
  3. Slang の色を書き換える → 300ms 後に画面反映 (shader recompile)
  4. .verts.lua を書き換える → 形が変わる (load_floats + version)
  5. サンプル切替 → 新サンプルが起動する
  6. Lua に syntax error を書く → ログにエラー、画面はフリーズしない
  7. Restart ボタン → 同じサンプルが再起動する
- **自動テスト**: PoC では実施しない。golden image diff は native の既存仕組みを継続使用。

## オープン項目 (実装中に判断)

- `samples/boot.lua` で entry module 名を受け取る経路: native は argv、web は
  `EM_JS` で `window._lub_entry_module` を取りに行く。C 側で文字列を確定したら
  `boot.lua` を `loadfile` で読み込み、`lua_pushstring(L, module_name)` してから
  `lua_pcall(L, 1, 1, 0)` で実行する。boot.lua は受け取った名前で `require` する。
- Slang reflection JSON のフォーマット詳細: Slang API ドキュメントを実装段階で確認、
  SPIRV-Cross reflection との中間 struct マッピングを実装時にコミット。
- emscripten の SDL3 サポート確認: SDL3 3.2.x が emscripten target を持つことは確認
  済だが、main_callbacks / WebGPU canvas 連携でハマる可能性あり。問題があれば
  `sapp` (sokol_app) ベースに乗り換える代替を検討する。本書では SDL3 継続前提で記述。
- 解像度: PoC では 480x360 固定。lub3d の resolution selector は scope 外。
