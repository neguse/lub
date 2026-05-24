# WASM Playground Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** lub の sample 01〜07 をブラウザで走らせ、編集に応じて debounce auto-sync で即時反映する WebGPU playground を作る。

**Architecture:** Emscripten で sokol_gfx の `SOKOL_WGPU` backend をビルド、Slang は `@shader-slang/slang-wasm` (JS) 経由で `EM_ASYNC_JS` ブリッジ。フロントは Vite + TypeScript + CodeMirror 6 で multi-tab editor、player は iframe (`/player.html`) に隔離し parent → MEMFS の片方向 sync で生かす。entry Lua は lume.hotswap で mtime-poll hotswap、shader/verts/texture は既存 version 機構が拾う。

**Tech Stack:** Emscripten, SDL3 (emscripten port), sokol_gfx (`SOKOL_WGPU`), Lua 5.5, lume.lua, `@shader-slang/slang-wasm`, Vite, TypeScript, CodeMirror 6.

**Spec:** [`docs/superpowers/specs/2026-05-12-wasm-playground-design.md`](../specs/2026-05-12-wasm-playground-design.md)

---

## ファイル構成

### 新規

| Path | Responsibility |
|------|----------------|
| `CMakePresets.json` | `wasm-debug` / `wasm-release` プリセット |
| `third_party/lume/lume.lua` | rxi/lume v2.3.0 vendor |
| `samples/boot.lua` | package.path 設定 + 引数 module を require |
| `web/package.json` | Vite + TS + CodeMirror |
| `web/tsconfig.json` | TS 設定 |
| `web/vite.config.ts` | `/samples/` を dev/build で配信 |
| `web/index.html` | parent: editor + sample selector + log + iframe |
| `web/public/player.html` | iframe: canvas + postMessage 受信 + WASM script load |
| `web/playground/main.ts` | orchestrator (editor ↔ iframe ↔ sample list) |
| `web/playground/editor.ts` | CodeMirror multi-tab + dirty + debounce |
| `web/playground/samples.ts` | `/samples/` fetch + Lua scan で tab 構築 |
| `web/playground/slang-bridge.ts` | slang-wasm session init + WGSL compile expose |
| `docs/superpowers/plans/2026-05-12-wasm-playground.md` | 本プラン |

### 既存改修

| Path | 内容 |
|------|------|
| `CMakeLists.txt` | EMSCRIPTEN 分岐 (sources, defines, link options) |
| `src/lua_api.c` | callback dispatch を module table 経由に変更、boot.lua 経由の init |
| `src/lua_api.h` | (型変更なし) |
| `src/app.c` | entry .lua の mtime-poll + lume.hotswap |
| `src/app.h` | App に entry_path / entry_module_name / mtime_cache / module_ref |
| `src/main.c` | argv → entry name 解決、native では `-` をモジュール名として渡せるよう調整 |
| `src/shader.cpp` | EMSCRIPTEN 分岐で slang-wasm bridge を呼ぶ、reflection 共通化 |
| `src/backend_sokol.c` | Vulkan 直叩きを `#ifndef __EMSCRIPTEN__` で囲う |
| `src/sokol_impl.c` | (define は CMake から付与、コード変更なし) |
| `samples/01_triangle.lua` 〜 `07_compute.lua` | module table 返却型に統一 |
| `samples/lub_io.lua` | `dofile` から `require` パターンへ (既に `return M` なので呼び出し側変更だけ) |
| `README.md` | wasm build と playground 起動手順を追記 |

---

## Phase 0: Sample module 化 (native baseline)

> hotswap の前提として、各サンプルを module table 返却型に揃え、C 側の dispatch を `lua_getglobal` から module table の field 呼出に変える。Web 作業に入る前にここを native で動く状態に確定する。

### Task 0.1: `samples/lub_io.lua` を require で読めるように `package.path` 経由化

**Files:**
- Modify: `samples/lub_io.lua:1-86` (中身そのまま、ファイル末尾の `return M` を維持)
- 各 `samples/0?_*.lua` の `dofile("samples/lub_io.lua")` を `require("lub_io")` に変更 (Task 0.2 と 0.3 で行う)

`lub_io.lua` は既に module pattern なので変更不要。`package.path` の整備は Task 0.4 で boot.lua 経由で行う。

- [ ] **Step 1: package.path 整備が完了するまで一時的に dofile を維持していい確認** — `samples/lub_io.lua` 自体は変更不要。

### Task 0.2: 全 sample を module table 返却型に書き換え

**Files:**
- Modify: `samples/01_triangle.lua`
- Modify: `samples/02_vertex_color.lua`
- Modify: `samples/03_texture.lua`
- Modify: `samples/04_mvp.lua`
- Modify: `samples/05_postprocess.lua`
- Modify: `samples/06_deferred.lua`
- Modify: `samples/07_compute.lua`

各サンプルの典型変換 (`01_triangle.lua` の例):

```lua
-- 旧
local lub_io = dofile("samples/lub_io.lua")
function on_init() ... end
function on_frame() ... end

-- 新
local lub_io = require("lub_io")
local M = {}
function M.on_init(self) ... end
function M.on_frame(self) ... end
function M.on_event(self, e) end
function M.on_quit(self) end
return M
```

- [ ] **Step 1: `samples/01_triangle.lua` を module pattern に書き換える**

```lua
-- samples/01_triangle.lua
local lub_io = require("lub_io")
local M = {}

function M.on_init(self)
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.on_event(self, e) end
function M.on_quit(self) end

function M.on_frame(self)
    local vs, vsv = lub_io.load_text("samples/data/01_triangle.vs.slang")
    local fs, fsv = lub_io.load_text("samples/data/01_triangle.fs.slang")
    local verts, vv = lub_io.load_floats("samples/data/01_triangle.verts.lua")
    if not vs or not fs or not verts then return end
    local s = use_shader("tri_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("tri_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
    end_pass()
end

return M
```

- [ ] **Step 2: 同じパターンで `02_vertex_color.lua` 〜 `07_compute.lua` を書き換え**

各ファイルの旧 global 関数 (`on_init` / `on_frame` / `on_event` / `on_quit`) を `M.on_init(self)` 等に詰め替え、最初の `dofile(...)` を `require("lub_io")` にする。最後に `return M` を追加。中身の動作ロジックは変えない。

- [ ] **Step 3: ビルドはまだ通らない (C 側がまだ global を見ているため)。コミットは Task 0.3 と一緒に行う** ので一旦コミットしない。

### Task 0.3: C 側 callback dispatch を module table 経由に変更

**Files:**
- Modify: `src/lua_api.c:904-945`
- Modify: `src/lua_api.h` (新 API がある場合 — 今回はシグネチャ維持なので変更なし想定)
- Modify: `src/app.h` (App 内に `entry_module_ref` を追加 — Task 1 でも触るので、ここでは LuaCtx に置く)

LuaCtx に module ref を生やし、`call_global_if_present` を `call_module_field` に置換:

- [ ] **Step 1: `src/lua_api.c` の `LuaCtx` (定義箇所を確認、構造体に `int module_ref` を追加)**

```c
// lua_api.c の LuaCtx 構造体定義箇所 (現状 L のみ持っていると思われる) に
// int module_ref; を追加。LUA_NOREF で初期化。
```

- [ ] **Step 2: `lua_ctx_init` を boot.lua 経由に変更**

Task 0.4 で `samples/boot.lua` を追加するので、ここでは「`script_path` を module 名として扱い、boot.lua を実行して結果を ref に保存」する形に変える:

```c
bool lua_ctx_init(LuaCtx *ctx, const char *entry_module_name, App *app) {
    g_app_for_lua = app;
    ctx->L = luaL_newstate();
    if (!ctx->L) { SDL_Log("luaL_newstate failed"); return false; }
    luaL_openlibs(ctx->L);
    lua_api_register(ctx->L);

    // boot.lua をロード、entry module 名を引数で渡す
    if (luaL_loadfile(ctx->L, "samples/boot.lua") != LUA_OK) {
        SDL_Log("boot.lua load error: %s", lua_tostring(ctx->L, -1));
        lua_close(ctx->L); ctx->L = NULL; return false;
    }
    lua_pushstring(ctx->L, entry_module_name);
    if (lua_pcall(ctx->L, 1, 1, 0) != LUA_OK) {
        SDL_Log("boot.lua run error: %s", lua_tostring(ctx->L, -1));
        lua_close(ctx->L); ctx->L = NULL; return false;
    }
    if (!lua_istable(ctx->L, -1)) {
        SDL_Log("boot.lua did not return a module table");
        lua_close(ctx->L); ctx->L = NULL; return false;
    }
    ctx->module_ref = luaL_ref(ctx->L, LUA_REGISTRYINDEX);
    return true;
}
```

- [ ] **Step 3: dispatch helper を module ref ベースに置換**

```c
static void call_module_field(LuaCtx *ctx, const char *name, int nargs) {
    lua_State *L = ctx->L;
    lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->module_ref);
    if (!lua_istable(L, -1)) {
        lua_pop(L, 1 + nargs);
        return;
    }
    lua_getfield(L, -1, name);
    if (!lua_isfunction(L, -1)) {
        lua_pop(L, 2 + nargs);
        return;
    }
    // arg order: [module, fn, ...args] -> swap so fn is first then self
    if (nargs > 0) {
        // stack: [..., args..., module, fn]
        // need: [..., module, args..., fn]? いや、想定呼び出しは M.on_event(self, ev)
        // 簡単化: self は渡さず、ev だけ渡す形 (Lua 側は M.on_frame(_) で _ を無視)
        // ここでは self を渡さない設計に統一する。
    }
    lua_remove(L, -2); // module を消す
    if (nargs > 0) {
        lua_insert(L, -1 - nargs); // fn を args の前に移動
    }
    if (lua_pcall(L, nargs, 0, 0) != LUA_OK) {
        SDL_Log("lua error in %s: %s", name, lua_tostring(L, -1));
        lua_pop(L, 1);
    }
}

void lua_ctx_call_init(LuaCtx *ctx)  { if (!ctx->L) return; call_module_field(ctx, "on_init", 0); }
void lua_ctx_call_frame(LuaCtx *ctx) { if (!ctx->L) return; call_module_field(ctx, "on_frame", 0); }
void lua_ctx_call_quit(LuaCtx *ctx)  { if (!ctx->L) return; call_module_field(ctx, "on_quit", 0); }

void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e) {
    if (!ctx->L) return;
    push_event_table(ctx->L, e);
    call_module_field(ctx, "on_event", 1);
}
```

注: `self` を渡さない方針に統一する (サンプル側 `M.on_frame(self)` の `self` は無視される空 param)。これでスタック操作が単純化され、native でも web でも同じ呼び方になる。

- [ ] **Step 4: `lua_ctx_shutdown` で module_ref を unref**

```c
void lua_ctx_shutdown(LuaCtx *ctx) {
    if (ctx->L) {
        if (ctx->module_ref != LUA_NOREF) {
            luaL_unref(ctx->L, LUA_REGISTRYINDEX, ctx->module_ref);
            ctx->module_ref = LUA_NOREF;
        }
        lua_close(ctx->L);
    }
    ctx->L = NULL;
}
```

### Task 0.4: `samples/boot.lua` を追加

**Files:**
- Create: `samples/boot.lua`

- [ ] **Step 1: ファイルを作成**

```lua
-- samples/boot.lua
-- C 側から `lua_pushstring(L, entry_module_name)` で渡される。
local entry_name = ...

-- lume と samples/ を解決できるように package.path を拡張。
-- /lume/?.lua は WASM preload で MEMFS の /lume/lume.lua に展開される (native では
-- third_party/lume/?.lua から読む)。
package.path = "/lume/?.lua;third_party/lume/?.lua;samples/?.lua;samples/?/init.lua;" .. package.path

local ok, lume = pcall(require, "lume")
if not ok then
    error("boot.lua: failed to load lume: " .. tostring(lume))
end
_G.lume = lume

local mod = require(entry_name)
if type(mod) ~= "table" then
    error("boot.lua: module " .. tostring(entry_name) .. " did not return a table")
end
return mod
```

### Task 0.5: `src/main.c` の entry resolution を module 名に切替

**Files:**
- Modify: `src/main.c:32-34`

- [ ] **Step 1: argv 解釈を更新**

`argv[i]` が `.lua` で終わるパスでも、`samples/01_triangle.lua` 形式でも受けられるようにし、module 名 (`01_triangle`) を抽出して `lua_ctx_init` に渡す:

```c
// 旧:
//   if (!script) script = "samples/00_hello.lua";
//   lua_ctx_init(&g_app.lua, script, &g_app);
//
// 新:
const char *raw = script ? script : "01_triangle";
// "samples/01_triangle.lua" / "01_triangle.lua" / "01_triangle" のいずれも受け入れる
const char *base = strrchr(raw, '/');
base = base ? base + 1 : raw;
char modbuf[256];
size_t n = strlen(base);
if (n >= 4 && strcmp(base + n - 4, ".lua") == 0) n -= 4;
if (n >= sizeof(modbuf)) n = sizeof(modbuf) - 1;
memcpy(modbuf, base, n);
modbuf[n] = '\0';
if (!lua_ctx_init(&g_app.lua, modbuf, &g_app)) return SDL_APP_FAILURE;
```

### Task 0.6: lume を vendor

**Files:**
- Create: `third_party/lume/lume.lua`

- [ ] **Step 1: rxi/lume v2.3.0 をダウンロードして vendor**

```bash
mkdir -p third_party/lume
curl -fsSL https://raw.githubusercontent.com/rxi/lume/v2.3.0/lume.lua -o third_party/lume/lume.lua
```

ファイル冒頭の license/version コメントが残っていることを確認。

### Task 0.7: ビルドして全 sample を走らせる

- [ ] **Step 1: 既存 native build を走らせる**

```bash
cmake -S . -B build
cmake --build build -j
```

期待: warning なし or 既存と同程度。エラーが出たら直す。

- [ ] **Step 2: sample 01〜07 を順に手動チェック**

```bash
scripts/run-headless.sh samples/01_triangle.lua --capture /tmp/01.png --capture-frame 30
# 同様に 02..07
```

`scripts/run-golden.sh` でも `--update` を付けずに走らせ、既存 golden image と byte 一致することを確認 (描画意図は不変)。

- [ ] **Step 3: コミット**

```bash
git add samples/ third_party/lume/ src/lua_api.c src/lua_api.h src/main.c
git commit -m "refactor(samples): return module table; lua dispatch via registry ref"
```

---

## Phase 1: Entry Lua の mtime-poll hotswap (native)

> 既存の shader/verts/texture mtime-poll は `samples/lub_io.lua` 側で動いている。 entry .lua はまだ未対応なので、`app.c` で frame ごとに poll し、変化していたら `lume.hotswap` を呼ぶ。

### Task 1.1: App に hotswap state を追加

**Files:**
- Modify: `src/app.h:60-95` (App struct)
- Modify: `src/app.c`

- [ ] **Step 1: `src/app.h` の `App` に field を追加**

```c
// app.h の App 構造体の末尾に:
    char    entry_path[256];        // "samples/01_triangle.lua"
    char    entry_module_name[128]; // "01_triangle"
    int64_t entry_mtime_cache;      // last seen mtime, 0 if unknown
```

- [ ] **Step 2: `src/main.c` で `app_init` 後に値を埋める**

`SDL_AppInit` の中、`app_init(&g_app)` の後あたり:

```c
// raw (argv) と modbuf を Task 0.5 で計算済み。それを App にコピー。
SDL_strlcpy(g_app.entry_module_name, modbuf, sizeof(g_app.entry_module_name));
SDL_snprintf(g_app.entry_path, sizeof(g_app.entry_path),
             "samples/%s.lua", modbuf);
g_app.entry_mtime_cache = 0;
```

### Task 1.2: `file_mtime` を C から呼べる helper にする

**Files:**
- 確認のみ: `src/lua_api.c` で `file_mtime` Lua API は既にある (使用例: lub_io.lua)

Lua から C 関数を呼んでファイルの mtime を取る。C から直接 mtime を取る既存 helper があるか確認 — 無ければ追加。

- [ ] **Step 1: `src/lua_api.c` で `file_mtime` の C 実装を探し、純粋 C 関数 (`int64_t app_file_mtime_ns(const char *path)`) として exposable な形に小リファクタ**

具体的には `lua_api.c` の `l_file_mtime` 中の OS-specific stat 呼び出しを、`app_file_mtime_ns(const char *path)` という別ファイル (or 同ファイル内の static でなく header export) に移し、`l_file_mtime` がそれを呼ぶ形にする。

`src/app.h` に追加:
```c
int64_t app_file_mtime_ns(const char *path);  // 0 if not found
```

- [ ] **Step 2: 既存の `l_file_mtime` を `app_file_mtime_ns` 経由に書き換え、native 動作が変わらないことを confirm**

### Task 1.3: `app_frame_begin` に hotswap poll を仕込む

**Files:**
- Modify: `src/app.c`
- Modify: `src/lua_api.c` (`lua_ctx_hotswap` 新規 export)

- [ ] **Step 1: `lua_api.c` に hotswap helper を追加**

```c
// lua_api.h
bool lua_ctx_hotswap(LuaCtx *ctx, const char *module_name);

// lua_api.c
bool lua_ctx_hotswap(LuaCtx *ctx, const char *module_name) {
    if (!ctx->L) return false;
    lua_State *L = ctx->L;
    lua_getglobal(L, "lume");
    if (!lua_istable(L, -1)) { lua_pop(L, 1); return false; }
    lua_getfield(L, -1, "hotswap");
    if (!lua_isfunction(L, -1)) { lua_pop(L, 2); return false; }
    lua_pushstring(L, module_name);
    if (lua_pcall(L, 1, 1, 0) != LUA_OK) {
        SDL_Log("hotswap failed: %s", lua_tostring(L, -1));
        lua_pop(L, 2); // err + lume
        return false;
    }
    // 新 module table を registry に上書き
    if (lua_istable(L, -1)) {
        luaL_unref(L, LUA_REGISTRYINDEX, ctx->module_ref);
        ctx->module_ref = luaL_ref(L, LUA_REGISTRYINDEX);
    } else {
        lua_pop(L, 1);
    }
    lua_pop(L, 1); // lume
    return true;
}
```

- [ ] **Step 2: `app.c` の `app_frame_begin` 末尾に poll を追加**

```c
void app_frame_begin(App *app, int *out_w, int *out_h) {
    // ... 既存 ...
    // hotswap check (only after backend init, i.e., POST_BACKEND phase)
    if (app->phase == APP_PHASE_POST_BACKEND && app->entry_module_name[0]) {
        int64_t now = app_file_mtime_ns(app->entry_path);
        if (now && now != app->entry_mtime_cache) {
            if (app->entry_mtime_cache != 0) {
                SDL_Log("entry mtime changed, hotswapping %s", app->entry_module_name);
                lua_ctx_hotswap(&app->lua, app->entry_module_name);
            }
            app->entry_mtime_cache = now;
        }
    }
}
```

初回呼出時 (`entry_mtime_cache == 0`) は hotswap せず、最初の mtime を記録するだけ。これでboot 直後の余計な hotswap を回避。

### Task 1.4: 手動検証

- [ ] **Step 1: native でビルド → 起動**

```bash
cmake --build build -j
./build/lub samples/01_triangle.lua
```

- [ ] **Step 2: 別ターミナルから `samples/01_triangle.lua` を編集** (例: `clear_color` を変える、`{0.1, 0.5, 0.2, 1}` など)

期待: 保存した瞬間に画面の背景色が変わる。ログに `entry mtime changed, hotswapping 01_triangle` が出る。

- [ ] **Step 3: コミット**

```bash
git add src/app.c src/app.h src/lua_api.c src/lua_api.h
git commit -m "feat(hotreload): poll entry lua mtime and lume.hotswap each frame"
```

---

## Phase 2: WASM build infrastructure (link 成功まで)

> CMake と emcc を通せる状態にする。実際の WGPU 描画はまだ動かなくていい。Slang は no-op stub にしておき、Phase 4 で本物に置換える。

### Task 2.1: `CMakeLists.txt` に EMSCRIPTEN 分岐

**Files:**
- Modify: `CMakeLists.txt`

- [ ] **Step 1: EMSCRIPTEN 検出と source list 分岐**

`add_executable(lub ...)` の前で sources を変数化:

```cmake
if(EMSCRIPTEN)
    set(LUB_WASM ON)
endif()

set(LUB_SOURCES
    src/main.c
    src/app.c
    src/sokol_impl.c
    src/lua_api.c
    src/enums_lua.c
    src/pass.c
    src/resources.c
    src/shader.cpp
    src/pipeline.c
    src/backend_sokol.c
    src/stb_impl.c
)
if(NOT LUB_WASM)
    list(APPEND LUB_SOURCES src/capture.c src/backend_sdlgpu.c)
endif()

add_executable(lub ${LUB_SOURCES})
```

- [ ] **Step 2: `find_package(Vulkan REQUIRED)` を `if(NOT LUB_WASM)` で囲う**

```cmake
if(NOT LUB_WASM)
    find_package(Vulkan REQUIRED)
endif()
```

- [ ] **Step 3: Slang prebuilt fetch を WASM では skip**

既存の `if(NOT EXISTS "${_slang_lib_marker}") ... endif()` 全体を `if(NOT LUB_WASM)` で囲う。

- [ ] **Step 4: target link libraries を分岐**

```cmake
target_link_libraries(lub PRIVATE
    SDL3::SDL3
    lua_static
)
if(NOT LUB_WASM)
    target_link_libraries(lub PRIVATE slang Vulkan::Vulkan)
    target_link_directories(lub PRIVATE third_party/slang/lib)
endif()
if(UNIX AND NOT LUB_WASM)
    target_link_libraries(lub PRIVATE m dl)
endif()
```

- [ ] **Step 5: backend defines を分岐**

```cmake
# 末尾の target_compile_definitions(lub PRIVATE SOKOL_VULKAN) を:
if(LUB_WASM)
    target_compile_definitions(lub PRIVATE SOKOL_WGPU)
else()
    target_compile_definitions(lub PRIVATE SOKOL_VULKAN)
endif()
```

- [ ] **Step 6: WASM 用 link options + preload**

```cmake
if(LUB_WASM)
    set_target_properties(lub PROPERTIES SUFFIX ".js")
    target_link_options(lub PRIVATE
        -sASYNCIFY
        -sUSE_WEBGPU=1
        -sALLOW_MEMORY_GROWTH=1
        -sASSERTIONS=1
        -sSTACK_SIZE=1048576
        "SHELL:-sEXPORTED_RUNTIME_METHODS=['FS','ccall','UTF8ToString']"
        --preload-file ${CMAKE_SOURCE_DIR}/samples@/samples
        --preload-file ${CMAKE_SOURCE_DIR}/third_party/lume@/lume
    )
endif()
```

### Task 2.2: `CMakePresets.json` を追加

**Files:**
- Create: `CMakePresets.json`

- [ ] **Step 1: 内容**

```json
{
  "version": 6,
  "cmakeMinimumRequired": { "major": 3, "minor": 21, "patch": 0 },
  "configurePresets": [
    {
      "name": "wasm-debug",
      "displayName": "WebAssembly WebGPU Debug",
      "description": "emcmake cmake --preset wasm-debug",
      "generator": "Ninja",
      "binaryDir": "${sourceDir}/build/wasm",
      "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug" }
    },
    {
      "name": "wasm-release",
      "displayName": "WebAssembly WebGPU Release",
      "description": "emcmake cmake --preset wasm-release",
      "generator": "Ninja",
      "binaryDir": "${sourceDir}/build/wasm",
      "cacheVariables": { "CMAKE_BUILD_TYPE": "Release" }
    }
  ],
  "buildPresets": [
    { "name": "wasm-debug",   "configurePreset": "wasm-debug" },
    { "name": "wasm-release", "configurePreset": "wasm-release" }
  ]
}
```

### Task 2.3: `backend_sokol.c` の Vulkan 直叩きを EMSCRIPTEN で skip

**Files:**
- Modify: `src/backend_sokol.c`

- [ ] **Step 1: Vulkan 関連 include と関数を分岐**

ファイル冒頭付近で:

```c
#ifndef __EMSCRIPTEN__
#include <vulkan/vulkan.h>
#include <SDL3/SDL_vulkan.h>
// ... (既存 Vulkan 直叩きが必要とする include)
#endif
```

Vulkan 直叩きを行う関数群 (init で `vkCreateInstance` を呼ぶような場所) を `#ifndef __EMSCRIPTEN__` ... `#else` ... `#endif` で挟む。WASM 側は `sg_setup` の WGPU 経路だけ通す:

```c
#ifndef __EMSCRIPTEN__
    // 既存の Vulkan instance / device / swapchain 作成と sokol_gfx 連携
    ...
#else
    // WGPU 経路: sokol が WGPUDevice を window から取るのは emscripten 標準。
    // 詳細は Task 3 で詰める。ここでは link が通るように最小スケルトン。
    sg_setup(&(sg_desc){ .environment = { .defaults = { .color_format = SG_PIXELFORMAT_BGRA8 } } });
#endif
```

具体的な WGPU 経路初期化 (canvas binding) は Task 3.1 で詰める。ここでは link 成功が目的。

### Task 2.4: `shader.cpp` を EMSCRIPTEN 用に stub

**Files:**
- Modify: `src/shader.cpp`

- [ ] **Step 1: Slang include と関数呼び出しを `#ifndef __EMSCRIPTEN__` で囲う**

```cpp
#ifndef __EMSCRIPTEN__
#include "slang.h"
#include "slang-com-ptr.h"
// ... 既存 ...
#endif
```

`compile_shader_to_spirv` 等の Slang を使う関数を:

```cpp
#ifndef __EMSCRIPTEN__
// 既存 Slang 実装
#else
// stub: empty bytecode + empty reflection 返却して "always recompile fails" 状態。
// link は通るが描画はまだ動かない。Phase 4 で本実装に置換。
bool compile_shader_to_spirv(...) {
    SDL_Log("[wasm stub] shader compile not yet implemented");
    return false;
}
#endif
```

(具体的な関数名は `shader.cpp` を Read して確認、stub の return false で `use_shader` が失敗ログを出して旧 shader を維持する流れを既存に合わせる)

### Task 2.5: 初回 wasm build を試す

- [ ] **Step 1: Emscripten SDK のセットアップ確認**

```bash
emcc --version  # 3.1.x 以上であること
```

ない場合は emsdk install 手順を案内 (`git clone https://github.com/emscripten-core/emsdk && ./emsdk install latest && ./emsdk activate latest && source ./emsdk_env.sh`)。

- [ ] **Step 2: configure & build**

```bash
emcmake cmake --preset wasm-debug
cmake --build build/wasm -j
```

期待: 成功して `build/wasm/lub.{js,wasm,data}` が生成される。link error は読んで都度修正 (例: SDL3 が emscripten target を選んでない、`-sUSE_SDL=3` の追加が必要等)。

- [ ] **Step 3: SDL3 が emscripten で正しくビルドされるか問題があれば調整**

SDL3 3.2.x は emscripten をサポートするが、CMake オプションで `SDL_PTHREADS=OFF`, `SDL_TESTS=OFF` 等の調整が必要な場合がある。問題が出たら `FetchContent_MakeAvailable(SDL3)` 前に必要な set() を入れる。

- [ ] **Step 4: コミット**

```bash
git add CMakeLists.txt CMakePresets.json src/backend_sokol.c src/shader.cpp
git commit -m "build(wasm): emscripten configure/build link-clean (slang stubbed)"
```

---

## Phase 3: WebGPU 経路で sample 01 を描画する

> sokol の WGPU backend に正しく canvas を渡し、sample 01 (shader compile が動かない状態でも fallback できれば) を画面に出す。Slang はまだ stub なので、ここでは「黒画面でクラッシュしない」+「shader 経路に到達して旧 shader 維持ログが出る」状態をゴールにする。

### Task 3.1: emscripten WebGPU の canvas 取得

**Files:**
- Modify: `src/backend_sokol.c` (`__EMSCRIPTEN__` 側)
- Modify: `src/main.c` (canvas 解像度を `_canvasWidth` / `_canvasHeight` から取得する経路)

参考: lub3d の `src/sokol_impl.c` 周辺で同等パターン。

- [ ] **Step 1: emscripten で canvas selector を sokol に渡す**

sokol_gfx の WGPU backend は `emscripten_webgpu_get_device()` で device を取得する。具体的なセットアップ:

```c
#ifdef __EMSCRIPTEN__
#include <emscripten/emscripten.h>
#include <emscripten/html5_webgpu.h>

EM_JS(int, lub_get_canvas_width, (), {
    return (window._canvasWidth || 480) | 0;
});
EM_JS(int, lub_get_canvas_height, (), {
    return (window._canvasHeight || 360) | 0;
});

static bool sokol_init_wgpu(App *app) {
    WGPUDevice device = emscripten_webgpu_get_device();
    if (!device) {
        SDL_Log("failed to get webgpu device");
        return false;
    }
    // sokol_gfx WGPU setup
    sg_setup(&(sg_desc){
        .environment = {
            .wgpu = {
                .device = (const void*)device,
            },
            .defaults = {
                .color_format = SG_PIXELFORMAT_BGRA8,
            },
        },
        .logger = { .func = slog_func },
    });
    return true;
}
#endif
```

注: `emscripten_webgpu_get_device()` は同期取得だが、内部で JS の `navigator.gpu.requestAdapter / requestDevice` を待つ async 処理が走る。これは player.html 側で事前に `await navigator.gpu.requestAdapter().requestDevice()` してから `Module.preinitializedWebGPUDevice = device` を設定しておくのが定石 (emscripten doc 参照)。

- [ ] **Step 2: `web/public/player.html` で WebGPU device を pre-init**

(Phase 4 で player.html を作るので、この時点では先取りして以下を含める方針をメモに残す)

### Task 3.2: 一旦 link & 動作確認用の最小 HTML

**Files:**
- 一時的に `build/wasm/lub.html` を browser で開いて動作確認 (emcc が自動生成する shell)

- [ ] **Step 1: 最小確認**

```bash
cd build/wasm && python3 -m http.server 8000
# ブラウザで http://localhost:8000/lub.html を開く
```

期待: WebGPU device 取得まで進む、shader compile で stub の "not implemented" ログが出る、画面はクリアカラー (黒) だけが出る。コンソールにエラーが大量に出ても OK (Slang stub 状態なので)。

実 frontend は Phase 5 で組む。ここまでで wasm build → 起動 → WebGPU 初期化 → frame ループ実行が確認できれば良い。

- [ ] **Step 2: 致命エラーが出るなら fix**

SDL3 main_callbacks + emscripten + WebGPU でハマる可能性:
- SDL3 の WebGPU surface 取得を諦め、sokol_app (`sapp`) を併用するパターンに切替が必要かもしれない
- 詰まる場合は spec の "オープン項目" を参照、SDL3 main_callbacks 経路で動かなければ sokol_app を選択肢に。

- [ ] **Step 3: コミット**

```bash
git add src/backend_sokol.c src/main.c
git commit -m "feat(wasm): wire up WebGPU device into sokol_gfx setup"
```

---

## Phase 4: Slang-wasm bridge

> Slang の WASM ビルドを JS 側にロードし、`EM_ASYNC_JS` で C から WGSL コンパイルを依頼する経路を作る。

### Task 4.1: `shader.cpp` に reflection 中間 struct を切り出す

**Files:**
- Modify: `src/shader.cpp`
- Modify: `src/shader.h`

- [ ] **Step 1: 現在 SPIR-V 経由で取っている reflection を中間 struct に詰める helper を抽出**

```cpp
// shader.h に追加:
struct ShaderReflection {
    // 既存 reflection で使われているフィールド (vertex inputs, uniform blocks,
    // textures, samplers, storage buffers, ...) を中間表現として持つ
};

bool reflect_from_spirv(const uint8_t* spv_data, size_t spv_len, ShaderReflection& out);

#ifdef __EMSCRIPTEN__
bool reflect_from_slang_json(const char* json, ShaderReflection& out);
#endif
```

詳細フィールドは現状の `shader.cpp` を Read して既存の reflection 抽出ロジックから決定する。Phase 0 で sample が動いている state で残しておけば、reflection が現に使う情報セットが明確。

### Task 4.2: `EM_ASYNC_JS` ブリッジ

**Files:**
- Modify: `src/shader.cpp`

- [ ] **Step 1: bridge 宣言**

```cpp
#ifdef __EMSCRIPTEN__
#include <emscripten.h>

// JS 側 (player.html / slang-bridge.ts) が定義する window.slangCompile を呼ぶ。
// 戻り値は malloc された JSON 文字列ポインタ。"\n" の手前が status ("ok" or "error")、
// それ以降が { wgsl, reflect_json } または error message を含む JSON。
// シンプル化: 成功時は wgsl と reflect_json を null 区切りで詰めて返す、失敗時は NULL。
EM_ASYNC_JS(char*, lub_slang_compile, (const char* src, const char* entry, int stage), {
    const srcStr = UTF8ToString(src);
    const entryStr = UTF8ToString(entry);
    const result = await window.slangCompile(srcStr, entryStr, stage);
    if (!result || result.error) {
        if (result && result.error) console.error("slang:", result.error);
        return 0;
    }
    // wgsl と reflect を \x01 区切りで詰める
    const blob = result.wgsl + "\x01" + result.reflectJson;
    const len = lengthBytesUTF8(blob) + 1;
    const ptr = _malloc(len);
    stringToUTF8(blob, ptr, len);
    return ptr;
});
#endif
```

- [ ] **Step 2: stub を本実装に差し替え**

Task 2.4 で入れた stub の場所を:

```cpp
#ifdef __EMSCRIPTEN__
bool compile_shader_to_wgsl(const char* src, const char* entry, int stage,
                            std::string& out_wgsl, ShaderReflection& out_refl) {
    char* blob = lub_slang_compile(src, entry, stage);
    if (!blob) return false;
    std::string s(blob);
    free(blob);
    auto sep = s.find('\x01');
    if (sep == std::string::npos) return false;
    out_wgsl = s.substr(0, sep);
    std::string reflect_json = s.substr(sep + 1);
    return reflect_from_slang_json(reflect_json.c_str(), out_refl);
}
#endif
```

このとき、native の `compile_shader_to_spirv` と web の `compile_shader_to_wgsl` の上位 caller (おそらく `use_shader` の実装) を 1 箇所で分岐:

```cpp
bool sgl_shader_compile(...) {
#ifdef __EMSCRIPTEN__
    return compile_shader_to_wgsl(...);
#else
    return compile_shader_to_spirv(...);
#endif
}
```

sokol_gfx は `sg_shader_desc` に backend 別の bytecode/source slots を持つので、native 側は SPIR-V を渡し、web 側は WGSL string を渡す。

### Task 4.3: Slang reflection JSON のパース実装

**Files:**
- Modify: `src/shader.cpp`

- [ ] **Step 1: JSON パーサを選定**

軽量 single-header の `nlohmann/json` を vendor (もしくは既に依存があるか確認、なければ `third_party/nlohmann/json.hpp` で MIT). 設計書通り中間 struct に詰める。

```cpp
#ifdef __EMSCRIPTEN__
#include "json.hpp"
using json = nlohmann::json;

bool reflect_from_slang_json(const char* json_text, ShaderReflection& out) {
    auto j = json::parse(json_text, nullptr, false);
    if (j.is_discarded()) return false;
    // Slang reflection JSON のスキーマに従って out を埋める。
    // 詳細フィールドマッピングは Slang docs:
    //   https://shader-slang.org/slang/user-guide/reflection.html
    return true;
}
#endif
```

Slang reflection JSON は SPIRV-Cross と field 名が異なる。reflection 中間 struct を共通化したのでマッピング箇所を 1 箇所に集約できる。

### Task 4.4: native 動作の retest

- [ ] **Step 1: native ビルドして sample 01〜07 が引き続き動くか確認 (回帰防止)**

```bash
cmake --build build -j
scripts/run-golden.sh
```

- [ ] **Step 2: コミット (web 側はまだ動かないがコード形だけ揃った状態)**

```bash
git add src/shader.cpp src/shader.h third_party/nlohmann/
git commit -m "feat(shader): EM_ASYNC_JS bridge to slang-wasm; reflection abstraction"
```

---

## Phase 5: Frontend scaffold (Vite + iframe + minimal editor)

> `web/` に Vite + TS 環境を作り、parent / player.html の postMessage 経路、最小の CodeMirror エディタ、サンプル fetch を組む。まだ Slang は接続しない。

### Task 5.1: Vite プロジェクトのスケルトン

**Files:**
- Create: `web/package.json`
- Create: `web/tsconfig.json`
- Create: `web/vite.config.ts`
- Create: `web/index.html`

- [ ] **Step 1: `web/package.json`**

```json
{
  "name": "lub-playground",
  "private": true,
  "version": "0.0.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "vite build",
    "preview": "vite preview"
  },
  "dependencies": {
    "codemirror": "^6.0.1",
    "@codemirror/legacy-modes": "^6.4.0",
    "@codemirror/language": "^6.10.0",
    "@codemirror/theme-one-dark": "^6.1.2",
    "@shader-slang/slang-wasm": "^0.x"
  },
  "devDependencies": {
    "vite": "^5.0.0",
    "typescript": "^5.3.0"
  }
}
```

(`@shader-slang/slang-wasm` の最新 minor を `npm view` で確認して固定)

- [ ] **Step 2: `web/tsconfig.json`**

```json
{
  "compilerOptions": {
    "target": "ES2020",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "noEmit": true,
    "lib": ["ES2020", "DOM", "DOM.Iterable"],
    "types": ["vite/client"]
  },
  "include": ["playground/**/*.ts"]
}
```

- [ ] **Step 3: `web/vite.config.ts`**

```ts
import { defineConfig } from 'vite'
import { readFileSync, existsSync, cpSync } from 'node:fs'
import { resolve, extname } from 'node:path'

export default defineConfig({
  publicDir: 'public',
  server: { fs: { allow: ['..'] } },
  build: { outDir: 'dist' },
  plugins: [
    {
      name: 'serve-samples-and-wasm',
      configureServer(server) {
        // /samples/* を ../samples から serve
        server.middlewares.use('/samples', (req, res, next) => {
          const filePath = resolve(__dirname, '../samples', req.url?.slice(1) || '')
          if (!existsSync(filePath)) return next()
          res.setHeader('Content-Type',
            ['.lua','.slang','.txt'].includes(extname(filePath).toLowerCase())
              ? 'text/plain; charset=utf-8'
              : 'application/octet-stream')
          res.end(readFileSync(filePath))
        })
        // lub.{js,wasm,data} を build/wasm から serve
        server.middlewares.use('/wasm', (req, res, next) => {
          const filePath = resolve(__dirname, '../build/wasm', req.url?.slice(1) || '')
          if (!existsSync(filePath)) return next()
          const ext = extname(filePath).toLowerCase()
          res.setHeader('Content-Type',
            ext === '.wasm' ? 'application/wasm' :
            ext === '.js'   ? 'application/javascript' :
            'application/octet-stream')
          res.end(readFileSync(filePath))
        })
      },
      closeBundle() {
        cpSync('../samples', 'dist/samples', { recursive: true })
        cpSync('../build/wasm', 'dist/wasm', { recursive: true })
      },
    },
  ],
})
```

- [ ] **Step 4: `web/index.html`**

```html
<!doctype html>
<html lang="ja">
<head>
  <meta charset="UTF-8" />
  <title>lub playground</title>
  <style>
    body { margin: 0; display: grid; grid-template-columns: 1fr 1fr; height: 100vh; font-family: sans-serif; }
    #left  { display: flex; flex-direction: column; }
    #right { display: flex; flex-direction: column; }
    #tabs  { display: flex; background: #222; color: #ddd; padding: 4px; gap: 4px; }
    .tab   { padding: 4px 8px; background: #333; cursor: pointer; border-radius: 3px 3px 0 0; }
    .tab.active { background: #555; }
    .tab.dirty::after { content: " *"; color: #fa0; }
    #editor { flex: 1; overflow: auto; }
    #toolbar { padding: 4px; background: #111; color: #eee; display: flex; gap: 8px; align-items: center; }
    #player-mount { flex: 1; display: flex; }
    iframe { flex: 1; border: 0; background: #000; }
    #log { height: 120px; background: #111; color: #0f0; font-family: monospace; font-size: 12px; overflow: auto; padding: 4px; }
  </style>
</head>
<body>
  <div id="left">
    <div id="toolbar">
      <select id="sample-select"></select>
      <button id="restart-btn">↻ Restart</button>
      <span id="status"></span>
    </div>
    <div id="tabs"></div>
    <div id="editor"></div>
  </div>
  <div id="right">
    <div id="player-mount"></div>
    <div id="log"></div>
  </div>
  <script type="module" src="/playground/main.ts"></script>
</body>
</html>
```

### Task 5.2: player.html

**Files:**
- Create: `web/public/player.html`

- [ ] **Step 1: 内容 (lub3d の player.html ベース + 我々の sync プロトコル)**

```html
<!doctype html>
<html><head>
  <meta charset="UTF-8" />
  <style>
    html, body { margin: 0; height: 100%; background: #000; overflow: hidden; }
    canvas { display: block; }
  </style>
</head><body>
  <canvas id="canvas" width="480" height="360" tabindex="0"></canvas>
  <script type="module">
    const canvas = document.getElementById('canvas')
    window._canvasWidth = 480
    window._canvasHeight = 360

    // WebGPU を事前に取得して Module に渡す
    async function initWebGPU() {
      if (!navigator.gpu) throw new Error('WebGPU not available')
      const adapter = await navigator.gpu.requestAdapter()
      const device = await adapter.requestDevice()
      return device
    }

    let pendingFiles = null
    let pendingEntry = null

    function writeFileEnsureDir(path, content) {
      const parts = path.split('/')
      let cur = ''
      for (let i = 0; i < parts.length - 1; ++i) {
        cur = cur ? cur + '/' + parts[i] : parts[i]
        try { FS.mkdir(cur) } catch (e) {}
      }
      FS.writeFile(path, content)
    }

    window.addEventListener('message', async (e) => {
      if (e.data.type === 'setFiles') {
        pendingFiles = e.data.files
        pendingEntry = e.data.entry
        const device = await initWebGPU()
        window.Module = {
          canvas,
          preinitializedWebGPUDevice: device,
          print: (t) => parent.postMessage({type:'log', msg: t, level:'log'}, '*'),
          printErr: (t) => parent.postMessage({type:'log', msg: t, level:'error'}, '*'),
          preRun: [() => {
            for (const [p, c] of Object.entries(pendingFiles))
              writeFileEnsureDir(p.startsWith('samples/') ? p : 'samples/' + p, c)
            window._lub_entry_module = pendingEntry
          }],
          arguments: [pendingEntry],
        }
        const script = document.createElement('script')
        script.src = '/wasm/lub.js'
        document.body.appendChild(script)
      } else if (e.data.type === 'syncFiles') {
        if (!window.FS) return
        for (const [p, c] of Object.entries(e.data.files))
          writeFileEnsureDir(p.startsWith('samples/') ? p : 'samples/' + p, c)
        // mtime-poll が拾うので明示 trigger 不要
      }
    })

    parent.postMessage({ type: 'playerReady' }, '*')
  </script>
</body></html>
```

### Task 5.3: `playground/main.ts` の orchestrator スケルトン

**Files:**
- Create: `web/playground/main.ts`
- Create: `web/playground/editor.ts`
- Create: `web/playground/samples.ts`

- [ ] **Step 1: `editor.ts` (CodeMirror multi-tab)**

```ts
import { EditorView, basicSetup } from 'codemirror'
import { StreamLanguage, LanguageDescription } from '@codemirror/language'
import { lua } from '@codemirror/legacy-modes/mode/lua'
import { cpp } from '@codemirror/legacy-modes/mode/clike' // HLSL/Slang は C 系で代用
import { oneDark } from '@codemirror/theme-one-dark'

export type EditorFile = { content: string; dirty: boolean; initial: string }

let view: EditorView | null = null
let files = new Map<string, EditorFile>()
let activePath: string | null = null
let onChangeCb: ((path: string, content: string) => void) | null = null

function modeFor(path: string) {
  if (path.endsWith('.slang')) return StreamLanguage.define(cpp)
  return StreamLanguage.define(lua)
}

export function attachEditor(container: HTMLElement,
                              onChange: (path: string, content: string) => void) {
  onChangeCb = onChange
  view = new EditorView({
    doc: '',
    extensions: [
      basicSetup,
      oneDark,
      EditorView.theme({ '&': { height: '100%' }, '.cm-scroller': { overflow: 'auto' } }),
      EditorView.updateListener.of((u) => {
        if (u.docChanged && activePath && onChangeCb) {
          const content = view!.state.doc.toString()
          const f = files.get(activePath)
          if (f) {
            f.content = content
            f.dirty = content !== f.initial
            onChangeCb(activePath, content)
          }
        }
      }),
    ],
    parent: container,
  })
}

export function setFiles(newFiles: Map<string, EditorFile>) {
  files = newFiles
  rebuildTabsUI()
  const first = files.keys().next().value as string | undefined
  if (first) selectTab(first)
}

export function getFiles(): Map<string, EditorFile> { return files }

export function selectTab(path: string) {
  const f = files.get(path)
  if (!f || !view) return
  activePath = path
  view.dispatch({
    changes: { from: 0, to: view.state.doc.length, insert: f.content },
  })
  rebuildTabsUI()
}

function rebuildTabsUI() {
  const tabs = document.getElementById('tabs')!
  tabs.innerHTML = ''
  for (const [path, f] of files) {
    const el = document.createElement('div')
    el.className = 'tab' + (path === activePath ? ' active' : '') + (f.dirty ? ' dirty' : '')
    el.textContent = path
    el.addEventListener('click', () => selectTab(path))
    tabs.appendChild(el)
  }
}
```

- [ ] **Step 2: `samples.ts` (sample fetch + scan)**

```ts
export const SAMPLES = [
  '01_triangle', '02_vertex_color', '03_texture', '04_mvp',
  '05_postprocess', '06_deferred', '07_compute',
]

export async function loadSample(name: string): Promise<Map<string, {content:string;dirty:boolean;initial:string}>> {
  const luaPath = `samples/${name}.lua`
  const luaText = await fetch('/' + luaPath).then(r => r.text())
  const files = new Map<string, {content:string;dirty:boolean;initial:string}>()
  files.set(luaPath, { content: luaText, dirty: false, initial: luaText })
  const refs = scanLuaReferences(luaText)
  for (const r of refs) {
    const res = await fetch('/' + r)
    if (res.ok) {
      const t = await res.text()
      files.set(r, { content: t, dirty: false, initial: t })
    }
  }
  return files
}

function scanLuaReferences(src: string): string[] {
  const re = /load_(?:text|floats)\(\s*"([^"]+)"\s*\)/g
  const out: string[] = []
  let m: RegExpExecArray | null
  while ((m = re.exec(src))) {
    if (!out.includes(m[1])) out.push(m[1])
  }
  return out
}
```

- [ ] **Step 3: `main.ts`**

```ts
import { attachEditor, setFiles, getFiles } from './editor'
import { SAMPLES, loadSample } from './samples'

let playerIframe: HTMLIFrameElement | null = null
let currentSample = '01_triangle'
let syncTimer: number | null = null

const selector = document.querySelector<HTMLSelectElement>('#sample-select')!
for (const s of SAMPLES) {
  const o = document.createElement('option'); o.value = s; o.textContent = s
  selector.appendChild(o)
}
selector.value = currentSample
selector.addEventListener('change', async () => {
  currentSample = selector.value
  const files = await loadSample(currentSample)
  setFiles(files as any)
  restart()
})

document.querySelector('#restart-btn')!.addEventListener('click', restart)

window.addEventListener('message', (e) => {
  if (e.data.type === 'log') {
    const log = document.getElementById('log')!
    const line = document.createElement('div')
    line.textContent = e.data.msg
    if (e.data.level === 'error') line.style.color = '#f55'
    log.appendChild(line); log.scrollTop = log.scrollHeight
  }
})

attachEditor(document.querySelector('#editor')!, (path, content) => {
  if (syncTimer) clearTimeout(syncTimer)
  syncTimer = setTimeout(() => {
    const dirty: Record<string, string> = {}
    for (const [p, f] of getFiles()) if (f.dirty) dirty[p] = f.content
    if (Object.keys(dirty).length === 0) return
    playerIframe?.contentWindow?.postMessage(
      { type: 'syncFiles', files: dirty }, '*')
  }, 300) as unknown as number
})

async function restart() {
  if (playerIframe) playerIframe.remove()
  playerIframe = document.createElement('iframe')
  playerIframe.src = '/player.html'
  document.getElementById('player-mount')!.appendChild(playerIframe)
  await waitForMsg('playerReady')
  const all: Record<string, string> = {}
  for (const [p, f] of getFiles()) all[p] = f.content
  playerIframe.contentWindow!.postMessage(
    { type: 'setFiles', files: all, entry: currentSample }, '*')
}

function waitForMsg(type: string): Promise<MessageEvent> {
  return new Promise((resolve) => {
    const h = (e: MessageEvent) => {
      if (e.data?.type === type) { window.removeEventListener('message', h); resolve(e) }
    }
    window.addEventListener('message', h)
  })
}

// 初回ロード
loadSample(currentSample).then((files) => {
  setFiles(files as any)
  restart()
})
```

### Task 5.4: 起動確認

- [ ] **Step 1: 依存 install + dev server**

```bash
cd web
pnpm install         # or npm install
pnpm dev
```

ブラウザで `http://localhost:5173`

期待: 左に CodeMirror editor、タブに `samples/01_triangle.lua` ほか .slang が並ぶ、右に iframe が出て (Slang stub のため) shader error ログが出るが落ちない。

- [ ] **Step 2: コミット**

```bash
git add web/
git commit -m "feat(web): vite scaffold, codemirror multi-tab, iframe player skeleton"
```

---

## Phase 6: Slang-wasm 接続と sample 01 描画

> player.html 内に slang-wasm をロードし、`window.slangCompile` を本物にする。sample 01_triangle が画面に出るまで持っていく。

### Task 6.1: `slang-bridge.ts` の追加と player.html への組み込み

**Files:**
- Create: `web/playground/slang-bridge.ts`
- Modify: `web/public/player.html` (slang-wasm を player 側でロード、`window.slangCompile` を expose)

- [ ] **Step 1: slang-bridge を player から使えるようにモジュール構成を変更**

player.html 内の `<script type="module">` から `import` で `@shader-slang/slang-wasm` を読み込めるよう、player.html を vite が処理する HTML エントリにする (vite.config.ts の `build.rollupOptions.input` に追加するか、`public/` から `playground/` に移してインポート可能にする)。

`web/playground/player.ts` (新規) として切り出し:

```ts
import slang from '@shader-slang/slang-wasm'

const canvas = document.getElementById('canvas') as HTMLCanvasElement
;(window as any)._canvasWidth = 480
;(window as any)._canvasHeight = 360

let slangSession: any = null
let slangGlobal: any = null

async function initSlang() {
  const mod = await slang()
  slangGlobal = mod.createGlobalSession()
  ;(window as any).slangCompile = async (src: string, entry: string, stage: number) => {
    const session = slangGlobal.createSession({ targetType: mod.SLANG_WGSL })
    try {
      const module = session.loadModuleFromSource(src, 'shader', 'shader.slang')
      const ep = module.findEntryPointByName(entry)
      const program = session.createCompositeComponentType([module, ep])
      const wgsl = program.getTargetCode(0).toString()
      const reflectJson = program.getLayout(0).toJson()
      return { wgsl, reflectJson }
    } catch (e: any) {
      return { error: String(e.message || e) }
    }
  }
}

initSlang().then(() => {
  parent.postMessage({ type: 'playerReady' }, '*')
})

// 以下、Phase 5 で player.html 内の <script> にあった message handler を移植
```

`player.html` は `<script type="module" src="/playground/player.ts"></script>` で読む。

(npm の `@shader-slang/slang-wasm` の実 API 名は実装時に `node_modules/@shader-slang/slang-wasm/dist/*.d.ts` を読んで確認。loadModuleFromSource / getTargetCode 等の名前は最新版で変わる可能性。)

- [ ] **Step 2: vite.config.ts で player.html もエントリ化**

```ts
// build.rollupOptions に追加
build: {
  outDir: 'dist',
  rollupOptions: {
    input: {
      main: resolve(__dirname, 'index.html'),
      player: resolve(__dirname, 'player.html'),
    },
  },
},
```

`web/player.html` を `web/public/player.html` から移動し、`web/playground/player.ts` を import。

### Task 6.2: shader compile から sokol_gfx へ繋ぐ

**Files:**
- Modify: `src/shader.cpp` (Phase 4 で stub から WGSL を返すまで実装済み)
- Modify: `src/backend_sokol.c` または `src/shader.cpp` の上位 caller — sokol への shader 渡しが WGSL string をそのまま渡す経路を確認

- [ ] **Step 1: `sg_make_shader` に WGSL を渡す経路**

sokol_gfx の WGPU backend は `sg_shader_desc.vertex_func.source` / `.fragment_func.source` に WGSL を文字列で受ける。reflection から bindings / uniforms をマッピングし、`sg_shader_desc` を組み立てる。

具体的な mapping ロジックは既存の SPIR-V 経路 (sokol_gfx の Vulkan backend 用) と並列で書く。既存コードを Read して location 番号 / binding 番号の対応関係を確認、Slang reflection JSON から同じ情報を引いて埋める。

### Task 6.3: 動作確認 (sample 01)

- [ ] **Step 1: rebuild & 起動**

```bash
cmake --build build/wasm -j
cd web && pnpm dev
```

ブラウザを開く。期待: sample 01 の三角形 (オレンジ色) が iframe に出る。コンソールに大きなエラーがない。

- [ ] **Step 2: 詰まりやすい点と対処**

- Slang reflection の binding 番号と sokol_gfx の binding 番号がずれる → reflection JSON のフィールドマッピングを修正
- canvas resolution が 0x0 → player.html で width/height 属性を確実に set
- WebGPU が device lost → adapter / device 取得を `requestAdapter({ powerPreference: 'high-performance' })` に
- preload-file の path が `/samples/01_triangle.lua` のはずが `samples/01_triangle.lua` (no leading slash) になっていて MEMFS から fopen 失敗 → CMake の `--preload-file ${CMAKE_SOURCE_DIR}/samples@/samples` の @ 以降の path を確認

- [ ] **Step 3: コミット**

```bash
git add web/ src/shader.cpp src/backend_sokol.c
git commit -m "feat(wasm): slang-wasm bridge wired; sample 01 renders in browser"
```

---

## Phase 7: 編集 → debounce auto-sync の動作確認

> ここまでで shader/verts/texture/Lua いずれも mtime-poll で hot reload する経路が存在するはず。editor の debounce 経路が actually MEMFS を更新し、C 側が変化を拾うか確認する。

### Task 7.1: shader 編集が反映されるか手動チェック

- [ ] **Step 1: 起動した状態で `samples/data/01_triangle.fs.slang` タブを開き、色を変える**

例: `return float4(1.0, 0.4, 0.0, 1.0);` → `return float4(0.0, 1.0, 0.5, 1.0);`

期待: 300ms 後に三角形の色が変わる。コンソールに `slang compile ok` / shader recompile ログ。

- [ ] **Step 2: 失敗したら**

- syncFiles message が届いていない → DevTools の console で `Module.FS.readFile('samples/data/01_triangle.fs.slang','utf8')` を確認
- mtime poll が拾わない → emscripten MEMFS の `FS.writeFile` が mtime を更新するか確認 (古い emscripten では update されない場合あり; 必要なら `FS.utime(path, now, now)` を後追いで叩く)
- Slang compile error → reflection / binding 周りで失敗、player の log を確認

### Task 7.2: Lua 編集 (entry .lua) が hotswap されるか

- [ ] **Step 1: `samples/01_triangle.lua` の `clear_color` を変更**

```lua
begin_pass({ target = main_tex, clear_color = {0.5, 0.0, 0.5, 1} })
```

期待: 300ms 後に背景色が紫に変わる。コンソールに `entry mtime changed, hotswapping 01_triangle` ログ。

### Task 7.3: verts (.verts.lua) 編集が反映されるか

- [ ] **Step 1: 頂点を編集** → 三角形の形が変わる確認。

### Task 7.4: コミット (バグ修正があれば)

- [ ] **Step 1: もし fix があれば**

```bash
git commit -am "fix(wasm): debounce auto-sync wires up hot reload for all asset types"
```

---

## Phase 8: 残りのサンプルと README

### Task 8.1: sample 02〜07 をブラウザで動作確認

- [ ] **Step 1: 各サンプルを selector から切替て表示確認**

問題が出たサンプルは個別 fix。compute (sample 07) は WebGPU compute をサポートする browser でのみ動作する旨を README に明記。

### Task 8.2: README.md 追記

**Files:**
- Modify: `README.md`

- [ ] **Step 1: web build セクション**

```markdown
## Web (WebGPU)

ブラウザ上でサンプルを走らせる playground を `web/` に置いている。
編集に応じて debounce auto-sync で即時反映される。

### Build

```sh
# Emscripten SDK が `emcc --version` で見える状態にしておく
emcmake cmake --preset wasm-debug
cmake --build build/wasm -j

# Vite dev server
cd web
pnpm install
pnpm dev
# ブラウザで http://localhost:5173 を開く
```

### 仕組み

- sokol_gfx の `SOKOL_WGPU` backend を emscripten ビルド
- Slang は `@shader-slang/slang-wasm` (JS) 経由で `EM_ASYNC_JS` ブリッジ
- editor は parent ページの CodeMirror multi-tab、player は iframe (`/player.html`) に隔離
- 編集 → 300ms debounce → postMessage で `FS.writeFile` → C 側 mtime-poll が拾って recompile/hotswap

### 制約

- WebGPU が利用可能なブラウザのみ (Chromium / Edge 推奨)。Safari は flag 有効でも不安定。
- sample 07 (compute) は WebGPU compute サポートが必要。
- capture / golden image diff は web では未対応。

### Native との live-edit 差分

- shader / 頂点 / texture: native も web も既存の mtime-poll + content-hash version で同じ挙動
- entry Lua: lume.hotswap + `app.c` の entry mtime-poll で両 OS 同じ挙動
```

- [ ] **Step 2: 「未実装 (将来)」セクションから "macOS" だけ残し、wasm を削除 (実現済みなので)**

### Task 8.3: 最終コミット

- [ ] **Step 1: 残作業のクリーンアップ**

```bash
git status
git add README.md samples/ src/ web/  # 必要な分だけ
git commit -m "docs: add web playground build & usage"
```

---

## Self-Review

- ✅ Spec の各セクションに対応する Task がある:
  - lume + entry hotswap → Phase 0, 1
  - WASM build infra → Phase 2
  - WebGPU 経路 → Phase 3
  - Slang bridge → Phase 4
  - Frontend (editor + player) → Phase 5
  - End-to-end 動作 → Phase 6, 7
  - Polish → Phase 8
- ✅ プレースホルダ (`TBD` 等) はなし。具体的なファイル / コード / コマンドを記述。
- ⚠ Slang reflection JSON のフィールドマッピングは「実装時に Slang docs 参照」と委ねている。具体的なマッピングテーブルは実装時の実 JSON 出力を見ながら詰める必要あり (これは spec の Open Item とも整合)。
- ⚠ SDL3 emscripten 経路がうまく動かない場合の代替 (sokol_app への乗り換え) は spec の Open Item として残してある。Phase 3 のハマりどころとして言及済み。

