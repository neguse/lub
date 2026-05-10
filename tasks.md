# sglua PoC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lua から呼べる薄い 3D 描画ライブラリの PoC を作り、仕様書の Sample 1〜4 (単色三角形 / 頂点カラー / テクスチャ / MVP) を `samples/*.lua` として動かす。

**Architecture:**
ホスト言語 C。SDL3 でウィンドウ + main callbacks、sokol_gfx (GL 3.3 backend) で描画、Lua 5.4 で API 露出、Slang をライブラリ同梱してランタイム shader compile。仕様書の `use_*(key, ..., version)` 宣言型ライフサイクルと `begin_pass / draw / end_pass` を最小実装する。MRT / post process / hot reload / sweep の高度機能は後段に回し、PoC では「触れたリソースは保持、無条件で再 upload は version 比較で抑制」までとする。

**Tech Stack:**
- C11
- CMake 3.20+ (FetchContent で SDL3 と Lua を取得)
- SDL3 (release tag, main callbacks API)
- sokol_gfx (single-header, vendored copy)
- Lua 5.4 (FetchContent / amalgamation)
- Slang (prebuilt binary release を vendor)
- Linux x86_64 / GL 3.3 を最初のターゲット platform とする

---

## File Structure

```
sglua/
├── CMakeLists.txt
├── tasks.md                # この計画
├── third_party/
│   ├── sokol/sokol_gfx.h   # vendored single-header
│   ├── slang/              # prebuilt slang ヘッダ + lib (Task 1 で展開)
│   │   ├── include/slang.h
│   │   └── lib/libslang.so
│   └── (SDL3 / Lua は CMake FetchContent で build dir に降りる)
├── src/
│   ├── main.c              # SDL3 main callbacks エントリ
│   ├── app.h, app.c        # アプリ状態 (window, gl ctx, sg_env, lua_State, frame_index)
│   ├── lua_api.h, lua_api.c  # use_buffer/use_texture/use_shader/begin_pass/end_pass/draw/retain の Lua 関数登録
│   ├── enums.h             # VERTEX/RGBA8/CLEAR/... の C 側 enum
│   ├── enums_lua.c         # 上記 enum を Lua グローバルに push する
│   ├── resources.h, resources.c  # key → entry (type/version/handle/last_seen_frame) のハッシュマップ
│   ├── shader.h, shader.c  # Slang compile wrapper + sg_shader 構築 + reflection
│   ├── pipeline.h, pipeline.c  # (shader, blend, depth, ... , target_fmt) → sg_pipeline cache
│   └── pass.h, pass.c      # begin_pass / end_pass / 現 pass state
├── samples/
│   ├── 01_triangle.lua
│   ├── 02_vertex_color.lua
│   ├── 03_texture.lua
│   └── 04_mvp.lua
└── assets/
    └── tex.png             # Sample 3 用テクスチャ (16x16 程度の手書き png)
```

ファイル分割方針: 「Slang 周辺」「pipeline cache」「pass state」「リソース key map」を独立 unit にする。`lua_api.c` はそれらを呼ぶ薄い glue だけ。`main.c` は SDL のエントリと `app.c` への委譲のみ。

---

## Task 1: プロジェクト基盤と空ウィンドウ

**Goal:** CMake で build → SDL3 ウィンドウが開いて即終了 (まだ描画なし)。

**Files:**
- Create: `CMakeLists.txt`
- Create: `src/main.c`
- Create: `third_party/sokol/sokol_gfx.h` (https://raw.githubusercontent.com/floooh/sokol/master/sokol_gfx.h を取得して保存)
- Create: `third_party/slang/include/slang.h` および `third_party/slang/lib/libslang.so` (https://github.com/shader-slang/slang/releases から `slang-<ver>-linux-x86_64.tar.gz` を展開して配置)

- [ ] **Step 1: 依存ファイルを配置**

```bash
mkdir -p third_party/sokol third_party/slang/{include,lib}
curl -L -o third_party/sokol/sokol_gfx.h https://raw.githubusercontent.com/floooh/sokol/master/sokol_gfx.h
# Slang は最新リリース (例 v2025.x) を取って展開
# 展開後: include/slang.h, bin/libslang.so → third_party/slang/include, third_party/slang/lib に移動
ls third_party/sokol/sokol_gfx.h third_party/slang/include/slang.h third_party/slang/lib/libslang.so
```

期待: 3 ファイル全てが存在する。

- [ ] **Step 2: CMakeLists.txt を作成**

```cmake
cmake_minimum_required(VERSION 3.20)
project(sglua C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

include(FetchContent)

FetchContent_Declare(
  SDL3
  GIT_REPOSITORY https://github.com/libsdl-org/SDL.git
  GIT_TAG release-3.2.0
)
FetchContent_MakeAvailable(SDL3)

FetchContent_Declare(
  lua
  URL https://www.lua.org/ftp/lua-5.4.7.tar.gz
)
FetchContent_MakeAvailable(lua)

# Lua は素の Makefile なので、ソースを集めて static lib を作る
file(GLOB LUA_SRC ${lua_SOURCE_DIR}/src/*.c)
list(REMOVE_ITEM LUA_SRC
  ${lua_SOURCE_DIR}/src/lua.c
  ${lua_SOURCE_DIR}/src/luac.c
)
add_library(lua_static STATIC ${LUA_SRC})
target_include_directories(lua_static PUBLIC ${lua_SOURCE_DIR}/src)

add_executable(sglua
  src/main.c
)
target_include_directories(sglua PRIVATE
  third_party/sokol
  third_party/slang/include
  src
)
target_link_directories(sglua PRIVATE third_party/slang/lib)
target_link_libraries(sglua PRIVATE
  SDL3::SDL3
  lua_static
  slang
  m dl
)
# OpenGL 3.3 backend を sokol_gfx に指示 (Linux)
target_compile_definitions(sglua PRIVATE
  SOKOL_GLCORE
)
```

- [ ] **Step 3: src/main.c の最小版を書く**

SDL3 main callbacks API を使う。Lua も sokol も今回はまだ読み込まない。

```c
#define SDL_MAIN_USE_CALLBACKS 1
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>

static SDL_Window *g_window;

SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
    (void)appstate; (void)argc; (void)argv;
    if (!SDL_Init(SDL_INIT_VIDEO)) {
        SDL_Log("SDL_Init failed: %s", SDL_GetError());
        return SDL_APP_FAILURE;
    }
    g_window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_OPENGL | SDL_WINDOW_RESIZABLE);
    if (!g_window) return SDL_APP_FAILURE;
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
    (void)appstate;
    if (event->type == SDL_EVENT_QUIT) return SDL_APP_SUCCESS;
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
    (void)appstate;
    SDL_Delay(16);
    return SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
    (void)appstate; (void)result;
    if (g_window) SDL_DestroyWindow(g_window);
    SDL_Quit();
}
```

- [ ] **Step 4: build & 動作確認**

```bash
cmake -S . -B build
cmake --build build -j
./build/sglua
```

期待: 1280x720 のウィンドウが開く。閉じると終了。`Ctrl+C` でも止まる。

- [ ] **Step 5: コミット**

```bash
git init
echo "build/" > .gitignore
git add .gitignore CMakeLists.txt src/main.c third_party/sokol third_party/slang/include tasks.md
# libslang.so はサイズが大きいので扱いを決める。最初は Git LFS or 別途 README に取得手順、で良い。
# ここでは LFS を後回しにし、libslang.so だけ未追跡のままにする。
git add -- ':!third_party/slang/lib/*'
git commit -m "feat: minimal CMake project with SDL3 window"
```

---

## Task 2: sokol_gfx 初期化と画面クリア

**Goal:** 毎フレーム sokol_gfx で青グレーにクリアするところまで C 側で完結させる。

**Files:**
- Create: `src/app.h`, `src/app.c`
- Modify: `src/main.c`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: src/app.h を作成**

```c
#pragma once
#include <SDL3/SDL.h>
#include "sokol_gfx.h"

typedef struct App {
    SDL_Window *window;
    SDL_GLContext gl_ctx;
    int frame_index;
} App;

bool app_init(App *app);
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);
```

- [ ] **Step 2: src/app.c を作成**

```c
#define SOKOL_IMPL
#include "sokol_gfx.h"
#include "app.h"
#include <SDL3/SDL.h>

bool app_init(App *app) {
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 3);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 3);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_CORE);
    app->window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_OPENGL | SDL_WINDOW_RESIZABLE);
    if (!app->window) return false;
    app->gl_ctx = SDL_GL_CreateContext(app->window);
    if (!app->gl_ctx) return false;
    SDL_GL_MakeCurrent(app->window, app->gl_ctx);
    SDL_GL_SetSwapInterval(1);

    sg_setup(&(sg_desc){
        .environment = { .defaults = {
            .color_format = SG_PIXELFORMAT_RGBA8,
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
        }},
        .logger.func = NULL,
    });
    app->frame_index = 0;
    return true;
}

void app_frame_begin(App *app, int *out_w, int *out_h) {
    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;
}

void app_frame_end(App *app) {
    sg_commit();
    SDL_GL_SwapWindow(app->window);
    app->frame_index++;
}

void app_shutdown(App *app) {
    sg_shutdown();
    if (app->gl_ctx) SDL_GL_DestroyContext(app->gl_ctx);
    if (app->window) SDL_DestroyWindow(app->window);
}
```

- [ ] **Step 3: src/main.c を書き換え**

```c
#define SDL_MAIN_USE_CALLBACKS 1
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include "app.h"

static App g_app;

SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
    (void)appstate; (void)argc; (void)argv;
    SDL_Init(SDL_INIT_VIDEO);
    if (!app_init(&g_app)) return SDL_APP_FAILURE;
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
    (void)appstate;
    if (event->type == SDL_EVENT_QUIT) return SDL_APP_SUCCESS;
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
    (void)appstate;
    int w, h;
    app_frame_begin(&g_app, &w, &h);
    sg_pass pass = {
        .action = {
            .colors[0] = { .load_action = SG_LOADACTION_CLEAR,
                           .clear_value = { 0.1f, 0.15f, 0.25f, 1.0f } },
        },
        .swapchain = {
            .width = w, .height = h,
            .sample_count = 1,
            .color_format = SG_PIXELFORMAT_RGBA8,
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .gl.framebuffer = 0,
        },
    };
    sg_begin_pass(&pass);
    sg_end_pass();
    app_frame_end(&g_app);
    return SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
    (void)appstate; (void)result;
    app_shutdown(&g_app);
    SDL_Quit();
}
```

- [ ] **Step 4: CMakeLists.txt の sources に app.c を追加**

```cmake
add_executable(sglua
  src/main.c
  src/app.c
)
target_link_libraries(sglua PRIVATE
  SDL3::SDL3 lua_static slang m dl
  GL  # OpenGL loader. システム依存。FindOpenGL を使うのが堅い
)
find_package(OpenGL REQUIRED)
target_link_libraries(sglua PRIVATE OpenGL::GL)
```

- [ ] **Step 5: build & 動作確認**

```bash
cmake --build build -j
./build/sglua
```

期待: 1280x720 のウィンドウが青グレー (`#1A2640` 程度) で塗りつぶされている。リサイズしても追従する。

- [ ] **Step 6: コミット**

```bash
git add CMakeLists.txt src/main.c src/app.h src/app.c
git commit -m "feat: bring up sokol_gfx with GL 3.3 swapchain clear"
```

---

## Task 3: Lua VM 組み込みと 4 コールバック dispatch

**Goal:** `samples/00_hello.lua` を読み込み、`on_init` / `on_frame` / `on_event` / `on_quit` を呼び出せるようにする。Lua 側は `print` するだけで、まだ描画 API は無い。

**Files:**
- Create: `src/lua_api.h`, `src/lua_api.c`
- Create: `samples/00_hello.lua`
- Modify: `src/app.h`, `src/app.c`
- Modify: `src/main.c`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: src/lua_api.h を作成**

```c
#pragma once
#include <lua.h>
#include <SDL3/SDL.h>

typedef struct LuaCtx {
    lua_State *L;
} LuaCtx;

bool lua_ctx_init(LuaCtx *ctx, const char *script_path);
void lua_ctx_call_init(LuaCtx *ctx);
void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e);
void lua_ctx_call_frame(LuaCtx *ctx);
void lua_ctx_call_quit(LuaCtx *ctx);
void lua_ctx_shutdown(LuaCtx *ctx);
```

- [ ] **Step 2: src/lua_api.c を作成**

```c
#include "lua_api.h"
#include <lua.h>
#include <lualib.h>
#include <lauxlib.h>
#include <SDL3/SDL.h>

static void push_event_table(lua_State *L, const SDL_Event *e) {
    lua_newtable(L);
    lua_pushinteger(L, e->type);
    lua_setfield(L, -2, "type");
    // Task 後段で詳細フィールドを足していく。今は type だけ。
}

static void call_global_if_present(lua_State *L, const char *name, int nargs) {
    lua_getglobal(L, name);
    if (!lua_isfunction(L, -1)) {
        lua_pop(L, 1 + nargs);
        return;
    }
    // 引数を関数の上に持ってくる
    if (nargs > 0) {
        lua_insert(L, -1 - nargs);
    }
    if (lua_pcall(L, nargs, 0, 0) != LUA_OK) {
        SDL_Log("lua error in %s: %s", name, lua_tostring(L, -1));
        lua_pop(L, 1);
    }
}

bool lua_ctx_init(LuaCtx *ctx, const char *script_path) {
    ctx->L = luaL_newstate();
    luaL_openlibs(ctx->L);
    if (luaL_dofile(ctx->L, script_path) != LUA_OK) {
        SDL_Log("lua load error: %s", lua_tostring(ctx->L, -1));
        return false;
    }
    return true;
}

void lua_ctx_call_init(LuaCtx *ctx) { call_global_if_present(ctx->L, "on_init", 0); }
void lua_ctx_call_frame(LuaCtx *ctx) { call_global_if_present(ctx->L, "on_frame", 0); }
void lua_ctx_call_quit(LuaCtx *ctx) { call_global_if_present(ctx->L, "on_quit", 0); }

void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e) {
    push_event_table(ctx->L, e);
    call_global_if_present(ctx->L, "on_event", 1);
}

void lua_ctx_shutdown(LuaCtx *ctx) {
    if (ctx->L) lua_close(ctx->L);
    ctx->L = NULL;
}
```

- [ ] **Step 3: app.h / app.c に LuaCtx を埋め込む**

```c
// app.h: App 構造体に追加
LuaCtx lua;

// app.c: app_init の最後で
// (script_path は main から渡す。簡単のため固定 "samples/00_hello.lua"。argv 対応は Task 後段)
```

- [ ] **Step 4: src/main.c を更新**

```c
SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
    (void)appstate;
    SDL_Init(SDL_INIT_VIDEO);
    if (!app_init(&g_app)) return SDL_APP_FAILURE;
    const char *script = (argc >= 2) ? argv[1] : "samples/00_hello.lua";
    if (!lua_ctx_init(&g_app.lua, script)) return SDL_APP_FAILURE;
    lua_ctx_call_init(&g_app.lua);
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
    (void)appstate;
    if (event->type == SDL_EVENT_QUIT) return SDL_APP_SUCCESS;
    lua_ctx_call_event(&g_app.lua, event);
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
    (void)appstate;
    int w, h;
    app_frame_begin(&g_app, &w, &h);
    // 既存の swapchain clear はそのまま残す (Lua が begin_pass を持つまでの暫定)
    sg_pass pass = {
        .action.colors[0] = { .load_action = SG_LOADACTION_CLEAR,
                              .clear_value = {0.1f, 0.15f, 0.25f, 1.0f} },
        .swapchain = { .width = w, .height = h,
                       .color_format = SG_PIXELFORMAT_RGBA8,
                       .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
                       .sample_count = 1, .gl.framebuffer = 0 },
    };
    sg_begin_pass(&pass);
    sg_end_pass();
    lua_ctx_call_frame(&g_app.lua);
    app_frame_end(&g_app);
    return SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
    (void)appstate; (void)result;
    lua_ctx_call_quit(&g_app.lua);
    lua_ctx_shutdown(&g_app.lua);
    app_shutdown(&g_app);
    SDL_Quit();
}
```

- [ ] **Step 5: samples/00_hello.lua を作成**

```lua
function on_init()
  print("[lua] on_init")
end
function on_event(e)
  if e.type then
    -- noisy なので有効化は必要時に
  end
end
function on_frame()
  -- 1秒ごとに print したい場合は frame counter を持つ
end
function on_quit()
  print("[lua] on_quit")
end
```

- [ ] **Step 6: CMakeLists.txt に lua_api.c 追加**

```cmake
add_executable(sglua
  src/main.c
  src/app.c
  src/lua_api.c
)
```

- [ ] **Step 7: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/00_hello.lua
```

期待: ターミナルに `[lua] on_init` が出る。ウィンドウを閉じると `[lua] on_quit` が出る。画面はまだ青クリアのまま。

- [ ] **Step 8: コミット**

```bash
git add src/lua_api.h src/lua_api.c src/app.h src/app.c src/main.c samples/00_hello.lua CMakeLists.txt
git commit -m "feat: load lua script and dispatch on_init/event/frame/quit callbacks"
```

---

## Task 4: 列挙値定義と main_tex グローバル

**Goal:** Lua からアクセスできるグローバル定数 (`VERTEX`, `RGBA8`, `CLEAR`, `NONE`, `BACK`, `TRIANGLES` など) と、グローバル `main_tex` (swapchain を表す sentinel) を導入する。

**Files:**
- Create: `src/enums.h`
- Create: `src/enums_lua.c`
- Modify: `src/lua_api.c`
- Modify: `samples/00_hello.lua`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: src/enums.h を作成**

仕様書の「列挙値」を C 側 enum にする。Lua 側は単に整数として保持する (light userdata でも良いが PoC では integer で十分)。

```c
#pragma once

typedef enum {
    SGL_BUFFER_VERTEX = 1,
    SGL_BUFFER_INDEX,
    SGL_BUFFER_UNIFORM,
    SGL_BUFFER_STORAGE,
} SglBufferType;

typedef enum {
    SGL_PF_RGBA8 = 1,
    SGL_PF_R8,
    SGL_PF_RG8,
    SGL_PF_RGBA16F,
    SGL_PF_RGBA32F,
    SGL_PF_DEPTH16,
    SGL_PF_DEPTH24_STENCIL8,
    SGL_PF_DEPTH32F,
} SglPixelFormat;

typedef enum { SGL_LOAD_CLEAR = 1, SGL_LOAD_LOAD, SGL_LOAD_DONTCARE } SglLoadAction;
typedef enum { SGL_STORE_STORE = 1, SGL_STORE_DONTCARE } SglStoreAction;
typedef enum { SGL_BLEND_NONE = 1, SGL_BLEND_ALPHA, SGL_BLEND_ADDITIVE, SGL_BLEND_MULTIPLY } SglBlend;
typedef enum { SGL_CULL_NONE = 1, SGL_CULL_BACK, SGL_CULL_FRONT } SglCull;
typedef enum {
    SGL_PRIM_TRIANGLES = 1, SGL_PRIM_TRIANGLE_STRIP,
    SGL_PRIM_LINES, SGL_PRIM_LINE_STRIP, SGL_PRIM_POINTS,
} SglPrimitive;
```

- [ ] **Step 2: src/enums_lua.c を作成**

```c
#include "enums.h"
#include <lua.h>
#include <lauxlib.h>

static void set_int(lua_State *L, const char *name, int v) {
    lua_pushinteger(L, v);
    lua_setglobal(L, name);
}

void enums_register(lua_State *L) {
    // Buffer types
    set_int(L, "VERTEX", SGL_BUFFER_VERTEX);
    set_int(L, "INDEX", SGL_BUFFER_INDEX);
    set_int(L, "UNIFORM", SGL_BUFFER_UNIFORM);
    set_int(L, "STORAGE", SGL_BUFFER_STORAGE);
    // Pixel formats
    set_int(L, "RGBA8", SGL_PF_RGBA8);
    set_int(L, "R8", SGL_PF_R8);
    set_int(L, "RG8", SGL_PF_RG8);
    set_int(L, "RGBA16F", SGL_PF_RGBA16F);
    set_int(L, "RGBA32F", SGL_PF_RGBA32F);
    set_int(L, "DEPTH16", SGL_PF_DEPTH16);
    set_int(L, "DEPTH24_STENCIL8", SGL_PF_DEPTH24_STENCIL8);
    set_int(L, "DEPTH32F", SGL_PF_DEPTH32F);
    // Load/store
    set_int(L, "CLEAR", SGL_LOAD_CLEAR);
    set_int(L, "LOAD", SGL_LOAD_LOAD);
    set_int(L, "DONTCARE", SGL_LOAD_DONTCARE);
    set_int(L, "STORE", SGL_STORE_STORE);
    // Blend / Cull
    set_int(L, "NONE", SGL_BLEND_NONE); // CULL_NONE と値は別だが Lua 上は同名で上書きされる。
    // → 仕様書では NONE が両方で使われている。pipeline 設定時に文脈で判別する。
    set_int(L, "ALPHA", SGL_BLEND_ALPHA);
    set_int(L, "ADDITIVE", SGL_BLEND_ADDITIVE);
    set_int(L, "MULTIPLY", SGL_BLEND_MULTIPLY);
    set_int(L, "BACK", SGL_CULL_BACK);
    set_int(L, "FRONT", SGL_CULL_FRONT);
    // Primitive
    set_int(L, "TRIANGLES", SGL_PRIM_TRIANGLES);
    set_int(L, "TRIANGLE_STRIP", SGL_PRIM_TRIANGLE_STRIP);
    set_int(L, "LINES", SGL_PRIM_LINES);
    set_int(L, "LINE_STRIP", SGL_PRIM_LINE_STRIP);
    set_int(L, "POINTS", SGL_PRIM_POINTS);
}
```

注: 仕様書で `NONE` は blend と cull の両方に使われる。両者を 1 つの整数にまとめると pipeline 構築時にどちらの NONE か文脈で判別する必要がある。PoC では `SGL_BLEND_NONE` の値をそのまま `NONE` として登録し、pipeline 構築側で `cull` フィールドの位置に来た `SGL_BLEND_NONE` を `SG_CULLMODE_NONE` として解釈する暫定処理にする。値の衝突は将来 `BLEND_NONE` / `CULL_NONE` に分けて解消する (Open Question)。

- [ ] **Step 3: main_tex を Lua に push する関数を追加**

`src/lua_api.h` に宣言を増やす:

```c
void lua_api_register(lua_State *L);
```

`src/lua_api.c` に実装:

```c
#include "enums.h"

extern void enums_register(lua_State *L);

void lua_api_register(lua_State *L) {
    enums_register(L);
    // main_tex は { __sgl_kind="main_tex" } という sentinel テーブルにする。
    // Texture と同じインターフェイスとして扱える。
    lua_newtable(L);
    lua_pushstring(L, "main_tex");
    lua_setfield(L, -2, "__sgl_kind");
    lua_setglobal(L, "main_tex");
}
```

`lua_ctx_init` の最後で `lua_api_register(ctx->L)` を呼ぶ。ただしスクリプトを `luaL_dofile` する前に呼ぶこと (グローバルが必要)。順序を:

```c
bool lua_ctx_init(LuaCtx *ctx, const char *path) {
    ctx->L = luaL_newstate();
    luaL_openlibs(ctx->L);
    lua_api_register(ctx->L);  // <- dofile より先に
    if (luaL_dofile(ctx->L, path) != LUA_OK) { ... }
    return true;
}
```

- [ ] **Step 4: samples/00_hello.lua を更新して enum をログ出力**

```lua
function on_init()
  print("[lua] on_init")
  print("VERTEX=", VERTEX, "RGBA8=", RGBA8, "CLEAR=", CLEAR)
  if main_tex and main_tex.__sgl_kind == "main_tex" then
    print("main_tex is registered")
  end
end
```

- [ ] **Step 5: CMakeLists.txt に enums_lua.c を追加**

```cmake
add_executable(sglua
  src/main.c
  src/app.c
  src/lua_api.c
  src/enums_lua.c
)
```

- [ ] **Step 6: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/00_hello.lua
```

期待: ターミナルに `VERTEX= 1 RGBA8= 1 CLEAR= 1` と `main_tex is registered` が出る。

- [ ] **Step 7: コミット**

```bash
git add src/enums.h src/enums_lua.c src/lua_api.h src/lua_api.c samples/00_hello.lua CMakeLists.txt
git commit -m "feat: register sgl enums and main_tex sentinel as lua globals"
```

---

## Task 5: pass 制御 (begin_pass / end_pass) と clear_color

**Goal:** Lua から `begin_pass({ target = main_tex, clear_color = {r,g,b,a} })` と `end_pass()` を呼べるようにし、画面の clear color を Lua で変更できることを確認する。

**Files:**
- Create: `src/pass.h`, `src/pass.c`
- Modify: `src/app.h`, `src/app.c`, `src/main.c`
- Modify: `src/lua_api.c`
- Create: `samples/00b_clear.lua`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: src/pass.h を作成**

```c
#pragma once
#include "sokol_gfx.h"
#include <stdbool.h>

typedef struct PassState {
    bool in_pass;
    int swapchain_w, swapchain_h;
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_swapchain_size(PassState *p, int w, int h);
bool pass_state_in_pass(const PassState *p);
void pass_state_begin_main(PassState *p, float r, float g, float b, float a);
void pass_state_end(PassState *p);
```

- [ ] **Step 2: src/pass.c を作成**

```c
#include "pass.h"
#include <SDL3/SDL.h>

void pass_state_init(PassState *p) {
    p->in_pass = false;
    p->swapchain_w = p->swapchain_h = 0;
}
void pass_state_set_swapchain_size(PassState *p, int w, int h) {
    p->swapchain_w = w; p->swapchain_h = h;
}
bool pass_state_in_pass(const PassState *p) { return p->in_pass; }

void pass_state_begin_main(PassState *p, float r, float g, float b, float a) {
    if (p->in_pass) {
        SDL_Log("begin_pass nested (not supported)"); return;
    }
    sg_pass pass = {
        .action.colors[0] = {
            .load_action = SG_LOADACTION_CLEAR,
            .clear_value = {r,g,b,a},
        },
        .swapchain = {
            .width = p->swapchain_w, .height = p->swapchain_h,
            .color_format = SG_PIXELFORMAT_RGBA8,
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
            .gl.framebuffer = 0,
        },
    };
    sg_begin_pass(&pass);
    p->in_pass = true;
}

void pass_state_end(PassState *p) {
    if (!p->in_pass) { SDL_Log("end_pass without begin"); return; }
    sg_end_pass();
    p->in_pass = false;
}
```

- [ ] **Step 3: App 構造体に PassState を埋め込む**

`src/app.h`:

```c
#include "pass.h"
typedef struct App {
    SDL_Window *window;
    SDL_GLContext gl_ctx;
    LuaCtx lua;
    PassState pass;
    int frame_index;
} App;
```

`src/app.c` の `app_init` で `pass_state_init(&app->pass)`、`app_frame_begin` で `pass_state_set_swapchain_size(&app->pass, w, h)` を呼ぶ。

- [ ] **Step 4: lua_api に begin_pass / end_pass を bind**

`src/lua_api.c` で App ポインタを受け取れるように、グローバル変数 `static App *g_app_for_lua;` を保持するか、`lua_State` の registry に格納する。PoC では simpler な前者で進める。`lua_api.h` を修正:

```c
struct App; // 前方宣言
bool lua_ctx_init(LuaCtx *ctx, const char *script_path, struct App *app);
```

`src/lua_api.c`:

```c
#include "app.h"

static App *g_app_for_lua;

static int l_begin_pass(lua_State *L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    // target は main_tex のみ対応 (Task 後段で texture target を追加)
    lua_getfield(L, 1, "target");
    if (lua_isnil(L, -1)) return luaL_error(L, "begin_pass: target required");
    // main_tex sentinel チェック
    int is_main = 0;
    if (lua_istable(L, -1)) {
        lua_getfield(L, -1, "__sgl_kind");
        if (lua_isstring(L, -1) && strcmp(lua_tostring(L, -1), "main_tex") == 0) is_main = 1;
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    if (!is_main) return luaL_error(L, "begin_pass: only main_tex supported in PoC");

    float r=0,g=0,b=0,a=1;
    lua_getfield(L, 1, "clear_color");
    if (lua_istable(L, -1)) {
        lua_geti(L, -1, 1); r = lua_tonumber(L, -1); lua_pop(L, 1);
        lua_geti(L, -1, 2); g = lua_tonumber(L, -1); lua_pop(L, 1);
        lua_geti(L, -1, 3); b = lua_tonumber(L, -1); lua_pop(L, 1);
        lua_geti(L, -1, 4); a = lua_tonumber(L, -1); lua_pop(L, 1);
    }
    lua_pop(L, 1);

    pass_state_begin_main(&g_app_for_lua->pass, r, g, b, a);
    return 0;
}

static int l_end_pass(lua_State *L) {
    (void)L;
    pass_state_end(&g_app_for_lua->pass);
    return 0;
}

void lua_api_register(lua_State *L) {
    enums_register(L);
    // main_tex sentinel
    lua_newtable(L);
    lua_pushstring(L, "main_tex"); lua_setfield(L, -2, "__sgl_kind");
    lua_setglobal(L, "main_tex");
    // 関数登録
    lua_pushcfunction(L, l_begin_pass); lua_setglobal(L, "begin_pass");
    lua_pushcfunction(L, l_end_pass); lua_setglobal(L, "end_pass");
}

bool lua_ctx_init(LuaCtx *ctx, const char *path, App *app) {
    g_app_for_lua = app;
    ctx->L = luaL_newstate();
    luaL_openlibs(ctx->L);
    lua_api_register(ctx->L);
    if (luaL_dofile(ctx->L, path) != LUA_OK) {
        SDL_Log("lua load: %s", lua_tostring(ctx->L, -1));
        return false;
    }
    return true;
}
```

`#include <string.h>` を忘れずに。

- [ ] **Step 5: main.c の SDL_AppIterate から既存の暫定 clear を削除**

Lua の on_frame 内で begin_pass / end_pass が呼ばれるので、main.c から固定 clear は消す:

```c
SDL_AppResult SDL_AppIterate(void *appstate) {
    (void)appstate;
    int w, h;
    app_frame_begin(&g_app, &w, &h);
    lua_ctx_call_frame(&g_app.lua);
    // もし on_frame が pass を閉じ忘れていたら強制的に閉じる (debug 安全策)
    if (pass_state_in_pass(&g_app.pass)) pass_state_end(&g_app.pass);
    app_frame_end(&g_app);
    return SDL_APP_CONTINUE;
}
```

`SDL_AppInit` の `lua_ctx_init` 呼び出しを `lua_ctx_init(&g_app.lua, script, &g_app)` に変更。

- [ ] **Step 6: samples/00b_clear.lua を作成**

```lua
local t = 0
function on_init() print("clear demo") end
function on_event(e) end
function on_quit() end
function on_frame()
  t = t + 1/60
  local r = 0.5 + 0.5 * math.sin(t)
  local g = 0.5 + 0.5 * math.sin(t + 2.0)
  local b = 0.5 + 0.5 * math.sin(t + 4.0)
  begin_pass({ target = main_tex, clear_color = {r, g, b, 1} })
  end_pass()
end
```

- [ ] **Step 7: CMakeLists.txt に pass.c 追加**

```cmake
add_executable(sglua
  src/main.c src/app.c src/lua_api.c src/enums_lua.c src/pass.c
)
```

- [ ] **Step 8: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/00b_clear.lua
```

期待: ウィンドウの背景色が虹色にゆっくり変化する。

- [ ] **Step 9: コミット**

```bash
git add src/pass.h src/pass.c src/app.h src/app.c src/main.c src/lua_api.c src/lua_api.h samples/00b_clear.lua CMakeLists.txt
git commit -m "feat: lua-driven begin_pass/end_pass clearing main_tex"
```

---

## Task 6: リソース key map と use_buffer

**Goal:** Lua から `use_buffer(key, VERTEX, data, version)` を呼ぶと sokol_gfx の `sg_buffer` が作られ、同じ key + 同じ version での再呼び出しでは upload を skip し、version が変わると再 upload される。返り値は BufferRef。

**Files:**
- Create: `src/resources.h`, `src/resources.c`
- Modify: `src/lua_api.c`
- Modify: `src/app.h`, `src/app.c`
- Create: `samples/00c_buffer.lua`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: src/resources.h を作成**

```c
#pragma once
#include "sokol_gfx.h"
#include "enums.h"
#include <stdbool.h>
#include <stdint.h>

typedef enum { RES_NONE=0, RES_BUFFER, RES_TEXTURE, RES_SHADER } ResKind;

typedef struct ResEntry {
    char *key;            // strdup'd
    ResKind kind;
    int version;
    int last_seen_frame;
    union {
        struct { sg_buffer h; SglBufferType type; size_t size_bytes; } buf;
        struct { sg_image h; sg_sampler smp; int w, h_; SglPixelFormat fmt; } tex;
        struct { sg_shader h; void *reflection; } sh; // reflection は Task 7 で定義
    } u;
    struct ResEntry *next; // chain
} ResEntry;

typedef struct ResTable {
    ResEntry *buckets[256]; // open hashing, size_pow2 = 256
} ResTable;

void res_table_init(ResTable *t);
void res_table_shutdown(ResTable *t);

ResEntry *res_table_get(ResTable *t, const char *key);
ResEntry *res_table_get_or_create(ResTable *t, const char *key, ResKind kind);
void res_table_touch(ResEntry *e, int frame_index);
```

- [ ] **Step 2: src/resources.c を作成**

```c
#include "resources.h"
#include <string.h>
#include <stdlib.h>

static uint32_t hash_str(const char *s) {
    uint32_t h = 2166136261u;
    while (*s) { h ^= (uint8_t)*s++; h *= 16777619u; }
    return h;
}

void res_table_init(ResTable *t) { memset(t, 0, sizeof(*t)); }

void res_table_shutdown(ResTable *t) {
    for (int i = 0; i < 256; ++i) {
        ResEntry *e = t->buckets[i];
        while (e) {
            ResEntry *n = e->next;
            switch (e->kind) {
                case RES_BUFFER: sg_destroy_buffer(e->u.buf.h); break;
                case RES_TEXTURE:
                    sg_destroy_image(e->u.tex.h);
                    sg_destroy_sampler(e->u.tex.smp);
                    break;
                case RES_SHADER:
                    sg_destroy_shader(e->u.sh.h);
                    free(e->u.sh.reflection);
                    break;
                default: break;
            }
            free(e->key);
            free(e);
            e = n;
        }
        t->buckets[i] = NULL;
    }
}

ResEntry *res_table_get(ResTable *t, const char *key) {
    uint32_t i = hash_str(key) & 0xff;
    for (ResEntry *e = t->buckets[i]; e; e = e->next) {
        if (strcmp(e->key, key) == 0) return e;
    }
    return NULL;
}

ResEntry *res_table_get_or_create(ResTable *t, const char *key, ResKind kind) {
    ResEntry *e = res_table_get(t, key);
    if (e) {
        if (e->kind != RES_NONE && e->kind != kind) return NULL; // 種別衝突
        return e;
    }
    uint32_t i = hash_str(key) & 0xff;
    e = (ResEntry*)calloc(1, sizeof(ResEntry));
    e->key = strdup(key);
    e->kind = kind;
    e->version = -1;
    e->last_seen_frame = -1;
    e->next = t->buckets[i];
    t->buckets[i] = e;
    return e;
}

void res_table_touch(ResEntry *e, int f) { e->last_seen_frame = f; }
```

- [ ] **Step 3: App に ResTable を埋め込む**

```c
// app.h
#include "resources.h"
typedef struct App {
    SDL_Window *window;
    SDL_GLContext gl_ctx;
    LuaCtx lua;
    PassState pass;
    ResTable res;
    int frame_index;
} App;
```

`app_init` で `res_table_init(&app->res)`、`app_shutdown` で `res_table_shutdown(&app->res)`。**重要**: `res_table_shutdown` は `sg_destroy_*` を呼ぶので `sg_shutdown` の前に呼ぶ。`app_shutdown` の順序を「res → sg → gl → window」にする。

- [ ] **Step 4: l_use_buffer を実装**

`src/lua_api.c` に追加。data は Lua table (numeric array of float) を想定 (PoC ではこれだけ; string 受けは将来)。

```c
static int l_use_buffer(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    int type = (int)luaL_checkinteger(L, 2);
    luaL_checktype(L, 3, LUA_TTABLE);
    int version = (int)luaL_checkinteger(L, 4);

    if (type != SGL_BUFFER_VERTEX && type != SGL_BUFFER_INDEX) {
        return luaL_error(L, "use_buffer: only VERTEX/INDEX in PoC");
    }

    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_BUFFER);
    if (!e) return luaL_error(L, "use_buffer: key %s already used as different kind", key);

    res_table_touch(e, g_app_for_lua->frame_index);

    if (e->version == version && e->u.buf.h.id != 0) {
        // upload skip。BufferRef を返す
        lua_newtable(L);
        lua_pushstring(L, "buffer"); lua_setfield(L, -2, "__sgl_kind");
        lua_pushstring(L, key);      lua_setfield(L, -2, "key");
        return 1;
    }

    int n = (int)lua_rawlen(L, 3);
    if (n <= 0) return luaL_error(L, "use_buffer: empty data");
    float *data = (float*)malloc(sizeof(float) * n);
    for (int i = 0; i < n; ++i) {
        lua_geti(L, 3, i + 1);
        data[i] = (float)lua_tonumber(L, -1);
        lua_pop(L, 1);
    }

    if (e->u.buf.h.id != 0) sg_destroy_buffer(e->u.buf.h);
    e->u.buf.h = sg_make_buffer(&(sg_buffer_desc){
        .size = (size_t)n * sizeof(float),
        .type = (type == SGL_BUFFER_INDEX) ? SG_BUFFERTYPE_INDEXBUFFER : SG_BUFFERTYPE_VERTEXBUFFER,
        .usage = SG_USAGE_IMMUTABLE,
        .data = { .ptr = data, .size = (size_t)n * sizeof(float) },
    });
    e->u.buf.type = (SglBufferType)type;
    e->u.buf.size_bytes = (size_t)n * sizeof(float);
    e->version = version;
    free(data);

    lua_newtable(L);
    lua_pushstring(L, "buffer"); lua_setfield(L, -2, "__sgl_kind");
    lua_pushstring(L, key);      lua_setfield(L, -2, "key");
    return 1;
}

// lua_api_register に追加
lua_pushcfunction(L, l_use_buffer); lua_setglobal(L, "use_buffer");
```

- [ ] **Step 5: samples/00c_buffer.lua を作成 (描画なし、ログのみ)**

```lua
local data = { 0,0.5,0,  -0.5,-0.5,0,  0.5,-0.5,0 }
function on_init() end
function on_event(e) end
function on_quit() end
function on_frame()
  local b = use_buffer("tri", VERTEX, data, 1)
  if b and b.__sgl_kind == "buffer" then
    -- 1秒に1回くらい確認したいなら frame counter で
  end
  begin_pass({ target = main_tex, clear_color = {0.1,0.1,0.2,1} })
  end_pass()
end
```

- [ ] **Step 6: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/00c_buffer.lua
```

期待: クラッシュせず青グレーのウィンドウ。Lua の `use_buffer` 呼び出しが毎フレーム成功し、初回のみ upload、以降は skip されている (内部状態; 表示としては変化なし)。Valgrind/asan を使うなら `cmake -DCMAKE_BUILD_TYPE=Debug` で再ビルドし `ASAN_OPTIONS=detect_leaks=1` で確認。

- [ ] **Step 7: コミット**

```bash
git add src/resources.h src/resources.c src/app.h src/app.c src/lua_api.c samples/00c_buffer.lua CMakeLists.txt
git commit -m "feat: declare gpu buffers via use_buffer with version-based upload skip"
```

---

## Task 7: Slang 統合と use_shader

**Goal:** Lua から `use_shader(key, vs_src, fs_src, version)` を呼ぶと、Slang ライブラリで GLSL 3.30 にクロスコンパイルされ、sg_shader が作られる。reflection から vertex attribute / uniform block / texture binding 情報を取り出し ResEntry に保存する。

**Files:**
- Create: `src/shader.h`, `src/shader.c`
- Modify: `src/resources.h` (reflection 構造体を追加)
- Modify: `src/lua_api.c`
- Create: `samples/00d_shader.lua`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: src/shader.h を作成**

```c
#pragma once
#include "sokol_gfx.h"
#include <stddef.h>
#include <stdbool.h>

#define SGL_MAX_ATTRS 8
#define SGL_MAX_UNIFORM_MEMBERS 32
#define SGL_MAX_TEXTURES 8

typedef struct ShaderAttr {
    char name[32];
    int slot;          // shader 入力の location
    int comp_count;    // 1..4
    int offset_floats; // vertex stride 内の float offset (Slang reflection から計算)
} ShaderAttr;

typedef struct ShaderUniformMember {
    char name[32];
    int offset_floats;
    int comp_count; // mat4 = 16, vec3 = 3 等
} ShaderUniformMember;

typedef struct ShaderUniformBlock {
    char name[32];        // Lua 側で `uniforms` キーで指定する block 名
    int slot;             // sokol uniform block slot
    int size_floats;
    int member_count;
    ShaderUniformMember members[SGL_MAX_UNIFORM_MEMBERS];
} ShaderUniformBlock;

typedef struct ShaderTexture {
    char name[32]; // Lua 側 resources の named キー
    int img_slot;
    int smp_slot;
} ShaderTexture;

typedef struct ShaderReflection {
    int attr_count; ShaderAttr attrs[SGL_MAX_ATTRS];
    int ub_count;   ShaderUniformBlock ubs[2]; // PoC では 1〜2 個まで
    int tex_count;  ShaderTexture texs[SGL_MAX_TEXTURES];
    int vertex_stride_floats; // 自動計算
} ShaderReflection;

bool shader_compile_and_create(
    const char *vs_src, const char *fs_src,
    sg_shader *out_shader, ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size);
```

- [ ] **Step 2: src/shader.c を作成**

Slang の C API を使って vs_src / fs_src を `SLANG_SOURCE_LANGUAGE_SLANG` として読み、`SLANG_GLSL` (profile `glsl_330`) に出力する。reflection は Slang の `IComponentType::getLayout()` から取得する。完全な API ドキュメントは https://shader-slang.com/slang/user-guide/compiling.html#using-the-compilation-api 参照。

PoC として最小実装の骨子:

```c
#include "shader.h"
#include <slang.h>
#include <slang-com-ptr.h>  // C++ ヘルパだが C で使うなら不要
#include <string.h>
#include <stdio.h>

bool shader_compile_and_create(
    const char *vs_src, const char *fs_src,
    sg_shader *out_shader, ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size)
{
    // 1. SlangSession 作成 (slang_createGlobalSession)
    // 2. SessionDesc に target = SLANG_GLSL, profile = "glsl_330"
    // 3. addCodeStringFromSource で vs_src / fs_src を 1 つの module として読む
    //    (実際は entry point 名で分ける: "vs_main" / "fs_main")
    // 4. composeProgramByEntryPoints で vs / fs entry points を集めた program を作る
    // 5. getEntryPointCode で stage 別 GLSL を取得
    // 6. getLayout() で reflection を取り、attrs / ubs / texs を埋める
    //    - vertex attributes: program reflection の vertex stage entry point の入力 parameters
    //    - uniform block: parameter category = ConstantBuffer
    //    - texture+sampler: category = ShaderResource / SamplerState
    // 7. sg_make_shader を呼ぶ:
    //    sg_shader_desc desc = {
    //      .vs = { .source = vs_glsl, .entry = "main", ... },
    //      .fs = { .source = fs_glsl, .entry = "main",
    //              .images[0] = { .image_type = SG_IMAGETYPE_2D, .sample_type = SG_IMAGESAMPLETYPE_FLOAT },
    //              .samplers[0] = { .sampler_type = SG_SAMPLERTYPE_FILTERING },
    //              .image_sampler_pairs[0] = { .image_slot=0, .sampler_slot=0, .glsl_name="diffuse" },
    //              .uniform_blocks[0] = { .size = ub_size, .layout = SG_UNIFORMLAYOUT_STD140, ... }
    //            },
    //      .attrs[0] = { .name = "position", .glsl_name = "position" }, ...
    //    };
    //    *out_shader = sg_make_shader(&desc);
    // 8. 失敗時は err_buf に diagnostics を書いて return false。

    // 上記すべてを実装する。Slang diagnostics は ISlangBlob から取り出す。

    // -------- 完全実装は大きいので、本タスクで Slang セッション初期化〜vs_main/fs_main の
    // GLSL 取得+ sg_make_shader 成功 までを 1 セッション内で書ききる。Reflection も
    // 同じセッション内で抽出する。
    snprintf(err_buf, err_buf_size, "shader_compile_and_create not implemented");
    return false; // 実装後、この行を消す
}
```

実装メモ:
- Slang の C API (`slang.h` の `SlangSession` / `SlangCompileRequest`) は古い API。新しい API (`SlangGlobalSession::createSession` 等) は C++ 寄りだが、`slang.h` 経由で C から呼べる関数 `spCreateSession` / `spAddTranslationUnit` / `spAddTranslationUnitSourceString` / `spCompile` / `spGetEntryPointSource` / `spGetReflection` が揃っている。これらを使う。
- entry point は user 側で `[shader("vertex")] void vs_main(...)` / `[shader("fragment")] void fs_main(...)` と書いてもらう想定。
- vertex attributes の semantic は `: POSITION0` 等は Slang/HLSL 流。Slang で attribute のバインドは `[[vk::location(0)]]` または auto。GLSL には `layout(location=N)` で出る。reflection から location を取れる。

- [ ] **Step 3: src/resources.h の `RES_SHADER` union に reflection を含める**

```c
struct { sg_shader h; ShaderReflection refl; } sh;
```

(`reflection` は heap でなく struct 直埋めに変更。Task 6 の `void *reflection` は削除し、shutdown 側も `free(e->u.sh.reflection)` を消す。)

`#include "shader.h"` を resources.h に追加。

- [ ] **Step 4: l_use_shader を実装**

```c
#include "shader.h"

static int l_use_shader(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    const char *vs  = luaL_checkstring(L, 2);
    const char *fs  = luaL_checkstring(L, 3);
    int version = (int)luaL_checkinteger(L, 4);

    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_SHADER);
    if (!e) return luaL_error(L, "use_shader: key conflict");
    res_table_touch(e, g_app_for_lua->frame_index);

    if (e->version == version && e->u.sh.h.id != 0) {
        lua_newtable(L);
        lua_pushstring(L, "shader"); lua_setfield(L, -2, "__sgl_kind");
        lua_pushstring(L, key);      lua_setfield(L, -2, "key");
        return 1;
    }

    char err[1024];
    sg_shader sh;
    ShaderReflection refl;
    if (!shader_compile_and_create(vs, fs, &sh, &refl, err, sizeof(err))) {
        return luaL_error(L, "shader compile error: %s", err);
    }
    if (e->u.sh.h.id != 0) sg_destroy_shader(e->u.sh.h);
    e->u.sh.h = sh;
    e->u.sh.refl = refl;
    e->version = version;

    lua_newtable(L);
    lua_pushstring(L, "shader"); lua_setfield(L, -2, "__sgl_kind");
    lua_pushstring(L, key);      lua_setfield(L, -2, "key");
    return 1;
}

// register
lua_pushcfunction(L, l_use_shader); lua_setglobal(L, "use_shader");
```

- [ ] **Step 5: samples/00d_shader.lua を作成 (compile 確認のみ)**

```lua
local vs = [[
[shader("vertex")]
struct VSIn { float3 pos : POSITION; };
struct VSOut { float4 pos : SV_Position; };
VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }
]]
local fs = [[
[shader("fragment")]
float4 fs_main() : SV_Target { return float4(1, 0.5, 0, 1); }
]]
function on_init()
  local s = use_shader("test", vs, fs, 1)
  print("shader compiled:", s.key)
end
function on_event(e) end
function on_quit() end
function on_frame()
  begin_pass({ target = main_tex, clear_color = {0.1,0.1,0.2,1} })
  end_pass()
end
```

- [ ] **Step 6: CMakeLists.txt 更新**

```cmake
add_executable(sglua
  src/main.c src/app.c src/lua_api.c src/enums_lua.c
  src/pass.c src/resources.c src/shader.c
)
```

- [ ] **Step 7: build & 動作確認**

```bash
LD_LIBRARY_PATH=third_party/slang/lib cmake --build build -j
LD_LIBRARY_PATH=third_party/slang/lib ./build/sglua samples/00d_shader.lua
```

期待: 起動時に `shader compiled: test` が出る。compile 失敗時は `lua error in on_init: shader compile error: ...` というメッセージが出る。

CMake で `target_link_options(sglua PRIVATE "-Wl,-rpath,$ORIGIN/../third_party/slang/lib")` を追加すると `LD_LIBRARY_PATH` 不要にできる。

- [ ] **Step 8: コミット**

```bash
git add src/shader.h src/shader.c src/resources.h src/resources.c src/lua_api.c samples/00d_shader.lua CMakeLists.txt
git commit -m "feat: integrate slang to compile vs/fs and create sg_shader with reflection"
```

---

## Task 8: pipeline cache と draw + Sample 1 (単色三角形)

**Goal:** `draw(count, resources, options)` を実装し、`samples/01_triangle.lua` でオレンジ色の三角形が画面に出る。pipeline は (shader, blend, depth, depth_write, cull, primitive, target_fmts) でハッシュキャッシュ。resources は **named版** (`{ verts = buf }`) のみ実装する (位置引数版は Open Question)。

**Files:**
- Create: `src/pipeline.h`, `src/pipeline.c`
- Modify: `src/lua_api.c`, `src/app.h`, `src/app.c`
- Create: `samples/01_triangle.lua`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: src/pipeline.h を作成**

```c
#pragma once
#include "sokol_gfx.h"
#include "shader.h"
#include "enums.h"

typedef struct PipelineKey {
    uint32_t shader_id;
    uint8_t blend, depth, depth_write, cull, primitive;
    uint8_t color_fmt; // SG_PIXELFORMAT_*
    uint8_t depth_fmt;
} PipelineKey;

typedef struct PipelineEntry {
    PipelineKey key;
    sg_pipeline pip;
    struct PipelineEntry *next;
} PipelineEntry;

typedef struct PipelineCache {
    PipelineEntry *buckets[64];
} PipelineCache;

void pipeline_cache_init(PipelineCache *c);
void pipeline_cache_shutdown(PipelineCache *c);
sg_pipeline pipeline_cache_get(
    PipelineCache *c,
    sg_shader sh, const ShaderReflection *refl,
    SglBlend blend, bool depth_test, bool depth_write,
    SglCull cull, SglPrimitive prim,
    sg_pixel_format color_fmt, sg_pixel_format depth_fmt);
```

- [ ] **Step 2: src/pipeline.c を作成**

```c
#include "pipeline.h"
#include <string.h>
#include <stdlib.h>

void pipeline_cache_init(PipelineCache *c) { memset(c, 0, sizeof(*c)); }

void pipeline_cache_shutdown(PipelineCache *c) {
    for (int i = 0; i < 64; ++i) {
        PipelineEntry *e = c->buckets[i];
        while (e) {
            PipelineEntry *n = e->next;
            sg_destroy_pipeline(e->pip);
            free(e); e = n;
        }
        c->buckets[i] = NULL;
    }
}

static uint32_t hash_key(const PipelineKey *k) {
    const uint8_t *p = (const uint8_t*)k;
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < sizeof(*k); ++i) { h ^= p[i]; h *= 16777619u; }
    return h;
}

static sg_blend_state to_sokol_blend(SglBlend b) {
    switch (b) {
        case SGL_BLEND_ALPHA: return (sg_blend_state){
            .enabled = true,
            .src_factor_rgb = SG_BLENDFACTOR_SRC_ALPHA,
            .dst_factor_rgb = SG_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
            .src_factor_alpha = SG_BLENDFACTOR_ONE,
            .dst_factor_alpha = SG_BLENDFACTOR_ONE_MINUS_SRC_ALPHA, };
        case SGL_BLEND_ADDITIVE: return (sg_blend_state){
            .enabled = true,
            .src_factor_rgb = SG_BLENDFACTOR_ONE,
            .dst_factor_rgb = SG_BLENDFACTOR_ONE, };
        case SGL_BLEND_MULTIPLY: return (sg_blend_state){
            .enabled = true,
            .src_factor_rgb = SG_BLENDFACTOR_DST_COLOR,
            .dst_factor_rgb = SG_BLENDFACTOR_ZERO, };
        default: return (sg_blend_state){0};
    }
}

sg_pipeline pipeline_cache_get(
    PipelineCache *c, sg_shader sh, const ShaderReflection *refl,
    SglBlend blend, bool dt, bool dw, SglCull cull, SglPrimitive prim,
    sg_pixel_format cfmt, sg_pixel_format dfmt)
{
    PipelineKey k = {
        .shader_id = sh.id,
        .blend = (uint8_t)blend, .depth = dt, .depth_write = dw,
        .cull = (uint8_t)cull, .primitive = (uint8_t)prim,
        .color_fmt = (uint8_t)cfmt, .depth_fmt = (uint8_t)dfmt,
    };
    uint32_t bi = hash_key(&k) & 63;
    for (PipelineEntry *e = c->buckets[bi]; e; e = e->next) {
        if (memcmp(&e->key, &k, sizeof(k)) == 0) return e->pip;
    }

    sg_pipeline_desc desc = {
        .shader = sh,
        .colors[0] = { .pixel_format = cfmt, .blend = to_sokol_blend(blend) },
        .depth = {
            .pixel_format = dfmt,
            .compare = dt ? SG_COMPAREFUNC_LESS_EQUAL : SG_COMPAREFUNC_ALWAYS,
            .write_enabled = dw,
        },
        .cull_mode = (cull == SGL_CULL_BACK) ? SG_CULLMODE_BACK
                  : (cull == SGL_CULL_FRONT) ? SG_CULLMODE_FRONT : SG_CULLMODE_NONE,
        .primitive_type = (prim == SGL_PRIM_LINES) ? SG_PRIMITIVETYPE_LINES
                       : (prim == SGL_PRIM_LINE_STRIP) ? SG_PRIMITIVETYPE_LINE_STRIP
                       : (prim == SGL_PRIM_POINTS) ? SG_PRIMITIVETYPE_POINTS
                       : (prim == SGL_PRIM_TRIANGLE_STRIP) ? SG_PRIMITIVETYPE_TRIANGLE_STRIP
                       : SG_PRIMITIVETYPE_TRIANGLES,
    };
    // Vertex layout を reflection から構築
    int float_offset = 0;
    for (int i = 0; i < refl->attr_count; ++i) {
        sg_vertex_format fmt;
        switch (refl->attrs[i].comp_count) {
            case 1: fmt = SG_VERTEXFORMAT_FLOAT; break;
            case 2: fmt = SG_VERTEXFORMAT_FLOAT2; break;
            case 3: fmt = SG_VERTEXFORMAT_FLOAT3; break;
            case 4: fmt = SG_VERTEXFORMAT_FLOAT4; break;
            default: fmt = SG_VERTEXFORMAT_FLOAT3;
        }
        desc.layout.attrs[refl->attrs[i].slot] = (sg_vertex_attr_state){
            .buffer_index = 0,
            .offset = refl->attrs[i].offset_floats * sizeof(float),
            .format = fmt,
        };
    }
    desc.layout.buffers[0].stride = refl->vertex_stride_floats * sizeof(float);

    sg_pipeline pip = sg_make_pipeline(&desc);
    PipelineEntry *e = (PipelineEntry*)calloc(1, sizeof(PipelineEntry));
    e->key = k; e->pip = pip; e->next = c->buckets[bi];
    c->buckets[bi] = e;
    return pip;
}
```

- [ ] **Step 3: App に PipelineCache を埋め込み + lifetime 管理**

```c
// app.h
#include "pipeline.h"
typedef struct App {
    SDL_Window *window; SDL_GLContext gl_ctx;
    LuaCtx lua; PassState pass;
    ResTable res; PipelineCache pip_cache;
    int frame_index;
} App;
```

`app_init` で `pipeline_cache_init(&app->pip_cache)`、`app_shutdown` で `res_table_shutdown` の前後どちらでも良いが pipeline は shader を参照しないので順序は緩い。`sg_shutdown` の前に呼ぶ。

- [ ] **Step 4: l_draw を実装**

```c
static int l_draw(lua_State *L) {
    if (!pass_state_in_pass(&g_app_for_lua->pass)) {
        return luaL_error(L, "draw: must be called inside begin_pass/end_pass");
    }
    int count = (int)luaL_checkinteger(L, 1);
    luaL_checktype(L, 2, LUA_TTABLE); // resources
    luaL_checktype(L, 3, LUA_TTABLE); // options

    // options.shader (BufferRef ではなく ShaderRef)
    lua_getfield(L, 3, "shader");
    if (!lua_istable(L, -1)) return luaL_error(L, "draw: options.shader required");
    lua_getfield(L, -1, "key");
    const char *shader_key = lua_tostring(L, -1);
    lua_pop(L, 2);
    ResEntry *sh_e = res_table_get(&g_app_for_lua->res, shader_key);
    if (!sh_e || sh_e->kind != RES_SHADER) return luaL_error(L, "draw: shader not found");

    // pipeline state options
    int blend = (int)SGL_BLEND_NONE;
    int cull  = (int)SGL_CULL_BACK;
    int prim  = (int)SGL_PRIM_TRIANGLES;
    bool depth_test = true, depth_write = true;
    lua_getfield(L, 3, "blend"); if (!lua_isnil(L, -1)) blend = (int)lua_tointeger(L, -1); lua_pop(L, 1);
    lua_getfield(L, 3, "cull");  if (!lua_isnil(L, -1)) cull  = (int)lua_tointeger(L, -1); lua_pop(L, 1);
    lua_getfield(L, 3, "primitive"); if (!lua_isnil(L, -1)) prim = (int)lua_tointeger(L, -1); lua_pop(L, 1);
    lua_getfield(L, 3, "depth"); if (!lua_isnil(L, -1)) depth_test = lua_toboolean(L, -1); lua_pop(L, 1);
    lua_getfield(L, 3, "depth_write"); if (!lua_isnil(L, -1)) depth_write = lua_toboolean(L, -1); lua_pop(L, 1);

    sg_pipeline pip = pipeline_cache_get(
        &g_app_for_lua->pip_cache, sh_e->u.sh.h, &sh_e->u.sh.refl,
        (SglBlend)blend, depth_test, depth_write, (SglCull)cull, (SglPrimitive)prim,
        SG_PIXELFORMAT_RGBA8, SG_PIXELFORMAT_DEPTH_STENCIL);
    sg_apply_pipeline(pip);

    // bindings: resources を named iteration で走査し、kind 別に処理
    sg_bindings bind = {0};
    lua_pushnil(L);
    while (lua_next(L, 2) != 0) {
        // stack: -2 = key, -1 = value
        if (lua_istable(L, -1)) {
            lua_getfield(L, -1, "__sgl_kind");
            const char *kind = lua_isstring(L, -1) ? lua_tostring(L, -1) : "";
            char kind_buf[16]; strncpy(kind_buf, kind, sizeof(kind_buf)-1); kind_buf[15]=0;
            lua_pop(L, 1);
            if (strcmp(kind_buf, "buffer") == 0) {
                lua_getfield(L, -1, "key");
                const char *bk = lua_tostring(L, -1);
                lua_pop(L, 1);
                ResEntry *be = res_table_get(&g_app_for_lua->res, bk);
                if (be && be->kind == RES_BUFFER && be->u.buf.type == SGL_BUFFER_VERTEX) {
                    bind.vertex_buffers[0] = be->u.buf.h;
                }
            }
            // texture / uniforms の処理は Task 10 / Task 11 で同じループに追加する
        }
        lua_pop(L, 1); // value を pop、key は次の lua_next のため残す
    }
    sg_apply_bindings(&bind);
    sg_draw(0, count, 1);
    return 0;
}

lua_pushcfunction(L, l_draw); lua_setglobal(L, "draw");
```

注: 上記は「attr が複数あっても 1 つの buffer に interleave されている」前提。Sample 1 は position だけの単一 attr なので OK。Sample 2 以降の頂点カラーも interleave で問題ない。複数 vertex buffer は PoC スコープ外。

- [ ] **Step 5: samples/01_triangle.lua を作成**

```lua
local verts = {
   0.0,  0.5, 0.0,
  -0.5, -0.5, 0.0,
   0.5, -0.5, 0.0,
}

local vs = [[
struct VSIn  { float3 pos : POSITION; };
struct VSOut { float4 pos : SV_Position; };
[shader("vertex")]
VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }
]]
local fs = [[
[shader("fragment")]
float4 fs_main() : SV_Target { return float4(1.0, 0.5, 0.0, 1.0); }
]]

function on_init() end
function on_event(e) end
function on_quit() end

function on_frame()
  local s = use_shader("tri_shader", vs, fs, 1)
  local b = use_buffer("tri_verts", VERTEX, verts, 1)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
  end_pass()
end
```

- [ ] **Step 6: CMakeLists.txt に pipeline.c を追加**

```cmake
add_executable(sglua
  src/main.c src/app.c src/lua_api.c src/enums_lua.c
  src/pass.c src/resources.c src/shader.c src/pipeline.c
)
```

- [ ] **Step 7: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/01_triangle.lua
```

期待: 青グレー背景にオレンジ色の三角形が中央に出る。

- [ ] **Step 8: コミット**

```bash
git add src/pipeline.h src/pipeline.c src/lua_api.c src/app.h src/app.c samples/01_triangle.lua CMakeLists.txt
git commit -m "feat: cache pipelines and dispatch draws — sample 01 triangle works"
```

---

## Task 9: Sample 2 (頂点カラー) — 複数 attribute 対応

**Goal:** Vertex shader 入力に position + color の 2 つの attribute を持たせ、interleave した buffer から頂点カラー三角形が描ける。

**Files:**
- Modify: `src/shader.c` (reflection が複数 attr の offset を正しく計算することを確認、必要なら修正)
- Create: `samples/02_vertex_color.lua`

- [ ] **Step 1: shader.c の reflection が複数 attr に対応していることを確認**

Slang の reflection で vertex stage の input parameters を全部走査し、各 parameter に:
- `name`
- `getBindingIndex` または location (Slang attribute `[[vk::location(N)]]` から)
- 型 (`getType()->getElementCount()` でベクトル成分数)

を取り、`offset_floats` を「これまでの attr の合計成分数」で計算する。`vertex_stride_floats` も合計値。実装に不安があれば最小ログを `SDL_Log("attr %d: name=%s slot=%d comp=%d offset=%d", i, name, slot, comp, off)` で確認する。

- [ ] **Step 2: samples/02_vertex_color.lua を作成**

```lua
local verts = {
  -- pos.x, pos.y, pos.z,  color.r, color.g, color.b, color.a
   0.0,  0.5, 0.0,   1, 0, 0, 1,
  -0.5, -0.5, 0.0,   0, 1, 0, 1,
   0.5, -0.5, 0.0,   0, 0, 1, 1,
}

local vs = [[
struct VSIn  { float3 pos : POSITION; float4 color : COLOR; };
struct VSOut { float4 pos : SV_Position; float4 color : COLOR; };
[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 1.0);
    o.color = i.color;
    return o;
}
]]
local fs = [[
struct FSIn { float4 color : COLOR; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target { return i.color; }
]]

function on_init() end
function on_event(e) end
function on_quit() end

function on_frame()
  local s = use_shader("vc_shader", vs, fs, 1)
  local b = use_buffer("vc_verts", VERTEX, verts, 1)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
  end_pass()
end
```

- [ ] **Step 3: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/02_vertex_color.lua
```

期待: RGB の頂点カラーで補間された三角形 (上=赤, 左下=緑, 右下=青)。

- [ ] **Step 4: 失敗時のデバッグ**

attribute の offset がズレていたら shader.c の reflection 抽出を確認。位置と色の location が逆になっていないか。`SDL_Log` でリフレクション結果を表示して目視確認。

- [ ] **Step 5: コミット**

```bash
git add samples/02_vertex_color.lua src/shader.c
git commit -m "feat: support multi-attribute vertex layout — sample 02 vertex color works"
```

---

## Task 10: use_texture と Sample 3 (テクスチャ)

**Goal:** Lua から `use_texture(key, w, h, RGBA8, data, version)` を呼んで `sg_image` + `sg_sampler` を作り、shader から sampling できる。

**Files:**
- Modify: `src/resources.c` (texture upload)
- Modify: `src/lua_api.c` (`l_use_texture`, `draw` の resource 解決で texture を扱う)
- Modify: `src/shader.c` (reflection に image/sampler 情報抽出)
- Create: `assets/tex.png`
- Create: `samples/03_texture.lua`
- Modify: `CMakeLists.txt` (stb_image を vendor or PNG ローダのみ samples 側で用意)

- [ ] **Step 1: PNG 読み込み**

PoC では Lua 側から「pixel 配列」を直接渡す形にし、PNG decoder は本体に組み込まない。`samples/03_texture.lua` で `gen_checker(16,16)` 関数を Lua で書いてチェッカー柄を生成 → `use_texture` に渡す。

→ PNG 不要なので `assets/tex.png` の作成は省略。

- [ ] **Step 2: l_use_texture を実装**

```c
static int l_use_texture(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    int w = (int)luaL_checkinteger(L, 2);
    int h = (int)luaL_checkinteger(L, 3);
    int fmt = (int)luaL_checkinteger(L, 4);
    int has_data = !lua_isnil(L, 5);
    luaL_checktype(L, 5, has_data ? LUA_TTABLE : LUA_TNIL);
    int version = (int)luaL_checkinteger(L, 6);

    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_TEXTURE);
    if (!e) return luaL_error(L, "use_texture: key conflict");
    res_table_touch(e, g_app_for_lua->frame_index);

    if (e->version == version && e->u.tex.h.id != 0) {
        lua_newtable(L);
        lua_pushstring(L, "texture"); lua_setfield(L, -2, "__sgl_kind");
        lua_pushstring(L, key);       lua_setfield(L, -2, "key");
        return 1;
    }

    sg_pixel_format pf;
    int bpp;
    switch (fmt) {
        case SGL_PF_RGBA8: pf = SG_PIXELFORMAT_RGBA8; bpp = 4; break;
        case SGL_PF_R8:    pf = SG_PIXELFORMAT_R8;    bpp = 1; break;
        default: return luaL_error(L, "use_texture: format not supported in PoC");
    }

    uint8_t *pixels = NULL;
    if (has_data) {
        int n = (int)lua_rawlen(L, 5);
        if (n != w * h * bpp) return luaL_error(L, "use_texture: data size mismatch");
        pixels = (uint8_t*)malloc(n);
        for (int i = 0; i < n; ++i) {
            lua_geti(L, 5, i + 1);
            pixels[i] = (uint8_t)lua_tointeger(L, -1);
            lua_pop(L, 1);
        }
    }

    if (e->u.tex.h.id != 0) sg_destroy_image(e->u.tex.h);
    if (e->u.tex.smp.id != 0) sg_destroy_sampler(e->u.tex.smp);
    e->u.tex.h = sg_make_image(&(sg_image_desc){
        .width = w, .height = h, .pixel_format = pf,
        .data.subimage[0][0] = has_data
            ? (sg_range){ .ptr = pixels, .size = (size_t)w*h*bpp }
            : (sg_range){0},
        .usage = has_data ? SG_USAGE_IMMUTABLE : SG_USAGE_DEFAULT,
        .render_target = !has_data,
    });
    e->u.tex.smp = sg_make_sampler(&(sg_sampler_desc){
        .min_filter = SG_FILTER_LINEAR, .mag_filter = SG_FILTER_LINEAR,
        .wrap_u = SG_WRAP_REPEAT, .wrap_v = SG_WRAP_REPEAT,
    });
    e->u.tex.w = w; e->u.tex.h_ = h; e->u.tex.fmt = fmt;
    e->version = version;
    if (pixels) free(pixels);

    lua_newtable(L);
    lua_pushstring(L, "texture"); lua_setfield(L, -2, "__sgl_kind");
    lua_pushstring(L, key);       lua_setfield(L, -2, "key");
    return 1;
}

// register
lua_pushcfunction(L, l_use_texture); lua_setglobal(L, "use_texture");
```

- [ ] **Step 3: shader.c の reflection で image/sampler 情報を抽出**

Slang reflection で `category == ShaderResource` なものは image、`SamplerState` は sampler。同じ名前の image/sampler ペアを集める (Slang/HLSL で `SamplerState s; Texture2D t;` のように 2 つ書くか、`Sampler2D` 1 つで済ませるか)。PoC では `SamplerState samp; Texture2D tex;` の 2 つを必須として、Lua 側 named キーは `tex` (image 側) のみで「自動で同名 + `_smp` の sampler を引く」のは複雑なので、Lua 側で別名は使わず **shader 側で `Texture2D diffuse; SamplerState diffuse_smp;` のような命名規約を採用** する。

`ShaderTexture` を埋める際、name は `diffuse`、img_slot/smp_slot を Slang reflection から取得。同じ inded name を Lua resources から探す。

- [ ] **Step 4: l_draw で texture binding を解決**

`l_draw` 内、resources のループで `__sgl_kind=="texture"` なら shader reflection の `texs[]` から match する name (Lua 側のキー) を引いて `bind.fs.images[slot]` / `bind.fs.samplers[slot]` を埋める。

```c
// l_draw 内、resources のテーブルを走査するループに追加
// keys を取れる named iteration に変える
lua_pushnil(L);
while (lua_next(L, 2) != 0) {
    // key は -2、value は -1
    const char *res_name = lua_isstring(L, -2) ? lua_tostring(L, -2) : NULL;
    if (lua_istable(L, -1)) {
        lua_getfield(L, -1, "__sgl_kind");
        const char *kind = lua_isstring(L, -1) ? lua_tostring(L, -1) : "";
        lua_pop(L, 1);
        if (strcmp(kind, "buffer") == 0) {
            // 既存の vertex buffer 解決
        } else if (strcmp(kind, "texture") == 0 && res_name) {
            lua_getfield(L, -1, "key");
            const char *tk = lua_tostring(L, -1);
            lua_pop(L, 1);
            ResEntry *te = res_table_get(&g_app_for_lua->res, tk);
            if (te && te->kind == RES_TEXTURE) {
                // shader reflection から res_name と一致する texs[i] を探す
                for (int i = 0; i < sh_e->u.sh.refl.tex_count; ++i) {
                    if (strcmp(sh_e->u.sh.refl.texs[i].name, res_name) == 0) {
                        bind.fs.images[sh_e->u.sh.refl.texs[i].img_slot] = te->u.tex.h;
                        bind.fs.samplers[sh_e->u.sh.refl.texs[i].smp_slot] = te->u.tex.smp;
                        break;
                    }
                }
            }
        }
    }
    lua_pop(L, 1);
}
```

- [ ] **Step 5: samples/03_texture.lua を作成**

```lua
local function gen_checker(w, h)
  local out = {}
  for y = 0, h-1 do
    for x = 0, w-1 do
      local c = ((x // 4) + (y // 4)) % 2 == 0
      local v = c and 240 or 60
      local i = (y * w + x) * 4
      out[i+1] = v; out[i+2] = v; out[i+3] = v; out[i+4] = 255
    end
  end
  return out
end

local pixels = gen_checker(32, 32)

-- 三角形を縦に大きめ、UV を 0..1 に張る
local verts = {
  --   x      y     z      u    v
   0.0,  0.7, 0.0,  0.5, 0.0,
  -0.7, -0.7, 0.0,  0.0, 1.0,
   0.7, -0.7, 0.0,  1.0, 1.0,
}

local vs = [[
struct VSIn  { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
[shader("vertex")]
VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos,1); o.uv = i.uv; return o; }
]]
local fs = [[
Texture2D    diffuse;
SamplerState diffuse_smp;
struct FSIn { float2 uv : TEXCOORD0; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    return diffuse.Sample(diffuse_smp, i.uv);
}
]]

function on_init() end
function on_event(e) end
function on_quit() end

function on_frame()
  local s = use_shader("tex_shader", vs, fs, 1)
  local b = use_buffer("tex_verts", VERTEX, verts, 1)
  local t = use_texture("tex_chk", 32, 32, RGBA8, pixels, 1)
  begin_pass({ target = main_tex, clear_color = {0.1,0.1,0.2,1} })
    draw(3, { verts = b, diffuse = t }, { shader = s, depth = false, cull = NONE })
  end_pass()
end
```

- [ ] **Step 6: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/03_texture.lua
```

期待: チェッカー柄 (白/濃灰) が三角形に貼られて表示される。

- [ ] **Step 7: コミット**

```bash
git add src/lua_api.c src/resources.c src/resources.h src/shader.c samples/03_texture.lua
git commit -m "feat: use_texture and texture binding via reflection — sample 03 works"
```

---

## Task 11: uniform block 対応と Sample 4 (MVP)

**Goal:** `resources.uniforms = { mvp = matrix }` で uniform block を渡せる。Lua 側の matrix は length 16 の数値配列 (column-major)。Sample 4 で回転する立方体ではなく **回転する三角形** を描いて検証する (立方体だと頂点・index 配列が長くなり PoC の主旨から外れる)。

**Files:**
- Modify: `src/lua_api.c` (l_draw で uniforms を解決)
- Modify: `src/shader.c` (uniform block reflection の精度確認)
- Create: `samples/04_mvp.lua`

- [ ] **Step 1: l_draw で uniforms を処理**

```c
// l_draw 内、resources を走査するループの中で
} else if (strcmp(kind, "") == 0 || lua_isnil(L_kind)) {
    // 値が table だが __sgl_kind が無い → uniform block 候補
    // 命名規約: resources.uniforms = { mvp = ... } のように key="uniforms" を期待。
    // shader reflection の ub[0] にマッチさせる。
}
```

簡略化のため: resources に `uniforms` というキーがあれば、その値を「shader reflection の ub[0]」へ詰める。multiple uniform block は PoC スコープ外。

```c
// l_draw 関数の末尾近くに追加
lua_getfield(L, 2, "uniforms");
if (lua_istable(L, -1) && sh_e->u.sh.refl.ub_count > 0) {
    const ShaderUniformBlock *ub = &sh_e->u.sh.refl.ubs[0];
    float *buf = (float*)alloca(ub->size_floats * sizeof(float));
    memset(buf, 0, ub->size_floats * sizeof(float));
    for (int m = 0; m < ub->member_count; ++m) {
        const ShaderUniformMember *mem = &ub->members[m];
        lua_getfield(L, -1, mem->name);
        if (lua_istable(L, -1)) {
            int n = (int)lua_rawlen(L, -1);
            int copy = n < mem->comp_count ? n : mem->comp_count;
            for (int j = 0; j < copy; ++j) {
                lua_geti(L, -1, j + 1);
                buf[mem->offset_floats + j] = (float)lua_tonumber(L, -1);
                lua_pop(L, 1);
            }
        }
        lua_pop(L, 1);
    }
    sg_apply_uniforms(ub->slot, &(sg_range){ .ptr = buf, .size = (size_t)ub->size_floats * sizeof(float) });
}
lua_pop(L, 1);
```

注: sokol_gfx 1.4 系では `sg_apply_uniforms(int slot, ...)` だが、最新の API では `sg_apply_uniforms(SG_SHADERSTAGE_VS, slot, ...)` のように stage 引数があるので vendored ファイルのバージョンに合わせて使い分けること。実装時に `sokol_gfx.h` の signature を確認する。

- [ ] **Step 2: Slang の uniform block reflection を埋める**

Slang reflection で `category == Uniform` の parameter は uniform block の中身。block 自体は `getParameterCount()` のうち `getCategory() == ConstantBuffer` の entry。各 parameter の `getOffset(SLANG_PARAMETER_CATEGORY_UNIFORM)` で float offset、type の `getElementCount()` で comp_count を取る。

実装の検証は `SDL_Log("ub: name=%s slot=%d size=%d", ub->name, ub->slot, ub->size_floats)` を 1 回出して目視で確認 (Sample 4 が動けば概ね正しい)。

- [ ] **Step 3: samples/04_mvp.lua を作成**

```lua
local verts = {
   0.0,  0.5, 0.0,   1, 0, 0, 1,
  -0.5, -0.5, 0.0,   0, 1, 0, 1,
   0.5, -0.5, 0.0,   0, 0, 1, 1,
}

-- column-major 4x4 (Z 軸回転)
local function rot_z(theta)
  local c, s = math.cos(theta), math.sin(theta)
  return { c, s, 0, 0,  -s, c, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }
end

local vs = [[
struct Uniforms { float4x4 mvp; };
ConstantBuffer<Uniforms> u;
struct VSIn  { float3 pos : POSITION; float4 color : COLOR; };
struct VSOut { float4 pos : SV_Position; float4 color : COLOR; };
[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = mul(u.mvp, float4(i.pos, 1.0));
    o.color = i.color;
    return o;
}
]]
local fs = [[
struct FSIn { float4 color : COLOR; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target { return i.color; }
]]

local t = 0
function on_init() end
function on_event(e) end
function on_quit() end

function on_frame()
  t = t + 1/60
  local s = use_shader("mvp_shader", vs, fs, 1)
  local b = use_buffer("mvp_verts", VERTEX, verts, 1)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b, uniforms = { mvp = rot_z(t) } },
            { shader = s, depth = false, cull = NONE })
  end_pass()
end
```

- [ ] **Step 4: build & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/04_mvp.lua
```

期待: 頂点カラーの三角形が画面中央でゆっくり回転する。

- [ ] **Step 5: 失敗時の見方**

- 三角形が真っ黒: uniform が 0 で埋められている → reflection の offset/size がズレている
- 回転が逆方向: matrix が row-major で渡されている可能性 → Slang shader 側で `mul(u.mvp, v)` を `mul(v, u.mvp)` に切替えるか Lua 側で transpose
- 三角形が消える: 1 フレームだけ回転して z=1 平面外に飛ぶような mat バグ → t を 0 で fix して目視

- [ ] **Step 6: コミット**

```bash
git add src/lua_api.c src/shader.c samples/04_mvp.lua
git commit -m "feat: bind uniform block from lua resources.uniforms — sample 04 mvp works"
```

---

## Task 12: README とサンプル一覧

**Goal:** 4 サンプル全部が動くことを 1 つの README で実演し、PoC 完成とする。

**Files:**
- Create: `README.md`

- [ ] **Step 1: README.md を作成**

```markdown
# sglua (PoC)

Lua 向け薄い 3D 描画ライブラリの PoC。SDL3 + sokol_gfx + Slang + Lua 5.4。

## Build

```bash
mkdir -p third_party/slang/lib
# slang prebuilt binary (libslang.so) を third_party/slang/lib に配置
cmake -S . -B build
cmake --build build -j
```

## Run

```bash
./build/sglua samples/01_triangle.lua
./build/sglua samples/02_vertex_color.lua
./build/sglua samples/03_texture.lua
./build/sglua samples/04_mvp.lua
```

## サンプル

| # | スクリプト | 内容 |
|---|------------|------|
| 1 | 01_triangle.lua | 単色三角形 (use_buffer / use_shader / draw / begin_pass) |
| 2 | 02_vertex_color.lua | 頂点カラーで補間された三角形 |
| 3 | 03_texture.lua | チェッカー柄テクスチャを貼った三角形 (use_texture) |
| 4 | 04_mvp.lua | 回転行列を uniform で渡す三角形 |

## 未実装 (将来)

- post process / MRT / deferred shading (Sample 5〜7)
- ホットリロード版 (use_* の version で素材は対応済、Lua 側ファイル監視は未実装)
- リソース sweep (フレーム未参照の自動破棄)
- SDL3 GPU backend
- compute shader / VR / マルチスレッド描画
```

- [ ] **Step 2: 4 サンプルが全部動くことを目視確認**

```bash
for s in samples/0[1-4]_*.lua; do echo "=== $s ==="; ./build/sglua "$s"; done
```

期待: 各サンプルでウィンドウを閉じると次に進む。全部問題なく動く。

- [ ] **Step 3: コミット**

```bash
git add README.md
git commit -m "docs: poc readme with sample run instructions"
```

---

## 計画外 (Open Questions のうち PoC で先送りしたもの)

- 位置引数版の `draw(count, { buf, tex, { ub } })`: named 版で十分なので未実装。仕様書通り両対応にする場合は Task 8 に追加。
- `retain(key)`: Sample 4 までは毎フレーム `use_*` が呼ばれるので不要。
- `init_config({...})`: ウィンドウサイズ等は main.c でハードコード。将来 `init_config` を Lua グローバルとして追加。
- リソース sweep: `last_seen_frame < frame_index - sweep_after` の自動破棄。frame_index は既にあるので残作業は走査だけ。
- MSAA / vsync 設定 / depth attachment: 各サンプルが要求しないので省略。Sample 5 (depth test) を入れる時に追加。
- Slang library のビルド済みバイナリの配布: `third_party/slang/lib/libslang.so` を git LFS or 取得スクリプト化する。
- Multi-uniform-block / Multi-vertex-buffer: shader reflection の取り扱い拡張が必要。
- shader sampler 設定の Lua 側からの指定 (filter, wrap): Open Question。texture 作成時引数に追加するのが筋。

---

# Phase 2: Vulkan Migration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** sokol_gfx の backend を `SOKOL_GLCORE` から `SOKOL_VULKAN` に切替え、Slang ターゲットを SPIR-V にし、`downversion_glsl` regex hack を全面削除する。lavapipe (Mesa software Vulkan ICD) を使った headless 動作を確立する。GL 経路は完全削除。

**Architecture:**
SDL3 で window + Vulkan instance/surface を作成、Vulkan loader 経由で実 ICD (実機 GPU は本物の vendor driver、headless は lavapipe) に到達する。`app.c` が VkInstance/PhysicalDevice/Device/Queue/Swapchain/depth attachment/per-frame semaphores を保持し、`sg_environment.vulkan` と `sg_swapchain.vulkan` に渡す。Slang は SPIR-V を直接出すので、shader compile pipeline はクロスコンパイル不要 — reflection は target 非依存なのでロジックはほぼ流用。lavapipe 経由 headless は `VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.x86_64.json` を立てて実行スクリプトで切替えるだけ (コードは判定不要)。

**Tech Stack:**
- 既存と同じ (C11, C++17, CMake 3.20+, SDL3, Lua 5.4, Slang, sokol_gfx)
- `find_package(Vulkan REQUIRED)` でシステム Vulkan loader を link
- ソフトウェア Vulkan: Mesa の lavapipe (`mesa-vulkan-drivers` / `vulkan-swrast`)

**移行前後の差分**
- `OpenGL::GL` link → `Vulkan::Vulkan` link
- `SOKOL_GLCORE` → `SOKOL_VULKAN`
- `SDL_WINDOW_OPENGL` → `SDL_WINDOW_VULKAN`
- `SDL_GL_*` (CreateContext / SwapWindow) → SDL3 Vulkan + 自前 swapchain + present queue
- Slang target `glsl_330` → `spirv_1_5` / `glsl-450` (sokol vulkan の期待に合わせる)
- `downversion_glsl` 全面削除 (#version 書換、column_major strip、separate texture/sampler 書換、UBO flatten すべて消える)
- `sg_shader_desc.vertex_func/.fragment_func` の `.source` → `.bytecode` (SPIR-V binary)

---

## File Structure (Phase 2 後)

変わるファイル:
```
src/
├── main.c               # SDL_INIT_VIDEO + SDL_WINDOW_VULKAN
├── app.{h,c}            # Vulkan instance/device/swapchain/semaphores ライフサイクル
├── pass.{h,c}           # sg_swapchain.vulkan 形式に組み立て直し
├── shader.cpp           # Slang→SPIR-V、reflection はそのまま流用、downversion_glsl 削除
├── pipeline.{h,c}       # 大半維持、color/depth pixel format は Vulkan 互換に
├── sokol_impl.c         # SOKOL_VULKAN define
├── resources.{h,c}      # 維持 (sokol API 越し)
├── lua_api.c            # 維持
└── enums.h, enums_lua.c # 維持

CMakeLists.txt           # find_package(Vulkan), -DSOKOL_VULKAN, link 切替
scripts/run-headless.sh  # 新規: VK_ICD_FILENAMES=lavapipe icd で起動
README.md                # 依存と headless 手順を更新
```

新規ファイルは `scripts/run-headless.sh` のみ。

---

## Task 13: CMake / define 切替と最小ビルド

**Goal:** GL 関連を削除し、Vulkan link + `SOKOL_VULKAN` define でビルドが通る最小状態にする。app.c は中身が壊れててもよいので、まず CMake のみ通す。

**Files:**
- Modify: `CMakeLists.txt`
- Modify: `src/main.c` (1 行)

- [ ] **Step 1: CMakeLists.txt の link/include を Vulkan に切替**

```cmake
# 削除: find_package(OpenGL REQUIRED) と OpenGL::GL link
# 追加:
find_package(Vulkan REQUIRED)

target_link_libraries(sglua PRIVATE
  SDL3::SDL3
  lua_static
  slang
  Vulkan::Vulkan      # OpenGL::GL から置換
  m dl
)

# Compile define を切替
target_compile_definitions(sglua PRIVATE
  SOKOL_VULKAN        # SOKOL_GLCORE から置換
)
```

`m dl` の Linux-only コメントはそのまま残してよい (Vulkan loader でも libdl 経由)。

- [ ] **Step 2: main.c の WINDOW フラグ切替**

`SDL_CreateWindow` (もしあれば。現状は app.c だが念のため) と app_init 内の window 作成で `SDL_WINDOW_OPENGL` を `SDL_WINDOW_VULKAN` に変更。

```c
// src/app.c の SDL_CreateWindow 呼び出し
app->window = SDL_CreateWindow("sglua", 1280, 720,
    SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
```

- [ ] **Step 3: app.c の GL ctx 作成行を一旦コメントアウト**

`SDL_GL_CreateContext` / `SDL_GL_MakeCurrent` / `SDL_GL_SetSwapInterval` / `sg_setup` / `sg_commit` / `SDL_GL_SwapWindow` 等の GL 直叩き行を **すべてコメントアウト or 削除**。app_init は `return false;` で即時失敗してよい (Task 14 で実装する)。app_frame_begin / app_frame_end / app_shutdown も最小化。

これで「ビルドは通るが実行は失敗する」状態を作る。

- [ ] **Step 4: ビルド確認**

```bash
cmake -S . -B build
cmake --build build -j 2>&1 | tee /tmp/build.log
```

期待: ビルド成功。`undefined reference to glX*` 等の GL シンボルエラーが出ていないこと。Vulkan は loader 経由なので `vk*` シンボル参照は `Vulkan::Vulkan` の `libvulkan.so` で解決される。

`undefined reference` が GL 関連で出る場合、消し忘れた呼び出しがどこかにある。

- [ ] **Step 5: コミット**

```bash
git add CMakeLists.txt src/app.c src/main.c
git commit -m "build: switch backend to SOKOL_VULKAN and Vulkan link"
```

---

## Task 14: Vulkan instance / physical device / device / queue 作成

**Goal:** SDL3 と Vulkan loader を使って VkInstance / VkPhysicalDevice / VkDevice / VkQueue を作り、`sg_environment.vulkan` を埋めて `sg_setup` を呼ぶ。pass はまだ実装しない。

**Files:**
- Modify: `src/app.h` (App 構造体に Vulkan handle を追加)
- Modify: `src/app.c` (Vulkan 初期化ロジック)

- [ ] **Step 1: src/app.h に Vulkan handle を追加**

```c
#pragma once
#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>
#include <stdint.h>
#include <stdbool.h>
#include "lua_api.h"
#include "pass.h"
#include "resources.h"
#include "pipeline.h"

typedef struct App {
    SDL_Window *window;

    // Vulkan core
    VkInstance       vk_instance;
    VkPhysicalDevice vk_phys;
    VkDevice         vk_device;
    VkQueue          vk_queue;
    uint32_t         vk_queue_family;

    // (Task 15 で追加: surface, swapchain, semaphores, depth)

    LuaCtx        lua;
    PassState     pass;
    ResTable      res;
    PipelineCache pip_cache;
    uint64_t      frame_index;
} App;

bool app_init(App *app);
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);
```

`SDL_GLContext gl_ctx` は削除。

- [ ] **Step 2: src/app.c の app_init を Vulkan 化**

```c
#include "app.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>
#include "sokol_gfx.h"
#include <stdlib.h>
#include <string.h>

static void sglua_sokol_logger(
    const char* tag, uint32_t level, uint32_t item_id,
    const char* msg, uint32_t line, const char* file, void* user)
{
    (void)tag; (void)item_id; (void)file; (void)user;
    const char *lvl = (level == 0) ? "PANIC" : (level == 1) ? "ERROR"
                    : (level == 2) ? "WARN"  : "INFO";
    SDL_Log("[sg %s:%u] %s", lvl, line, msg ? msg : "(no msg)");
}

static bool create_vk_instance(VkInstance *out_inst) {
    Uint32 ext_count = 0;
    const char * const *sdl_exts = SDL_Vulkan_GetInstanceExtensions(&ext_count);
    if (!sdl_exts) {
        SDL_Log("SDL_Vulkan_GetInstanceExtensions failed: %s", SDL_GetError());
        return false;
    }
    VkApplicationInfo ai = {
        .sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
        .pApplicationName = "sglua",
        .applicationVersion = VK_MAKE_VERSION(0, 1, 0),
        .pEngineName = "sglua",
        .engineVersion = VK_MAKE_VERSION(0, 1, 0),
        .apiVersion = VK_API_VERSION_1_2,
    };
    VkInstanceCreateInfo ci = {
        .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
        .pApplicationInfo = &ai,
        .enabledExtensionCount = ext_count,
        .ppEnabledExtensionNames = sdl_exts,
    };
    if (vkCreateInstance(&ci, NULL, out_inst) != VK_SUCCESS) {
        SDL_Log("vkCreateInstance failed");
        return false;
    }
    return true;
}

static bool pick_physical_device(VkInstance inst, VkPhysicalDevice *out_phys, uint32_t *out_qf) {
    uint32_t n = 0;
    vkEnumeratePhysicalDevices(inst, &n, NULL);
    if (n == 0) { SDL_Log("no Vulkan physical device"); return false; }
    VkPhysicalDevice *phys = (VkPhysicalDevice*)malloc(sizeof(VkPhysicalDevice) * n);
    vkEnumeratePhysicalDevices(inst, &n, phys);

    // Pick first device that has a graphics queue family.
    for (uint32_t i = 0; i < n; ++i) {
        uint32_t qfn = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(phys[i], &qfn, NULL);
        VkQueueFamilyProperties *qfp = (VkQueueFamilyProperties*)malloc(sizeof(VkQueueFamilyProperties) * qfn);
        vkGetPhysicalDeviceQueueFamilyProperties(phys[i], &qfn, qfp);
        for (uint32_t q = 0; q < qfn; ++q) {
            if (qfp[q].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
                *out_phys = phys[i];
                *out_qf = q;
                free(qfp); free(phys);
                return true;
            }
        }
        free(qfp);
    }
    free(phys);
    SDL_Log("no graphics queue family found");
    return false;
}

static bool create_vk_device(VkPhysicalDevice phys, uint32_t qf,
                             VkDevice *out_dev, VkQueue *out_q) {
    float prio = 1.0f;
    VkDeviceQueueCreateInfo qci = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
        .queueFamilyIndex = qf,
        .queueCount = 1,
        .pQueuePriorities = &prio,
    };
    const char *dev_exts[] = { VK_KHR_SWAPCHAIN_EXTENSION_NAME };
    VkDeviceCreateInfo ci = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
        .queueCreateInfoCount = 1,
        .pQueueCreateInfos = &qci,
        .enabledExtensionCount = 1,
        .ppEnabledExtensionNames = dev_exts,
    };
    if (vkCreateDevice(phys, &ci, NULL, out_dev) != VK_SUCCESS) {
        SDL_Log("vkCreateDevice failed");
        return false;
    }
    vkGetDeviceQueue(*out_dev, qf, 0, out_q);
    return true;
}

bool app_init(App *app) {
    memset(app, 0, sizeof(*app));
    app->window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
    if (!app->window) { SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError()); return false; }

    if (!SDL_Vulkan_LoadLibrary(NULL)) {
        SDL_Log("SDL_Vulkan_LoadLibrary failed: %s", SDL_GetError()); return false;
    }
    if (!create_vk_instance(&app->vk_instance)) return false;
    if (!pick_physical_device(app->vk_instance, &app->vk_phys, &app->vk_queue_family)) return false;
    if (!create_vk_device(app->vk_phys, app->vk_queue_family, &app->vk_device, &app->vk_queue)) return false;

    sg_setup(&(sg_desc){
        .environment = {
            .defaults = {
                .color_format = SG_PIXELFORMAT_BGRA8,    // Vulkan swapchain は BGRA がデフォルト
                .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
                .sample_count = 1,
            },
            .vulkan = {
                .instance        = (const void*)app->vk_instance,
                .physical_device = (const void*)app->vk_phys,
                .device          = (const void*)app->vk_device,
                .queue           = (const void*)app->vk_queue,
                .queue_family_index = app->vk_queue_family,
            },
        },
        .logger.func = sglua_sokol_logger,
    });

    pass_state_init(&app->pass);
    res_table_init(&app->res);
    pipeline_cache_init(&app->pip_cache);
    return true;
}

void app_frame_begin(App *app, int *out_w, int *out_h) {
    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;
    pass_state_set_swapchain_size(&app->pass, w, h);
    // (Task 15: vkAcquireNextImage 等)
}

void app_frame_end(App *app) {
    sg_commit();
    // (Task 15: vkQueuePresentKHR 等)
    app->frame_index++;
}

void app_shutdown(App *app) {
    pipeline_cache_shutdown(&app->pip_cache);
    res_table_shutdown(&app->res);
    sg_shutdown();
    if (app->vk_device)   vkDestroyDevice(app->vk_device, NULL);
    if (app->vk_instance) vkDestroyInstance(app->vk_instance, NULL);
    SDL_Vulkan_UnloadLibrary();
    if (app->window) SDL_DestroyWindow(app->window);
}
```

注: BGRA8 を default にしているのは Vulkan swapchain の一般的な surface format に合わせるため。後段で実 swapchain format を取得して反映する (Task 15)。

- [ ] **Step 3: ビルド & smoke test**

```bash
cmake --build build -j
SDL_VIDEODRIVER=offscreen timeout 2 ./build/sglua samples/00_hello.lua
```

期待: ビルド clean、起動して `[lua] on_init` が出る、exit 124。`vkCreateInstance failed` 等のログが出ていないこと。

(まだ swapchain が無いので描画系サンプルは動かなくてよい。)

- [ ] **Step 4: コミット**

```bash
git add src/app.h src/app.c
git commit -m "feat(vk): create Vulkan instance/device/queue and call sg_setup"
```

---

## Task 15: Swapchain 構築と presentation loop

**Goal:** VkSurfaceKHR + VkSwapchainKHR + depth attachment + per-frame semaphores を作り、毎フレーム `vkAcquireNextImageKHR` → sokol render → `vkQueuePresentKHR` で画面更新する。`sg_swapchain.vulkan` を pass 開始時に渡せるようにする。

**Files:**
- Modify: `src/app.h` (フィールド追加)
- Modify: `src/app.c` (swapchain 関連実装)
- Modify: `src/pass.h`, `src/pass.c` (sg_swapchain.vulkan 組立て)

- [ ] **Step 1: src/app.h を拡張**

App 構造体に追加:

```c
    // Surface & swapchain
    VkSurfaceKHR     vk_surface;
    VkSwapchainKHR   vk_swapchain;
    VkFormat         vk_swapchain_format;
    uint32_t         vk_swapchain_image_count;
    VkImage         *vk_swapchain_images;       // 配列、image_count 個
    VkImageView     *vk_swapchain_views;        // 同上

    // Depth attachment (swapchain 全体で 1 枚共有)
    VkImage          vk_depth_image;
    VkDeviceMemory   vk_depth_mem;
    VkImageView      vk_depth_view;

    // Per-frame semaphores
    VkSemaphore      vk_acquire_sem;            // image 取得待ち
    VkSemaphore      vk_present_sem;            // present 待ち
    uint32_t         vk_current_image;          // acquire の戻り値
```

`vk_acquire_sem` / `vk_present_sem` は本格的なフレーム並列を入れるなら配列化が必要だが、PoC は 1 フレーム同期前提で 1 個ずつ。

- [ ] **Step 2: src/app.c に swapchain 作成関数を追加**

```c
static bool create_swapchain(App *app) {
    if (!SDL_Vulkan_CreateSurface(app->window, app->vk_instance, NULL, &app->vk_surface)) {
        SDL_Log("SDL_Vulkan_CreateSurface failed: %s", SDL_GetError());
        return false;
    }

    // Surface format: 最初に対応する BGRA8 SRGB or UNORM を選ぶ。
    uint32_t fmt_count = 0;
    vkGetPhysicalDeviceSurfaceFormatsKHR(app->vk_phys, app->vk_surface, &fmt_count, NULL);
    VkSurfaceFormatKHR *fmts = malloc(sizeof(VkSurfaceFormatKHR) * fmt_count);
    vkGetPhysicalDeviceSurfaceFormatsKHR(app->vk_phys, app->vk_surface, &fmt_count, fmts);
    VkSurfaceFormatKHR chosen = fmts[0];
    for (uint32_t i = 0; i < fmt_count; ++i) {
        if (fmts[i].format == VK_FORMAT_B8G8R8A8_UNORM) { chosen = fmts[i]; break; }
    }
    free(fmts);
    app->vk_swapchain_format = chosen.format;

    VkSurfaceCapabilitiesKHR caps;
    vkGetPhysicalDeviceSurfaceCapabilitiesKHR(app->vk_phys, app->vk_surface, &caps);

    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    VkExtent2D extent = caps.currentExtent.width != 0xffffffff ? caps.currentExtent
                                                               : (VkExtent2D){ (uint32_t)w, (uint32_t)h };

    VkSwapchainCreateInfoKHR sci = {
        .sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
        .surface = app->vk_surface,
        .minImageCount = caps.minImageCount + 1,
        .imageFormat = chosen.format,
        .imageColorSpace = chosen.colorSpace,
        .imageExtent = extent,
        .imageArrayLayers = 1,
        .imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT,
        .imageSharingMode = VK_SHARING_MODE_EXCLUSIVE,
        .preTransform = caps.currentTransform,
        .compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
        .presentMode = VK_PRESENT_MODE_FIFO_KHR,
        .clipped = VK_TRUE,
    };
    if (vkCreateSwapchainKHR(app->vk_device, &sci, NULL, &app->vk_swapchain) != VK_SUCCESS) {
        SDL_Log("vkCreateSwapchainKHR failed"); return false;
    }

    // Swapchain images & views
    vkGetSwapchainImagesKHR(app->vk_device, app->vk_swapchain, &app->vk_swapchain_image_count, NULL);
    app->vk_swapchain_images = malloc(sizeof(VkImage) * app->vk_swapchain_image_count);
    vkGetSwapchainImagesKHR(app->vk_device, app->vk_swapchain, &app->vk_swapchain_image_count, app->vk_swapchain_images);
    app->vk_swapchain_views = malloc(sizeof(VkImageView) * app->vk_swapchain_image_count);
    for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
        VkImageViewCreateInfo ivci = {
            .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            .image = app->vk_swapchain_images[i],
            .viewType = VK_IMAGE_VIEW_TYPE_2D,
            .format = chosen.format,
            .subresourceRange = {
                .aspectMask = VK_IMAGE_ASPECT_COLOR_BIT,
                .levelCount = 1, .layerCount = 1,
            },
        };
        vkCreateImageView(app->vk_device, &ivci, NULL, &app->vk_swapchain_views[i]);
    }

    // Depth attachment (D24S8 or D32S8 fallback)
    VkFormat depth_fmt = VK_FORMAT_D24_UNORM_S8_UINT;
    VkFormatProperties fp;
    vkGetPhysicalDeviceFormatProperties(app->vk_phys, depth_fmt, &fp);
    if (!(fp.optimalTilingFeatures & VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT)) {
        depth_fmt = VK_FORMAT_D32_SFLOAT_S8_UINT;
    }

    VkImageCreateInfo dci = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
        .imageType = VK_IMAGE_TYPE_2D,
        .format = depth_fmt,
        .extent = { extent.width, extent.height, 1 },
        .mipLevels = 1, .arrayLayers = 1,
        .samples = VK_SAMPLE_COUNT_1_BIT,
        .tiling = VK_IMAGE_TILING_OPTIMAL,
        .usage = VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
        .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
    };
    vkCreateImage(app->vk_device, &dci, NULL, &app->vk_depth_image);

    VkMemoryRequirements mr;
    vkGetImageMemoryRequirements(app->vk_device, app->vk_depth_image, &mr);
    VkPhysicalDeviceMemoryProperties pmp;
    vkGetPhysicalDeviceMemoryProperties(app->vk_phys, &pmp);
    uint32_t mem_type = 0;
    for (uint32_t i = 0; i < pmp.memoryTypeCount; ++i) {
        if ((mr.memoryTypeBits & (1u << i)) &&
            (pmp.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) {
            mem_type = i; break;
        }
    }
    VkMemoryAllocateInfo mai = {
        .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
        .allocationSize = mr.size,
        .memoryTypeIndex = mem_type,
    };
    vkAllocateMemory(app->vk_device, &mai, NULL, &app->vk_depth_mem);
    vkBindImageMemory(app->vk_device, app->vk_depth_image, app->vk_depth_mem, 0);

    VkImageViewCreateInfo dvci = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
        .image = app->vk_depth_image,
        .viewType = VK_IMAGE_VIEW_TYPE_2D,
        .format = depth_fmt,
        .subresourceRange = {
            .aspectMask = VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT,
            .levelCount = 1, .layerCount = 1,
        },
    };
    vkCreateImageView(app->vk_device, &dvci, NULL, &app->vk_depth_view);

    // Semaphores
    VkSemaphoreCreateInfo sci2 = { .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    vkCreateSemaphore(app->vk_device, &sci2, NULL, &app->vk_acquire_sem);
    vkCreateSemaphore(app->vk_device, &sci2, NULL, &app->vk_present_sem);

    return true;
}
```

- [ ] **Step 3: app_init で swapchain 作成、app_shutdown で破棄**

`app_init` の `sg_setup` 呼び出しの**直前**に `if (!create_swapchain(app)) return false;` を入れる。`color_format` は実 swapchain format に応じて決める:

```c
sg_pixel_format color_pf =
    (app->vk_swapchain_format == VK_FORMAT_B8G8R8A8_UNORM) ? SG_PIXELFORMAT_BGRA8 :
    (app->vk_swapchain_format == VK_FORMAT_R8G8B8A8_UNORM) ? SG_PIXELFORMAT_RGBA8 :
    SG_PIXELFORMAT_BGRA8;

sg_setup(&(sg_desc){
    .environment = {
        .defaults = {
            .color_format = color_pf,
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
        },
        .vulkan = { /* 既存 */ },
    },
    .logger.func = sglua_sokol_logger,
});
```

`app_shutdown` の sokol shutdown 後・`vkDestroyDevice` 前に追加:

```c
vkDeviceWaitIdle(app->vk_device);
if (app->vk_present_sem) vkDestroySemaphore(app->vk_device, app->vk_present_sem, NULL);
if (app->vk_acquire_sem) vkDestroySemaphore(app->vk_device, app->vk_acquire_sem, NULL);
if (app->vk_depth_view)  vkDestroyImageView(app->vk_device, app->vk_depth_view, NULL);
if (app->vk_depth_image) vkDestroyImage(app->vk_device, app->vk_depth_image, NULL);
if (app->vk_depth_mem)   vkFreeMemory(app->vk_device, app->vk_depth_mem, NULL);
for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
    vkDestroyImageView(app->vk_device, app->vk_swapchain_views[i], NULL);
}
free(app->vk_swapchain_views);
free(app->vk_swapchain_images);
if (app->vk_swapchain) vkDestroySwapchainKHR(app->vk_device, app->vk_swapchain, NULL);
if (app->vk_surface)   vkDestroySurfaceKHR(app->vk_instance, app->vk_surface, NULL);
```

- [ ] **Step 4: app_frame_begin で acquire、app_frame_end で present**

```c
void app_frame_begin(App *app, int *out_w, int *out_h) {
    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;
    pass_state_set_swapchain_size(&app->pass, w, h);

    vkAcquireNextImageKHR(app->vk_device, app->vk_swapchain, UINT64_MAX,
                          app->vk_acquire_sem, VK_NULL_HANDLE, &app->vk_current_image);
}

void app_frame_end(App *app) {
    sg_commit();
    VkPresentInfoKHR pi = {
        .sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
        .waitSemaphoreCount = 1,
        .pWaitSemaphores = &app->vk_present_sem,
        .swapchainCount = 1,
        .pSwapchains = &app->vk_swapchain,
        .pImageIndices = &app->vk_current_image,
    };
    vkQueuePresentKHR(app->vk_queue, &pi);
    app->frame_index++;
}
```

注: `present_sem` は sokol が pass の最後で signal するセマフォ。`acquire_sem` は sokol が pass の最初で wait するセマフォ。これらを **`sg_swapchain.vulkan`** に渡す (Step 5)。

- [ ] **Step 5: pass.h / pass.c を Vulkan swapchain ベースに変更**

`PassState` に Vulkan swapchain の参照を持たせ、`pass_state_begin_main` で `sg_swapchain.vulkan` を組み立てる。**App ポインタを PassState に保持** するのが簡単。

`src/pass.h`:

```c
#pragma once
#include <stdbool.h>

struct App; // 前方宣言

typedef struct PassState {
    bool in_pass;
    int swapchain_w, swapchain_h;
    struct App *app;     // swapchain handle 取得用
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_app(PassState *p, struct App *app);
void pass_state_set_swapchain_size(PassState *p, int w, int h);
bool pass_state_in_pass(const PassState *p);
void pass_state_begin_main(PassState *p, float r, float g, float b, float a);
void pass_state_end(PassState *p);
```

`src/pass.c`:

```c
#include "pass.h"
#include "app.h"
#include "sokol_gfx.h"
#include <SDL3/SDL.h>

void pass_state_init(PassState *p) { *p = (PassState){0}; }
void pass_state_set_app(PassState *p, struct App *app) { p->app = app; }
void pass_state_set_swapchain_size(PassState *p, int w, int h) {
    p->swapchain_w = w; p->swapchain_h = h;
}
bool pass_state_in_pass(const PassState *p) { return p->in_pass; }

void pass_state_begin_main(PassState *p, float r, float g, float b, float a) {
    if (p->in_pass) { SDL_Log("begin_pass nested"); return; }
    App *app = p->app;
    sg_pass pass = {
        .action.colors[0] = {
            .load_action = SG_LOADACTION_CLEAR,
            .clear_value = {r, g, b, a},
        },
        .swapchain = {
            .width = p->swapchain_w,
            .height = p->swapchain_h,
            .color_format = SG_PIXELFORMAT_BGRA8,    // app の swapchain format に合わせる
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
            .vulkan = {
                .render_image = (const void*)app->vk_swapchain_images[app->vk_current_image],
                .render_view  = (const void*)app->vk_swapchain_views[app->vk_current_image],
                .depth_stencil_image = (const void*)app->vk_depth_image,
                .depth_stencil_view  = (const void*)app->vk_depth_view,
                .render_finished_semaphore = (const void*)app->vk_present_sem,
                .present_complete_semaphore = (const void*)app->vk_acquire_sem,
            },
        },
    };
    sg_begin_pass(&pass);
    p->in_pass = true;
}

void pass_state_end(PassState *p) {
    if (!p->in_pass) { SDL_Log("end_pass without begin"); return; }
    sg_end_pass();
    p->in_pass = false;
}
```

`color_format` を `app->vk_swapchain_format` から推論するヘルパを別途用意してもよいが、PoC は BGRA8 固定でよい (Step 3 で BGRA8 を選んでいるため)。

- [ ] **Step 6: app_init で pass_state_set_app を呼ぶ**

`pass_state_init` 直後に:

```c
pass_state_set_app(&app->pass, app);
```

- [ ] **Step 7: ビルド & smoke test**

```bash
cmake --build build -j
./build/sglua samples/00b_clear.lua
```

期待: ディスプレイ環境ならウィンドウが虹色クリアでアニメーションする。ヘッドレスでは:

```bash
SDL_VIDEODRIVER=offscreen timeout 2 ./build/sglua samples/00b_clear.lua
```
exit 124、エラーなし。

注: SDL3 の offscreen driver は Vulkan 対応してない場合がある。その場合は Task 20 で lavapipe + SDL3 dummy/x11 driver の組合せに切替える。ここでは「ビルド clean、ディスプレイあれば動く」までで OK。

- [ ] **Step 8: コミット**

```bash
git add src/app.h src/app.c src/pass.h src/pass.c
git commit -m "feat(vk): create swapchain with depth attachment and per-frame acquire/present"
```

---

## Task 16: Slang を SPIR-V ターゲットに切替

**Goal:** `src/shader.cpp` の Slang ターゲットを `SLANG_GLSL`/`glsl_330` から `SLANG_SPIRV`/`spirv_1_5` (or appropriate) に変更し、`downversion_glsl` 関数を完全削除。`sg_shader_desc.vertex_func.bytecode` / `.fragment_func.bytecode` に SPIR-V binary を渡すように切替える。reflection は target 非依存なのでロジックは流用。

**Files:**
- Modify: `src/shader.cpp`

- [ ] **Step 1: TargetDesc 切替**

`shader.cpp` 内の Slang セッション作成で、target を切替える:

```cpp
slang::TargetDesc target_desc = {};
target_desc.format  = SLANG_SPIRV;
target_desc.profile = global_session->findProfile("spirv_1_5");
// target_desc.flags = SLANG_TARGET_FLAG_GENERATE_SPIRV_DIRECTLY; // 必要なら
```

`spirv_1_5` が無ければ `glsl_450` でも Slang は SPIR-V 出力する (target.format = SLANG_SPIRV を見るので)。実際には `spirv_1_3` あたりが最も互換性が広い。

- [ ] **Step 2: getEntryPointCode が SPIR-V を返すようになるので blob はバイナリ扱いに**

```cpp
// 旧:
// std::string vs_glsl(reinterpret_cast<const char*>(vs_blob->getBufferPointer()), vs_blob->getBufferSize());
// vs_glsl = downversion_glsl(vs_glsl.c_str(), vs_glsl.size());

// 新: SPIR-V はバイナリ。文字列扱い禁止
const void *vs_spv = vs_blob->getBufferPointer();
size_t      vs_spv_size = vs_blob->getBufferSize();
const void *fs_spv = fs_blob->getBufferPointer();
size_t      fs_spv_size = fs_blob->getBufferSize();
```

`vs_blob` / `fs_blob` (Slang の `IBlob`) は `shader_compile_and_create` の終端まで生存する必要がある。**ローカル変数として保持し、`sg_make_shader` の前に解放しないこと。**

- [ ] **Step 3: sg_shader_desc に bytecode を渡す**

```cpp
sg_shader_desc desc = {};
desc.vertex_func.entry = "main";    // Slang は entry 名を main に出力する
desc.vertex_func.bytecode.ptr  = vs_spv;
desc.vertex_func.bytecode.size = vs_spv_size;
desc.fragment_func.entry = "main";
desc.fragment_func.bytecode.ptr  = fs_spv;
desc.fragment_func.bytecode.size = fs_spv_size;
// .source は使わない (削除 or 0 のまま)
```

(GL backend では `desc.vertex_func.source = vs_glsl_str.c_str()` を渡していたが、Vulkan backend は bytecode 一択。)

- [ ] **Step 4: vertex attribute / uniform block / image_sampler の name 系設定を整理**

Vulkan の sokol backend では SPIR-V の binding 番号で識別するので、`attrs[i].name` / `glsl_name` / `glsl_uniforms[]` などは不要。`sg_shader_desc.attrs[]` の `.name` フィールドが残っているか sokol_gfx.h で確認。Vulkan backend では多くの場合不要 (空文字でよい)。

具体的には:
- 旧: `desc.attrs[i].name = name; desc.attrs[i].glsl_name = name;`
- 新: `desc.attrs[i] = (sg_shader_vertex_attr_desc){0};` でも動く。

`uniform_blocks[i].layout = SG_UNIFORMLAYOUT_STD140;` と `.size = ub.size_floats * sizeof(float);` は維持。`glsl_uniforms[]` は GL 専用なので削除。

`image_sampler_pairs[i]` は `view_slot` / `sampler_slot` / `stage` を使う。`glsl_name` は GL 専用 → 削除可。

- [ ] **Step 5: downversion_glsl 関数および <regex> インクルードを削除**

`downversion_glsl` 関数定義と呼び出しすべてを削除。`#include <regex>` も削除。

- [ ] **Step 6: ビルド & 動作確認**

```bash
cmake --build build -j
./build/sglua samples/01_triangle.lua
```

ディスプレイ環境: オレンジ三角形が描画される。ヘッドレス:

```bash
SDL_VIDEODRIVER=offscreen timeout 2 ./build/sglua samples/01_triangle.lua
```
exit 124、エラーなし。

(Vulkan validation layer を有効化していれば、SPIR-V バリデーションエラーがあれば logger に出る。エラーが出る場合は `desc.vertex_func.entry` が "main" でない可能性 — Slang が出す SPIR-V の entry 名は通常 "main"、Slang にカスタム entry 名を出させたい場合は `EntryPointDesc::name` を指定する。)

- [ ] **Step 7: コミット**

```bash
git add src/shader.cpp
git commit -m "feat(vk): switch slang target to spirv and drop glsl downversion hacks"
```

---

## Task 17: 残るサンプル動作確認 (vertex color / texture / uniform)

**Goal:** Sample 02/03/04 が新しい SPIR-V + Vulkan backend で動く。reflection ロジックや `sg_shader_desc` 詰める部分が attribute / texture / uniform で正しく機能することを確認。問題があれば直す。

**Files:**
- Modify: `src/shader.cpp` (必要に応じて binding/location 反映の調整)

- [ ] **Step 1: samples/02 動作確認**

```bash
./build/sglua samples/02_vertex_color.lua
```

期待: RGB 補間された三角形が描かれる。エラーが出る場合:
- vertex attribute の `location` が SPIR-V と pipeline desc で不一致 → `attrs[i].name` 指定が機能してない可能性。reflection で取得した `slot` (= location) が正しく `desc.layout.attrs[refl->attrs[i].slot]` のインデックスに対応しているか確認 (これは pipeline.c の責務、変更不要のはず)。

- [ ] **Step 2: samples/03 動作確認**

```bash
./build/sglua samples/03_texture.lua
```

期待: チェッカー三角形。エラーが出る場合:
- SPIR-V では descriptor set 0 + binding N で texture/sampler 識別。reflection の `img_slot` / `smp_slot` が SPIR-V binding と一致してるか確認。
- Slang の SPIR-V 出力では、`Texture2D diffuse;` は binding 0、`SamplerState diffuse_smp;` は別 binding になる (HLSL→SPIR-V の通常)。`sg_image_sampler_pair_desc` の `view_slot` / `sampler_slot` がそれぞれの binding 番号と一致するように。

- [ ] **Step 3: samples/04 動作確認**

```bash
./build/sglua samples/04_mvp.lua
```

期待: 回転する RGB 三角形。エラーが出る場合:
- uniform block は SPIR-V では UBO bound。`uniform_blocks[0].size` / `.layout = SG_UNIFORMLAYOUT_STD140` が正しいか。`sg_apply_uniforms(ub_slot, range)` の slot = SPIR-V binding 番号。

- [ ] **Step 4: 4 sample すべて headless smoke test**

```bash
for s in samples/0[1-4]_*.lua; do
    SDL_VIDEODRIVER=offscreen timeout 2 ./build/sglua "$s" 2>&1 | head -5
    echo "EXIT=$?"
done
```

期待: 各 EXIT=124、エラーログなし。

- [ ] **Step 5: 必要に応じて shader.cpp を修正**

問題があった場合、reflection の binding 取得部分や `sg_shader_desc` 詰める部分を Vulkan に合わせて修正。具体的には Slang reflection の以下のメソッドを使う:

- `parameter->getCategory()` で `DescriptorTableSlot` / `ConstantBuffer` 等を判定
- `parameter->getBindingIndex()` で SPIR-V の binding 番号取得
- `parameter->getBindingSpace()` で descriptor set 番号取得

GL では `getOffset(SLANG_PARAMETER_CATEGORY_UNIFORM)` を使っていたが、Vulkan では `getBindingIndex()` 系。

- [ ] **Step 6: コミット**

```bash
git add src/shader.cpp
git commit -m "feat(vk): adjust reflection bindings for spirv samples 02-04"
```

(変更が無ければスキップして次タスクへ。)

---

## Task 18: lavapipe 経由 headless 動作確認

**Goal:** Mesa の lavapipe を ICD として指定し、GPU 不要で 4 サンプルが動作することを確認。実行スクリプトを `scripts/run-headless.sh` として用意。

**Files:**
- Create: `scripts/run-headless.sh`

- [ ] **Step 1: lavapipe をシステムに用意**

Arch Linux: `sudo pacman -S vulkan-swrast`
Debian/Ubuntu: `sudo apt install mesa-vulkan-drivers`

ICD JSON の場所を確認:

```bash
ls /usr/share/vulkan/icd.d/ | grep -i lvp
# 期待: lvp_icd.x86_64.json (or similar)
```

- [ ] **Step 2: scripts/run-headless.sh を作成**

```bash
#!/usr/bin/env bash
# headless 実行: Mesa lavapipe (CPU Vulkan) を ICD として強制指定。
# SDL は dummy video driver で OK (Vulkan loader は SDL とは独立)。

set -euo pipefail

ICD=/usr/share/vulkan/icd.d/lvp_icd.x86_64.json
if [[ ! -f "$ICD" ]]; then
    echo "lavapipe ICD not found at $ICD" >&2
    echo "Install with: sudo pacman -S vulkan-swrast (Arch) or sudo apt install mesa-vulkan-drivers (Debian)" >&2
    exit 1
fi

export VK_ICD_FILENAMES="$ICD"
export VK_LOADER_DRIVERS_SELECT=lvp_icd.x86_64.json
export SDL_VIDEODRIVER="${SDL_VIDEODRIVER:-x11}"  # x11 が無ければ dummy にフォールバック (Step 3)

exec "${1:-./build/sglua}" "${@:2}"
```

(Note: SDL3 で `SDL_WINDOW_VULKAN` を作るには SDL の video driver が VULKAN extension を提供する必要がある。x11 / wayland / cocoa は OK。`offscreen` / `dummy` は Vulkan 対応していない場合があるので、ヘッドレス CI では Xvfb と組み合わせるのが堅い。)

- [ ] **Step 3: 必要なら Xvfb と組合せ**

ディスプレイ無し CI では:

```bash
xvfb-run -a scripts/run-headless.sh ./build/sglua samples/01_triangle.lua
```

`xvfb-run` で X server を立てて、SDL3 が x11 video driver で window を作る → window は X 上に存在 → SDL_Vulkan_CreateSurface が成功 → Vulkan 側は lavapipe で実行 (実 GPU には到達しない)。

これを `scripts/run-headless.sh` に統合してもよい:

```bash
if ! command -v xvfb-run >/dev/null && [[ -z "${DISPLAY:-}" ]]; then
    echo "no DISPLAY and no xvfb-run; cannot create SDL window" >&2
    exit 2
fi
if [[ -z "${DISPLAY:-}" ]]; then
    exec xvfb-run -a "$0" "$@"
fi
# ... 上記の本体 ...
```

- [ ] **Step 4: 実行確認**

```bash
chmod +x scripts/run-headless.sh
scripts/run-headless.sh ./build/sglua samples/01_triangle.lua &
sleep 2
kill $!
```

期待: エラーなし、`vkCreateInstance` 等が成功している (logger に WARN/PANIC が出ない)。

```bash
# 4 sample 全部
for s in samples/0[1-4]_*.lua; do
    timeout 2 scripts/run-headless.sh ./build/sglua "$s" || true
done
```

期待: 各サンプル exit 124 (or 137 for SIGKILL from timeout)、エラーログなし。

- [ ] **Step 5: コミット**

```bash
chmod +x scripts/run-headless.sh
git add scripts/run-headless.sh
git commit -m "feat(headless): add lavapipe-based run script for software vulkan"
```

---

## Task 19: 残置 GL artifact のクリーンアップ + README 更新

**Goal:** GL 関連の dead code / コメント / 依存をすべて掃除。README.md に Vulkan + headless 構成を反映。

**Files:**
- Modify: `src/shader.cpp` (`<regex>` include / downversion_glsl が残っていれば削除)
- Modify: `src/app.c` (`SDL_GL_*` 残存があれば削除)
- Modify: `CMakeLists.txt` (find_package(OpenGL) / OpenGL::GL の残存があれば削除)
- Modify: `README.md`

- [ ] **Step 1: dead code 検索**

```bash
grep -n "OpenGL::GL\|find_package(OpenGL\|SDL_GL_\|SDL_WINDOW_OPENGL\|SOKOL_GLCORE\|downversion_glsl\|#include <regex>" \
    CMakeLists.txt src/*.c src/*.cpp src/*.h
```

期待: マッチなし (もしあれば削除)。

- [ ] **Step 2: README.md を更新**

`## ビルド` セクションに「Vulkan SDK or Vulkan loader (libvulkan)」を依存に追加。`## 実行` セクションに headless 手順を追記:

```markdown
## ビルド

依存:
- CMake 3.20+
- C11 / C++17 対応コンパイラ
- Vulkan loader (`libvulkan` — Linux: `vulkan-icd-loader` / `vulkan-loader`)
- Linux x86_64 (現状)

```sh
cmake -S . -B build
cmake --build build -j
```

## 実行

通常 (実 GPU 経由):
```sh
./build/sglua samples/01_triangle.lua
```

ヘッドレス (Mesa lavapipe = CPU Vulkan):
```sh
# 事前: sudo pacman -S vulkan-swrast (Arch) / sudo apt install mesa-vulkan-drivers (Debian)
scripts/run-headless.sh ./build/sglua samples/01_triangle.lua
```

CI 等の DISPLAY 無し環境では `xvfb-run` でラップする:
```sh
xvfb-run -a scripts/run-headless.sh ./build/sglua samples/01_triangle.lua
```
```

`## アーキテクチャ` の図中の「sokol_gfx (GL 3.3)」を「sokol_gfx (Vulkan)」に、「Slang→GLSL」を「Slang→SPIR-V」に更新。

- [ ] **Step 3: 4 sample 最終 smoke test**

```bash
for s in samples/0[1-4]_*.lua; do
    timeout 2 scripts/run-headless.sh ./build/sglua "$s"
    echo "  $s -> $?"
done
```

期待: 全部 124 (or SIGKILL から 137)、エラーログなし。

- [ ] **Step 4: コミット**

```bash
git add CMakeLists.txt src/shader.cpp src/app.c README.md
git commit -m "docs: vulkan migration cleanup and readme update"
```

---

## 計画外 (Phase 2 で先送り)

- MSAA 対応 (`sg_swapchain.vulkan.resolve_image/view` を使う)
- swapchain recreate (window resize 対応): 現状はリサイズ無視。対応するなら `vkAcquireNextImageKHR` の `VK_ERROR_OUT_OF_DATE_KHR` を捕捉して swapchain 作り直す
- フレーム並列度 > 1 (per-frame semaphore array, fence、frame in flight)
- VK_KHR_headless_surface 使用 (X server 不要パス) — Mesa lavapipe は対応してるが SDL3 が surface 作成しないので別途 vkCreateHeadlessSurfaceEXT を呼ぶ実装が必要。Phase 3 で
- macOS / Windows (MoltenVK / dxvk 経由) — 移植
- Vulkan validation layers の自動有効化 (DEBUG ビルド時)
