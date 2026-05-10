# SDL3 GPU backend 両立 実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lua の `config({backend="sokol"|"sdlgpu"})` を `on_init` 内で呼ぶことで sokol_gfx (現行) と SDL3 GPU API を切り替えられるようにし、サンプル 01〜04 + capture を両 backend で動作させる。

**Architecture:** 内部に `RenderBackend` 関数ポインタ vtable を導入し、`backend_sokol.c` (現行コードをラップ) と `backend_sdlgpu.c` (新規) の 2 実装を持つ。`pass.c` / `pipeline.c` / `resources.c` / `capture.c` / `lua_api.c` は全て `g_backend->xxx()` を経由する薄い glue にする。SDL window 作成 → Lua on_init → backend 確定 → backend->init → 描画ループ、の順に初期化フローを再構成する。

**Tech Stack:** C11 / C++17, SDL3 (`SDL_Vulkan_*` + `SDL_GPU*`), sokol_gfx (Vulkan), Slang→SPIR-V, lavapipe (lavapipe-test)。

---

## ファイル構成

**新規作成:**
- `src/backend.h` — `RenderBackend` interface + 共通 desc 構造体 + `g_backend` extern
- `src/backend_sokol.c` — 現行実装を vtable に sun
- `src/backend_sdlgpu.c` — SDL3 GPU 版

**変更:**
- `src/app.h`, `src/app.c` — Vulkan 状態を `backend_sokol.c` 側に移譲、`config()` を受けて backend を切り替えるフロー
- `src/lua_api.c` — `config()` API 追加、`use_*` / `draw` / `begin_pass` / `end_pass` / `capture` を `g_backend->xxx` 経由に
- `src/pass.c` — 内部状態 (in_pass フラグ等) のみ残し、実装は backend へ
- `src/pipeline.c` — pipeline state hash → opaque handle の cache だけ残し、生成は `g_backend->make_pipeline`
- `src/resources.c`, `src/resources.h` — `ResEntry.handle` を `uintptr_t` 一本化
- `src/capture.c` — pending path の管理だけ。書出は `g_backend->capture()` に委譲
- `src/shader.h`, `src/shader.cpp` — sokol 依存を切り、SPIR-V blob と reflection を返す API に変える
- `CMakeLists.txt` — sources に `backend_sokol.c`, `backend_sdlgpu.c` 追加
- `samples/01_triangle.lua` 〜 `samples/04_mvp.lua` — `config({backend = arg[1] or os.getenv("SGLUA_BACKEND") or "sokol"})` を `on_init` に追加
- `scripts/run-headless.sh` — `SGLUA_BACKEND` 環境変数を伝播
- `README.md` — backend 切替方法と既知制約を追記

---

## Task 1: `RenderBackend` interface 定義 + sokol 側を vtable 化

**Goal:** 全 GPU 呼び出しを `g_backend->xxx()` 経由にする。中身は現行の sokol_gfx + Vulkan 直叩きをそのまま `backend_sokol.c` に詰めただけで、挙動はゼロリグレッション。`g_backend = &SOKOL` 固定。

**Files:**
- Create: `src/backend.h`
- Create: `src/backend_sokol.c`
- Modify: `src/app.h`, `src/app.c`
- Modify: `src/pass.c`, `src/pipeline.c`, `src/resources.h`, `src/resources.c`, `src/capture.c`, `src/lua_api.c`
- Modify: `src/shader.h`, `src/shader.cpp`
- Modify: `CMakeLists.txt`

- [ ] **Step 1: `src/backend.h` を新規作成**

```c
#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "enums.h"
#include "shader.h"

#ifdef __cplusplus
extern "C" {
#endif

struct App;

// Opaque handles. Each backend casts integer IDs or pointers into uintptr_t.
typedef uintptr_t BackendBuffer;
typedef uintptr_t BackendImage;
typedef uintptr_t BackendShader;
typedef uintptr_t BackendPipeline;

typedef struct ImageDesc {
    SglPixelFormat fmt;
    int w, h;
    const uint8_t *data;
    size_t data_bytes;
} ImageDesc;

typedef struct ShaderDesc {
    const uint32_t *vs_spirv; size_t vs_bytes;
    const uint32_t *fs_spirv; size_t fs_bytes;
    const ShaderReflection *refl;
} ShaderDesc;

typedef struct PipelineDesc {
    BackendShader shader;
    const ShaderReflection *refl;
    SglBlend blend;
    bool depth_test;
    bool depth_write;
    SglCull cull;
    SglPrimitive primitive;
    SglPixelFormat color_fmt;
} PipelineDesc;

typedef struct PassBeginDesc {
    BackendImage target;          // 0 = main_tex
    float clear[4];
} PassBeginDesc;

typedef struct BindingsDesc {
    const ShaderReflection *refl; // textures slot 解決のため (NULL なら texture binding スキップ)
    BackendBuffer vbuf;           // 0 = none
    int texture_count;
    struct {
        const char *name;         // matches reflection
        BackendImage image;
    } textures[8];
} BindingsDesc;

typedef struct RenderBackend {
    const char *name;

    bool (*init)(struct App *app);
    void (*shutdown)(struct App *app);

    void (*begin_frame)(struct App *app, int *out_w, int *out_h);
    void (*end_frame)(struct App *app);

    BackendBuffer   (*make_buffer)(SglBufferType type, const float *data, size_t bytes);
    BackendImage    (*make_image)(const ImageDesc *desc);
    BackendShader   (*make_shader)(const ShaderDesc *desc);
    BackendPipeline (*make_pipeline)(const PipelineDesc *desc);

    void (*destroy_buffer)(BackendBuffer);
    void (*destroy_image)(BackendImage);
    void (*destroy_shader)(BackendShader);
    void (*destroy_pipeline)(BackendPipeline);

    void (*begin_pass)(const PassBeginDesc *);
    void (*end_pass)(void);

    void (*apply_pipeline)(BackendPipeline);
    void (*apply_bindings)(const BindingsDesc *);
    void (*apply_uniforms)(const void *data, size_t bytes);
    void (*draw)(int base, int count);

    bool (*capture)(struct App *app, const char *path);

    // pipeline cache が key にする swapchain color format を問い合わせる。
    // sokol/sdlgpu 双方が現フレームの swapchain image format を返す。
    SglPixelFormat (*swapchain_color_format)(struct App *app);
} RenderBackend;

extern const RenderBackend *g_backend;
extern const RenderBackend g_backend_sokol;
extern const RenderBackend g_backend_sdlgpu;  // populated in Task 3

#ifdef __cplusplus
}
#endif
```

- [ ] **Step 2: `src/shader.h` から sokol 依存を切る**

shader が `sg_shader` を直接返すと backend 抽象が壊れるので、SPIR-V blob と reflection を返す形に変える。

```c
// src/shader.h (差し替え)
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SGL_MAX_ATTRS 8
#define SGL_MAX_UB_MEMBERS 32
#define SGL_MAX_TEXTURES 8
#define SGL_MAX_UNIFORM_BLOCKS 2

typedef struct ShaderAttr {
    char name[32];
    int slot;
    int comp_count;
    int offset_floats;
} ShaderAttr;

typedef struct ShaderUniformMember {
    char name[32];
    int offset_floats;
    int comp_count;
} ShaderUniformMember;

typedef struct ShaderUniformBlock {
    char name[32];
    int slot;
    int size_floats;
    int member_count;
    ShaderUniformMember members[SGL_MAX_UB_MEMBERS];
} ShaderUniformBlock;

typedef struct ShaderTexture {
    char name[32];
    int img_slot;
    int smp_slot;
} ShaderTexture;

typedef struct ShaderReflection {
    int attr_count;
    ShaderAttr attrs[SGL_MAX_ATTRS];
    int ub_count;
    ShaderUniformBlock ubs[SGL_MAX_UNIFORM_BLOCKS];
    int tex_count;
    ShaderTexture texs[SGL_MAX_TEXTURES];
    int vertex_stride_floats;
} ShaderReflection;

typedef struct ShaderBlob {
    uint32_t *spirv;   // malloc'd, owner = caller (free with shader_blob_free)
    size_t bytes;
} ShaderBlob;

bool shader_compile(
    const char *vs_src, const char *fs_src,
    ShaderBlob *out_vs, ShaderBlob *out_fs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size);

void shader_blob_free(ShaderBlob *b);

#ifdef __cplusplus
}
#endif
```

`shader.cpp` の `shader_compile_and_create` を `shader_compile` にリネーム:
- 内部の Slang compile + reflection 抽出は流用
- 末尾の `sg_make_shader` 呼び出しを削除し、SPIR-V を `ShaderBlob` に格納して返す
- 既存の `set 0 → set 1` patching は SPIR-V 段階で実施 (現行のまま)

```cpp
// shader.cpp 末尾の差分: sg_make_shader を作らず blob を返す
bool shader_compile(
    const char *vs_src, const char *fs_src,
    ShaderBlob *out_vs, ShaderBlob *out_fs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size)
{
    // ... 既存の slang セットアップと vs/fs compile ...
    // patch_spv_descriptor_sets() 呼び出しまで現行のまま ...

    out_vs->spirv = (uint32_t*)malloc(vs_size);
    if (!out_vs->spirv) { snprintf(err_buf, err_buf_size, "OOM vs"); return false; }
    memcpy(out_vs->spirv, vs_code, vs_size);
    out_vs->bytes = vs_size;

    out_fs->spirv = (uint32_t*)malloc(fs_size);
    if (!out_fs->spirv) { free(out_vs->spirv); out_vs->spirv=NULL; snprintf(err_buf, err_buf_size, "OOM fs"); return false; }
    memcpy(out_fs->spirv, fs_code, fs_size);
    out_fs->bytes = fs_size;

    *out_refl = refl;
    return true;
}

void shader_blob_free(ShaderBlob *b) {
    if (b && b->spirv) { free(b->spirv); b->spirv = NULL; b->bytes = 0; }
}
```

- [ ] **Step 3: `src/resources.h` の `ResEntry` を opaque ハンドル化**

```c
// resources.h (union を再構成)
#pragma once
#include "enums.h"
#include "shader.h"
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

typedef enum { RES_NONE = 0, RES_BUFFER, RES_TEXTURE, RES_SHADER } ResKind;

#define RES_BUCKETS 256

typedef struct ResEntry {
    char *key;
    ResKind kind;
    int version;
    int64_t last_seen_frame;
    union {
        struct { uintptr_t h; SglBufferType type; size_t size_bytes; } buf;
        struct { uintptr_t h; int w, h_; SglPixelFormat fmt; } tex;
        struct { uintptr_t h; ShaderReflection refl; } sh;
    } u;
    struct ResEntry *next;
} ResEntry;

typedef struct ResTable {
    ResEntry *buckets[RES_BUCKETS];
} ResTable;

void res_table_init(ResTable *t);
void res_table_shutdown(ResTable *t);

ResEntry *res_table_get(ResTable *t, const char *key);
ResEntry *res_table_get_or_create(ResTable *t, const char *key, ResKind kind);
void res_table_touch(ResEntry *e, int64_t frame_index);
```

`resources.c` の destroy ロジックを `g_backend->destroy_*` に置き換える:

```c
// resources.c の res_table_shutdown / 内部 destroy 部分
#include "resources.h"
#include "backend.h"
#include <stdlib.h>
#include <string.h>

// ...既存のハッシュ実装は流用...

static void res_entry_release(ResEntry *e) {
    switch (e->kind) {
        case RES_BUFFER:
            if (e->u.buf.h)  g_backend->destroy_buffer(e->u.buf.h);
            break;
        case RES_TEXTURE:
            if (e->u.tex.h)  g_backend->destroy_image(e->u.tex.h);
            break;
        case RES_SHADER:
            if (e->u.sh.h)   g_backend->destroy_shader(e->u.sh.h);
            break;
        default: break;
    }
    free(e->key);
    free(e);
}

void res_table_shutdown(ResTable *t) {
    for (int i = 0; i < RES_BUCKETS; ++i) {
        ResEntry *e = t->buckets[i];
        while (e) {
            ResEntry *n = e->next;
            res_entry_release(e);
            e = n;
        }
        t->buckets[i] = NULL;
    }
}
```

- [ ] **Step 4: `src/pipeline.c` を vtable 経由に書き換え**

cache の hash key と `pipeline_cache_get` 構造は流用するが、`sg_pipeline` の代わりに `BackendPipeline` を扱い、生成は `g_backend->make_pipeline()` に投げる。

```c
// pipeline.h (差分: handle を BackendPipeline に変更)
#pragma once
#include "backend.h"
#include "shader.h"
#include <stdint.h>

#define PIPELINE_BUCKETS 64

typedef struct PipelineKey {
    uintptr_t shader_handle;
    uint8_t blend, depth_test, depth_write, cull, primitive, color_fmt, _pad[2];
} PipelineKey;

typedef struct PipelineEntry {
    PipelineKey key;
    BackendPipeline pip;
    struct PipelineEntry *next;
} PipelineEntry;

typedef struct PipelineCache {
    PipelineEntry *buckets[PIPELINE_BUCKETS];
} PipelineCache;

void pipeline_cache_init(PipelineCache *c);
void pipeline_cache_shutdown(PipelineCache *c);
BackendPipeline pipeline_cache_get(
    PipelineCache *c, BackendShader sh, const ShaderReflection *refl,
    SglBlend blend, bool dt, bool dw, SglCull cull, SglPrimitive prim,
    SglPixelFormat cfmt);
```

```c
// pipeline.c
#include "pipeline.h"
#include "backend.h"
#include <string.h>
#include <stdlib.h>

_Static_assert((PIPELINE_BUCKETS & (PIPELINE_BUCKETS - 1)) == 0, "POW2");

void pipeline_cache_init(PipelineCache *c) { memset(c, 0, sizeof(*c)); }

void pipeline_cache_shutdown(PipelineCache *c) {
    for (int i = 0; i < PIPELINE_BUCKETS; ++i) {
        PipelineEntry *e = c->buckets[i];
        while (e) {
            PipelineEntry *n = e->next;
            if (e->pip) g_backend->destroy_pipeline(e->pip);
            free(e);
            e = n;
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

BackendPipeline pipeline_cache_get(
    PipelineCache *c, BackendShader sh, const ShaderReflection *refl,
    SglBlend blend, bool dt, bool dw, SglCull cull, SglPrimitive prim,
    SglPixelFormat cfmt)
{
    PipelineKey k; memset(&k, 0, sizeof(k));
    k.shader_handle = sh;
    k.blend = (uint8_t)blend;
    k.depth_test = dt ? 1 : 0;
    k.depth_write = dw ? 1 : 0;
    k.cull = (uint8_t)cull;
    k.primitive = (uint8_t)prim;
    k.color_fmt = (uint8_t)cfmt;
    uint32_t bi = hash_key(&k) & (PIPELINE_BUCKETS - 1);
    for (PipelineEntry *e = c->buckets[bi]; e; e = e->next) {
        if (memcmp(&e->key, &k, sizeof(k)) == 0) return e->pip;
    }
    PipelineDesc desc = {
        .shader = sh, .refl = refl,
        .blend = blend, .depth_test = dt, .depth_write = dw,
        .cull = cull, .primitive = prim, .color_fmt = cfmt,
    };
    BackendPipeline pip = g_backend->make_pipeline(&desc);
    PipelineEntry *e = (PipelineEntry*)calloc(1, sizeof(*e));
    if (!e) return pip;
    e->key = k; e->pip = pip; e->next = c->buckets[bi];
    c->buckets[bi] = e;
    return pip;
}
```

- [ ] **Step 5: `src/pass.c` を vtable 経由に書き換え**

```c
// pass.c
#include "pass.h"
#include "backend.h"
#include <SDL3/SDL.h>

void pass_state_init(PassState *p) {
    p->in_pass = false; p->swapchain_w = 0; p->swapchain_h = 0; p->app = NULL;
}
void pass_state_set_app(PassState *p, struct App *app) { p->app = app; }
void pass_state_set_swapchain_size(PassState *p, int w, int h) {
    p->swapchain_w = w; p->swapchain_h = h;
}
bool pass_state_in_pass(const PassState *p) { return p->in_pass; }

void pass_state_begin_main(PassState *p, float r, float g, float b, float a) {
    if (p->in_pass) { SDL_Log("begin_pass: already in pass"); return; }
    PassBeginDesc d = { .target = 0, .clear = {r,g,b,a} };
    g_backend->begin_pass(&d);
    p->in_pass = true;
}

void pass_state_end(PassState *p) {
    if (!p->in_pass) { SDL_Log("end_pass: no matching begin"); return; }
    g_backend->end_pass();
    p->in_pass = false;
}
```

- [ ] **Step 6: `src/lua_api.c` の sokol 直叩きを vtable 経由に置き換え**

`l_use_buffer` / `l_use_texture` / `l_use_shader` / `l_draw` / `l_capture` を `g_backend` 呼び出しに変える。骨子は維持。例えば `l_use_buffer`:

```c
static int l_use_buffer(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    int type = (int)luaL_checkinteger(L, 2);
    luaL_checktype(L, 3, LUA_TTABLE);
    int version = (int)luaL_checkinteger(L, 4);
    if (type != SGL_BUFFER_VERTEX && type != SGL_BUFFER_INDEX)
        return luaL_error(L, "use_buffer: only VERTEX/INDEX");

    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_BUFFER);
    if (!e) return luaL_error(L, "use_buffer: kind mismatch '%s'", key);
    res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

    if (e->version == version && e->u.buf.h != 0) { push_buffer_ref(L, key); return 1; }

    int n = (int)lua_rawlen(L, 3);
    if (n <= 0) return luaL_error(L, "use_buffer: empty data");
    float *data = (float*)malloc((size_t)n * sizeof(float));
    if (!data) return luaL_error(L, "use_buffer: OOM");
    for (int i = 0; i < n; ++i) {
        lua_rawgeti(L, 3, i+1);
        data[i] = (float)lua_tonumber(L, -1);
        lua_pop(L, 1);
    }
    if (e->u.buf.h) g_backend->destroy_buffer(e->u.buf.h);
    e->u.buf.h = g_backend->make_buffer((SglBufferType)type, data, (size_t)n * sizeof(float));
    e->u.buf.type = (SglBufferType)type;
    e->u.buf.size_bytes = (size_t)n * sizeof(float);
    e->version = version;
    free(data);
    push_buffer_ref(L, key);
    return 1;
}
```

`l_use_shader` の中で `shader_compile` を呼び出し、blob を `g_backend->make_shader()` に渡す:

```c
static int l_use_shader(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    const char *vs  = luaL_checkstring(L, 2);
    const char *fs  = luaL_checkstring(L, 3);
    int version = (int)luaL_checkinteger(L, 4);
    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_SHADER);
    if (!e) return luaL_error(L, "use_shader: kind mismatch");
    res_table_touch(e, (int64_t)g_app_for_lua->frame_index);
    if (e->version == version && e->u.sh.h) { push_shader_ref(L, key); return 1; }

    char err[1024];
    ShaderBlob vsb, fsb; ShaderReflection refl;
    if (!shader_compile(vs, fs, &vsb, &fsb, &refl, err, sizeof(err)))
        return luaL_error(L, "shader compile error: %s", err);

    ShaderDesc d = { .vs_spirv = vsb.spirv, .vs_bytes = vsb.bytes,
                     .fs_spirv = fsb.spirv, .fs_bytes = fsb.bytes, .refl = &refl };
    if (e->u.sh.h) g_backend->destroy_shader(e->u.sh.h);
    e->u.sh.h = g_backend->make_shader(&d);
    e->u.sh.refl = refl;
    e->version = version;
    shader_blob_free(&vsb); shader_blob_free(&fsb);
    push_shader_ref(L, key);
    return 1;
}
```

`l_draw` も `pipeline_cache_get` → `g_backend->apply_pipeline` → bindings 構築 → `apply_bindings` → uniform pack → `apply_uniforms` → `draw` の流れに整理。`sg_*` 呼び出しは全て vtable に置換。詳細は既存ロジックを移植 (resources の name match で texture を入れる、uniform は ub[0] に pack)。

`l_capture`:
```c
static int l_capture(lua_State *L) {
    const char *path = luaL_checkstring(L, 1);
    capture_schedule(&g_app_for_lua->capture, path, 0);
    return 0;
}
```

(capture_schedule の中身は `g_backend->capture` を呼ぶ薄い委譲に、Step 7 で書き換える)

- [ ] **Step 7: `src/capture.h` / `src/capture.c` を委譲構造に縮める**

```c
// capture.h
#pragma once
#include <stdint.h>
#include <stdbool.h>

typedef struct CaptureState {
    char *pending_path;        // strdup'd or NULL
    uint64_t target_frame;     // 0 = next frame
    uint64_t scheduled_at;
} CaptureState;

void capture_state_init(CaptureState *c);
void capture_state_shutdown(CaptureState *c);
void capture_schedule(CaptureState *c, const char *path, uint64_t at_frame);
// app->frame_end が呼ぶ。pending があれば g_backend->capture を呼んで path をクリア。
// 戻り値: capture を実行したか。
bool capture_state_drain(CaptureState *c, struct App *app);
```

```c
// capture.c
#include "capture.h"
#include "backend.h"
#include "app.h"
#include <stdlib.h>
#include <string.h>

void capture_state_init(CaptureState *c) {
    c->pending_path = NULL; c->target_frame = 0; c->scheduled_at = 0;
}
void capture_state_shutdown(CaptureState *c) {
    free(c->pending_path); c->pending_path = NULL;
}
void capture_schedule(CaptureState *c, const char *path, uint64_t at_frame) {
    free(c->pending_path);
    c->pending_path = path ? strdup(path) : NULL;
    c->target_frame = at_frame;
}
bool capture_state_drain(CaptureState *c, struct App *app) {
    if (!c->pending_path) return false;
    if (app->frame_index < c->target_frame) return false;
    bool ok = g_backend->capture(app, c->pending_path);
    free(c->pending_path); c->pending_path = NULL;
    return ok;
}
```

(Vulkan 直叩きの copy-image-to-buffer ロジックは Step 8 で `backend_sokol.c` 内に移設)

- [ ] **Step 8: `src/backend_sokol.c` を作成 — 現行 sokol/Vulkan コードを集約**

`app.c` の Vulkan instance/device/swapchain 作成、`pass.c` 旧 `sg_begin_pass` 設定、`pipeline.c` 旧 `sg_make_pipeline`、`lua_api.c` の `sg_make_buffer/image/shader/apply_bindings/apply_uniforms/draw` 呼出、旧 `capture.c` の `vkCmdCopyImageToBuffer` フローを **`backend_sokol.c` 内に集約**して RenderBackend impl にする。

ファイル骨子:

```c
// backend_sokol.c
#include "backend.h"
#include "app.h"
#include "shader.h"
#include "sokol_gfx.h"
#include "stb_image_write.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>
#include <stdlib.h>
#include <string.h>

// app->vk_* に保持していた Vulkan 状態 (instance/device/swapchain etc.)
// は引き続き App に持たせる (Task 1 では app.h は変更しない)。
// SDL_GPU 側 (Task 3+) は別の状態を別途 App に追加する。

static bool sk_init(App *app) { /* 既存 app_init() の中身を移植 */ return true; }
static void sk_shutdown(App *app) { /* 既存 app_shutdown() の中身を移植 */ }
static void sk_begin_frame(App *app, int *w, int *h) { /* 既存 app_frame_begin */ }
static void sk_end_frame(App *app) { /* 既存 app_frame_end (sg_commit + present) */ }

static BackendBuffer sk_make_buffer(SglBufferType type, const float *data, size_t bytes) {
    sg_buffer h = sg_make_buffer(&(sg_buffer_desc){
        .size = bytes,
        .usage = {
            .vertex_buffer = (type == SGL_BUFFER_VERTEX),
            .index_buffer  = (type == SGL_BUFFER_INDEX),
            .immutable     = true,
        },
        .data = { .ptr = data, .size = bytes },
    });
    return (uintptr_t)h.id;
}
static void sk_destroy_buffer(BackendBuffer h) {
    sg_destroy_buffer((sg_buffer){ .id = (uint32_t)h });
}

// make_image: image+sampler+view を 1 構造体にまとめて pointer で返す
typedef struct SkImage { sg_image img; sg_sampler smp; sg_view view; } SkImage;
static BackendImage sk_make_image(const ImageDesc *d) {
    SkImage *si = (SkImage*)calloc(1, sizeof(SkImage));
    sg_pixel_format pf = (d->fmt == SGL_PF_R8) ? SG_PIXELFORMAT_R8 : SG_PIXELFORMAT_RGBA8;
    sg_image_desc img_desc = { .width = d->w, .height = d->h, .pixel_format = pf, .usage = { .immutable = true } };
    if (d->data) { img_desc.data.mip_levels[0] = (sg_range){ .ptr = d->data, .size = d->data_bytes }; }
    si->img  = sg_make_image(&img_desc);
    si->smp  = sg_make_sampler(&(sg_sampler_desc){ .min_filter = SG_FILTER_LINEAR, .mag_filter = SG_FILTER_LINEAR, .wrap_u = SG_WRAP_REPEAT, .wrap_v = SG_WRAP_REPEAT });
    si->view = sg_make_view(&(sg_view_desc){ .texture = { .image = si->img } });
    return (uintptr_t)si;
}
static void sk_destroy_image(BackendImage h) {
    SkImage *si = (SkImage*)h; if (!si) return;
    if (si->view.id) sg_destroy_view(si->view);
    if (si->img.id)  sg_destroy_image(si->img);
    if (si->smp.id)  sg_destroy_sampler(si->smp);
    free(si);
}

// make_shader: SPIR-V blob から sg_shader_desc を構築する。reflection は
// 旧 shader.cpp の sokol-side 構築ロジックを移植。
typedef struct SkShader { sg_shader sh; } SkShader;
static BackendShader sk_make_shader(const ShaderDesc *d) {
    SkShader *ss = (SkShader*)calloc(1, sizeof(SkShader));
    sg_shader_desc sd = {0};
    sd.vertex_func.bytecode  = (sg_range){ .ptr = d->vs_spirv, .size = d->vs_bytes };
    sd.fragment_func.bytecode= (sg_range){ .ptr = d->fs_spirv, .size = d->fs_bytes };
    sd.vertex_func.entry  = "vs_main";
    sd.fragment_func.entry= "fs_main";
    // 旧 shader.cpp で attrs / texs / ubs を sg_shader_desc に詰めていたコードを移植
    // (set / slot / stage 設定)。詳細は既存 shader.cpp の末尾参照。
    ss->sh = sg_make_shader(&sd);
    return (uintptr_t)ss;
}
static void sk_destroy_shader(BackendShader h) {
    SkShader *ss = (SkShader*)h;
    if (ss && ss->sh.id) sg_destroy_shader(ss->sh);
    free(ss);
}

static BackendPipeline sk_make_pipeline(const PipelineDesc *d) {
    SkShader *ss = (SkShader*)d->shader;
    // 旧 pipeline.c の to_sokol_blend / vertex layout 構築を移植
    // depth_format = SG_PIXELFORMAT_DEPTH_STENCIL 固定 (sokol 側で管理)
    sg_pipeline_desc desc = {0};
    desc.shader = ss->sh;
    desc.colors[0].pixel_format =
        (d->color_fmt == SGL_PF_RGBA8) ? SG_PIXELFORMAT_RGBA8 : SG_PIXELFORMAT_BGRA8;
    // ... blend / cull / primitive / vertex layout ...
    desc.depth.pixel_format = SG_PIXELFORMAT_DEPTH_STENCIL;
    desc.depth.compare = d->depth_test ? SG_COMPAREFUNC_LESS_EQUAL : SG_COMPAREFUNC_ALWAYS;
    desc.depth.write_enabled = d->depth_write;
    sg_pipeline pip = sg_make_pipeline(&desc);
    return (uintptr_t)pip.id;
}
static void sk_destroy_pipeline(BackendPipeline h) {
    sg_destroy_pipeline((sg_pipeline){ .id = (uint32_t)h });
}

// pass / bindings / uniforms / draw — 旧コードを移植
static App *g_pass_app;  // begin_pass で current swapchain image を取るため
static void sk_begin_pass(const PassBeginDesc *d) { /* 旧 pass.c 中身 */ }
static void sk_end_pass(void) { sg_end_pass(); }
static void sk_apply_pipeline(BackendPipeline p) {
    sg_apply_pipeline((sg_pipeline){ .id = (uint32_t)p });
}
static void sk_apply_bindings(const BindingsDesc *b) {
    sg_bindings sb = {0};
    if (b->vbuf) sb.vertex_buffers[0] = (sg_buffer){ .id = (uint32_t)b->vbuf };
    // textures: name -> reflection slot は呼び出し側 (lua_api.c) で解決済みの slot を渡す形に
    // するか、ここで refl を見て解決するか。シンプルさのため呼び出し側で解決し、
    // BindingsDesc.textures[].name と SkShader 内部の slot map を突合せる方式にする。
    // (Task 1 ではまずは旧コードを移植する形で動くようにする。)
    sg_apply_bindings(&sb);
}
static void sk_apply_uniforms(const void *data, size_t bytes) {
    sg_apply_uniforms(0, &(sg_range){ .ptr = data, .size = bytes });
}
static void sk_draw(int base, int count) { sg_draw(base, count, 1); }

// capture: 旧 capture.c の vkCmdCopyImageToBuffer 経路を移植
static bool sk_capture(App *app, const char *path);

const RenderBackend g_backend_sokol = {
    .name = "sokol",
    .init = sk_init,
    .shutdown = sk_shutdown,
    .begin_frame = sk_begin_frame,
    .end_frame = sk_end_frame,
    .make_buffer = sk_make_buffer,
    .make_image = sk_make_image,
    .make_shader = sk_make_shader,
    .make_pipeline = sk_make_pipeline,
    .destroy_buffer = sk_destroy_buffer,
    .destroy_image = sk_destroy_image,
    .destroy_shader = sk_destroy_shader,
    .destroy_pipeline = sk_destroy_pipeline,
    .begin_pass = sk_begin_pass,
    .end_pass = sk_end_pass,
    .apply_pipeline = sk_apply_pipeline,
    .apply_bindings = sk_apply_bindings,
    .apply_uniforms = sk_apply_uniforms,
    .draw = sk_draw,
    .capture = sk_capture,
    .swapchain_color_format = sk_swapchain_color_format,
};

const RenderBackend *g_backend = &g_backend_sokol;
```

> **重要:** texture binding と uniform binding の解決ロジック (reflection の name → slot 検索) は呼び出し側 (lua_api.c の `l_draw`) で `BindingsDesc.refl` に reflection ポインタを入れて渡す。各 backend の `apply_bindings` 内で reflection の `texs[]` を見て name 一致から slot を引き、`SkImage*` / `SgImage*` のテクスチャと sampler を該当 slot にバインドする。これは Step 1 で定義した `BindingsDesc` に既に `refl` フィールドが含まれている前提で書く。

- [ ] **Step 9: `src/app.c` を縮める**

`app_init` / `app_frame_begin` / `app_frame_end` / `app_shutdown` は `g_backend->...` への委譲に変える。Vulkan 状態管理の中身は `backend_sokol.c` に移行済み。

```c
// app.c (置換後の形)
#include "app.h"
#include "backend.h"
#include "lua_api.h"
#include <SDL3/SDL.h>

bool app_init(App *app) {
    memset(app, 0, sizeof(*app));
    app->window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
    if (!app->window) { SDL_Log("CreateWindow: %s", SDL_GetError()); return false; }
    pass_state_init(&app->pass);
    pass_state_set_app(&app->pass, app);
    res_table_init(&app->res);
    pipeline_cache_init(&app->pip_cache);
    capture_state_init(&app->capture);
    // backend->init は Lua の on_init 後に呼ぶ (config() を読むため)
    return true;
}

void app_backend_init(App *app) {
    if (!g_backend->init(app)) {
        SDL_Log("backend(%s) init failed", g_backend->name);
    }
}

void app_frame_begin(App *app, int *w, int *h) {
    g_backend->begin_frame(app, w, h);
    pass_state_set_swapchain_size(&app->pass, *w, *h);
    app->last_w = *w; app->last_h = *h;
}

void app_frame_end(App *app) {
    g_backend->end_frame(app);
    if (capture_state_drain(&app->capture, app)) {
        app->capture_then_exit = true;
    }
    app->frame_index++;
}

void app_shutdown(App *app) {
    pipeline_cache_shutdown(&app->pip_cache);
    res_table_shutdown(&app->res);
    capture_state_shutdown(&app->capture);
    g_backend->shutdown(app);
    if (app->window) SDL_DestroyWindow(app->window);
}
```

`app.h` から `app_swapchain_color_format` を削除。`l_draw` 内の `app_swapchain_color_format(g_app_for_lua)` を `g_backend->swapchain_color_format(g_app_for_lua)` に置換 (この関数ポインタは Step 1 で定義済み)。`backend_sokol.c` 側の実装は現行の `app_swapchain_color_format` のロジックをそのまま移す:

```c
static SglPixelFormat sk_swapchain_color_format(App *app) {
    switch (app->vk_swapchain_format) {
        case VK_FORMAT_B8G8R8A8_UNORM:
        case VK_FORMAT_B8G8R8A8_SRGB:  return SGL_PF_BGRA8;
        case VK_FORMAT_R8G8B8A8_UNORM:
        case VK_FORMAT_R8G8B8A8_SRGB:
        default:                        return SGL_PF_RGBA8;
    }
}
```

(`SGL_PF_BGRA8` が `enums.h` にない場合は追加する)

- [ ] **Step 10: `src/main.c` の初期化順を `app_backend_init` 経由に変更**

```c
SDL_AppResult SDL_AppInit(...) {
    if (!SDL_Init(SDL_INIT_VIDEO)) return SDL_APP_FAILURE;
    if (!app_init(&g_app)) return SDL_APP_FAILURE;
    /* argv parse は現行のまま */
    if (!lua_ctx_init(&g_app.lua, script, &g_app)) return SDL_APP_FAILURE;
    lua_ctx_call_init(&g_app.lua);
    app_backend_init(&g_app);   // ← 追加: on_init 後に backend init
    /* capture path のスケジュールは現行のまま */
    return SDL_APP_CONTINUE;
}
```

- [ ] **Step 11: `CMakeLists.txt` を更新**

`add_executable(sglua ...)` の sources に `src/backend_sokol.c` を追加。

```cmake
add_executable(sglua
  src/main.c
  src/app.c
  src/sokol_impl.c
  src/lua_api.c
  src/enums_lua.c
  src/pass.c
  src/resources.c
  src/shader.cpp
  src/pipeline.c
  src/capture.c
  src/backend_sokol.c
)
```

- [ ] **Step 12: build & 4 サンプル + capture を回して リグレッションがないことを確認**

```bash
cmake --build build -j 2>&1 | tee /tmp/build.log
echo "---"
for s in samples/0[1-4]_*.lua; do
  scripts/run-headless.sh ./build/sglua "$s" --capture "/tmp/$(basename $s .lua).png" --capture-frame 5
  echo "=== $s -> exit $?"
done
ls -la /tmp/0[1-4]_*.png
```

期待: build エラーなし。各サンプルが exit 0 (capture 完了で正常終了) または exit 124 (timeout)、PNG が 4 枚作られる。エラーログなし。validation 警告は現状の depth format 問題のみ許容。

- [ ] **Step 13: コミット**

```bash
git add -A
git commit -m "$(cat <<'EOF'
refactor: introduce RenderBackend vtable and route sokol path through it

backend.h で RenderBackend interface を定義し、現行の sokol_gfx + 直叩き
Vulkan 実装を backend_sokol.c に集約。pass/pipeline/resources/capture/
lua_api は g_backend 経由に統一し、shader.cpp は SPIR-V blob を返す形に変更。
今は g_backend = &g_backend_sokol 固定で、サンプル 01〜04 + capture が
sokol path で従来通り動くことを確認。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Lua `config()` API + on_init 後 backend 初期化フロー

**Goal:** Lua が `config({backend = "sokol"})` を `on_init` の中で呼べるようにし、main 側はそれを読んで `g_backend` を確定。`config()` を呼ばなければ default = sokol で従来通り動く。

**Files:**
- Modify: `src/app.h`, `src/app.c`
- Modify: `src/lua_api.c`
- Modify: `src/main.c`

- [ ] **Step 1: `App` に backend 確定状態を持たせる**

```c
// app.h: App struct に追加
typedef enum { APP_PHASE_PRE_BACKEND, APP_PHASE_POST_BACKEND } AppPhase;

typedef struct App {
    /* ... 既存 ... */
    AppPhase phase;             // APP_PHASE_PRE_BACKEND の間だけ config() 可能
    char     backend_name[16];  // "sokol" / "sdlgpu"
} App;
```

`app_init()` の冒頭で `app->phase = APP_PHASE_PRE_BACKEND; strcpy(app->backend_name, "sokol");` を入れる。

- [ ] **Step 2: `lua_api.c` に `l_config` 追加**

```c
static int l_config(lua_State *L) {
    if (g_app_for_lua->phase != APP_PHASE_PRE_BACKEND) {
        return luaL_error(L, "config: must be called inside on_init");
    }
    luaL_checktype(L, 1, LUA_TTABLE);
    lua_getfield(L, 1, "backend");
    const char *name = lua_isstring(L, -1) ? lua_tostring(L, -1) : "sokol";
    if (strcmp(name, "sokol") != 0 && strcmp(name, "sdlgpu") != 0) {
        return luaL_error(L, "config: backend must be 'sokol' or 'sdlgpu', got '%s'", name);
    }
    strncpy(g_app_for_lua->backend_name, name, sizeof(g_app_for_lua->backend_name) - 1);
    g_app_for_lua->backend_name[sizeof(g_app_for_lua->backend_name) - 1] = '\0';
    lua_pop(L, 1);
    return 0;
}
```

`lua_api_register` の末尾に:
```c
lua_pushcfunction(L, l_config);
lua_setglobal(L, "config");
```

- [ ] **Step 3: `app_backend_init` で名前を見て backend を確定**

```c
// app.c
void app_backend_init(App *app) {
    if (strcmp(app->backend_name, "sdlgpu") == 0) {
        g_backend = &g_backend_sdlgpu;
    } else {
        g_backend = &g_backend_sokol;
    }
    SDL_Log("backend selected: %s", g_backend->name);
    if (!g_backend->init(app)) {
        SDL_Log("backend init failed");
    }
    app->phase = APP_PHASE_POST_BACKEND;
}
```

(Task 2 時点では `g_backend_sdlgpu` はまだ完全実装されていないので、その分岐に入ったら panic でよい — Task 3 で skeleton が入る。一旦 Task 2 では sokol しか選択されない前提のテストを通す)

- [ ] **Step 4: 既存サンプル `samples/00_hello.lua` で config を呼ぶテスト**

`samples/00_hello.lua` の `on_init` を以下に変更:

```lua
function on_init()
    config({ backend = "sokol" })
    print("config called")
end
```

期待: 起動して `config called` がログ出る。clear 動作に影響なし。

- [ ] **Step 5: build & 動作確認**

```bash
cmake --build build -j
scripts/run-headless.sh ./build/sglua samples/00_hello.lua &
sleep 2; kill $!
# clear 動作確認
scripts/run-headless.sh ./build/sglua samples/00b_clear.lua &
sleep 2; kill $!
```

期待: 起動・終了が正常、`config called` がログに出る、validation 警告なし。

- [ ] **Step 6: コミット**

```bash
git add -A
git commit -m "feat: lua config() API for backend selection during on_init"
```

---

## Task 3: `backend_sdlgpu.c` skeleton (init / shutdown / clear pass)

**Goal:** SDL_GPU 経路で `SDL_CreateGPUDevice` → `SDL_ClaimWindowForGPUDevice` → `SDL_BeginGPURenderPass` で clear だけする最小実装を入れる。`config({backend="sdlgpu"})` のサンプルで黒+クリアカラーの画面が出る。

**Files:**
- Create: `src/backend_sdlgpu.c`
- Modify: `src/app.h`, `src/app.c` (`SDL_GPUDevice *` を保持)
- Modify: `CMakeLists.txt`
- Modify: `samples/00b_clear.lua`

- [ ] **Step 1: `App` に SDL_GPU 状態を追加**

```c
// app.h
#include <SDL3/SDL_gpu.h>

typedef struct App {
    /* ... 既存 ... */
    SDL_GPUDevice *gpu_device;
    SDL_GPUTexture *gpu_swapchain_tex;  // current frame の swapchain
    SDL_GPUCommandBuffer *gpu_cmd;      // current frame
} App;
```

- [ ] **Step 2: `src/backend_sdlgpu.c` skeleton**

```c
#include "backend.h"
#include "app.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_gpu.h>
#include <stdlib.h>
#include <string.h>

static bool sg_init(App *app) {
    g_app_for_sdlgpu = app;
    app->gpu_device = SDL_CreateGPUDevice(SDL_GPU_SHADERFORMAT_SPIRV, true, NULL);
    if (!app->gpu_device) {
        SDL_Log("SDL_CreateGPUDevice failed: %s", SDL_GetError());
        return false;
    }
    if (!SDL_ClaimWindowForGPUDevice(app->gpu_device, app->window)) {
        SDL_Log("SDL_ClaimWindowForGPUDevice failed: %s", SDL_GetError());
        return false;
    }
    return true;
}

static void sg_shutdown(App *app) {
    if (app->gpu_device) {
        SDL_ReleaseWindowFromGPUDevice(app->gpu_device, app->window);
        SDL_DestroyGPUDevice(app->gpu_device);
        app->gpu_device = NULL;
    }
}

static void sg_begin_frame(App *app, int *w, int *h) {
    g_app_for_sdlgpu = app;
    app->gpu_cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
    SDL_AcquireGPUSwapchainTexture(app->gpu_cmd, app->window, &app->gpu_swapchain_tex,
        (Uint32*)w, (Uint32*)h);
}

static void sg_end_frame(App *app) {
    SDL_SubmitGPUCommandBuffer(app->gpu_cmd);
    app->gpu_cmd = NULL;
    app->gpu_swapchain_tex = NULL;
}

static SDL_GPURenderPass *g_render_pass;

static App *g_app_for_sdlgpu = NULL;  // sg_init / sg_begin_frame で代入

static void sg_begin_pass(const PassBeginDesc *d) {
    // PoC: target == 0 (main_tex sentinel) は swapchain texture にバインド。
    // 将来 RT 化したら d->target が SgImage* を指す形になる。
    SDL_GPUColorTargetInfo target = {
        .texture = g_app_for_sdlgpu->gpu_swapchain_tex,
        .clear_color = { d->clear[0], d->clear[1], d->clear[2], d->clear[3] },
        .load_op = SDL_GPU_LOADOP_CLEAR,
        .store_op = SDL_GPU_STOREOP_STORE,
    };
    g_render_pass = SDL_BeginGPURenderPass(g_app_for_sdlgpu->gpu_cmd, &target, 1, NULL);
}

static void sg_end_pass(void) {
    SDL_EndGPURenderPass(g_render_pass);
    g_render_pass = NULL;
}

// その他は Task 4-7 で実装。一旦 stub:
static BackendBuffer   sg_make_buffer(SglBufferType t, const float *d, size_t b) { (void)t;(void)d;(void)b; return 0; }
static BackendImage    sg_make_image(const ImageDesc *d) { (void)d; return 0; }
static BackendShader   sg_make_shader(const ShaderDesc *d) { (void)d; return 0; }
static BackendPipeline sg_make_pipeline(const PipelineDesc *d) { (void)d; return 0; }
static void sg_destroy_buffer(BackendBuffer h) { (void)h; }
static void sg_destroy_image(BackendImage h) { (void)h; }
static void sg_destroy_shader(BackendShader h) { (void)h; }
static void sg_destroy_pipeline(BackendPipeline h) { (void)h; }
static void sg_apply_pipeline(BackendPipeline h) { (void)h; }
static void sg_apply_bindings(const BindingsDesc *b) { (void)b; }
static void sg_apply_uniforms(const void *d, size_t b) { (void)d;(void)b; }
static void sg_draw(int base, int count) { (void)base;(void)count; }
static bool sg_capture(App *app, const char *path) { (void)app;(void)path; return false; }
static SglPixelFormat sg_swapchain_color_format(App *app) { (void)app; return SGL_PF_RGBA8; }

const RenderBackend g_backend_sdlgpu = {
    .name = "sdlgpu",
    .init = sg_init, .shutdown = sg_shutdown,
    .begin_frame = sg_begin_frame, .end_frame = sg_end_frame,
    .make_buffer = sg_make_buffer, .make_image = sg_make_image,
    .make_shader = sg_make_shader, .make_pipeline = sg_make_pipeline,
    .destroy_buffer = sg_destroy_buffer, .destroy_image = sg_destroy_image,
    .destroy_shader = sg_destroy_shader, .destroy_pipeline = sg_destroy_pipeline,
    .begin_pass = sg_begin_pass, .end_pass = sg_end_pass,
    .apply_pipeline = sg_apply_pipeline, .apply_bindings = sg_apply_bindings,
    .apply_uniforms = sg_apply_uniforms, .draw = sg_draw,
    .capture = sg_capture,
    .swapchain_color_format = sg_swapchain_color_format,
};
```

> **注:** `g_app_ptr` のような static グローバル経由でアクセスするのは抽象が壊れるので、`PassBeginDesc` に `App *` を含める形か、`begin_frame` が `App *` を保持する内部 static にする (この backend は 1 プロセス 1 instance しかないので static でも実害ない)。実装は `static App *g_app_for_sdlgpu = NULL;` を置き `sg_begin_frame(app,…)` で代入、他関数で参照する形でよい。

- [ ] **Step 3: `CMakeLists.txt` に `backend_sdlgpu.c` を追加**

```cmake
add_executable(sglua
  ...
  src/backend_sokol.c
  src/backend_sdlgpu.c
)
```

- [ ] **Step 4: `samples/00b_clear.lua` で sdlgpu を試す**

```lua
function on_init()
    local b = arg and arg[1] or os.getenv("SGLUA_BACKEND") or "sokol"
    config({ backend = b })
    print("backend = " .. b)
end

function on_frame()
    begin_pass({ target = main_tex, clear_color = {0.2, 0.6, 0.2, 1} })
    end_pass()
end
```

- [ ] **Step 5: `scripts/run-headless.sh` で SGLUA_BACKEND を伝播**

`run-headless.sh` の `exec` 直前に:
```bash
export SGLUA_BACKEND="${SGLUA_BACKEND:-sokol}"
```

- [ ] **Step 6: build & 両 backend で動作確認**

```bash
cmake --build build -j
SGLUA_BACKEND=sokol  scripts/run-headless.sh ./build/sglua samples/00b_clear.lua --capture /tmp/clear_sk.png --capture-frame 5
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/00b_clear.lua --capture /tmp/clear_sg.png --capture-frame 5
```

期待: 両方とも exit 0 (sdlgpu 側は capture 未実装なので timeout で落ちる場合は exit 124 でも可) — 緑色画面が出る。

> **既知の制約:** Task 3 では sdlgpu の capture は未実装。timeout で落とすか、capture 引数を渡さずに目視 (lavapipe + xvfb 越しでは目視できないので、別途 build/sglua をネイティブで起動して確認する選択肢もある)。

- [ ] **Step 7: コミット**

```bash
git add -A
git commit -m "feat(sdlgpu): add backend_sdlgpu skeleton with clear-pass support"
```

---

## Task 4: sdlgpu draw — sample 01 (単色三角形) を sdlgpu で動かす

**Goal:** sdlgpu の make_buffer / make_shader / make_pipeline / apply_pipeline / apply_bindings / draw を実装し、`samples/01_triangle.lua` を sdlgpu で動かす。

**Files:**
- Modify: `src/backend_sdlgpu.c`
- Modify: `samples/01_triangle.lua`

- [ ] **Step 1: `sg_make_buffer` 実装 (transfer buffer 経由 upload)**

```c
typedef struct SgBuffer { SDL_GPUBuffer *gpu; size_t bytes; SglBufferType type; } SgBuffer;

static BackendBuffer sg_make_buffer(SglBufferType type, const float *data, size_t bytes) {
    SgBuffer *b = (SgBuffer*)calloc(1, sizeof(SgBuffer));
    b->gpu = SDL_CreateGPUBuffer(g_app_for_sdlgpu->gpu_device,
        &(SDL_GPUBufferCreateInfo){
            .usage = (type == SGL_BUFFER_VERTEX ? SDL_GPU_BUFFERUSAGE_VERTEX : SDL_GPU_BUFFERUSAGE_INDEX),
            .size = (Uint32)bytes,
        });
    SDL_GPUTransferBuffer *tb = SDL_CreateGPUTransferBuffer(g_app_for_sdlgpu->gpu_device,
        &(SDL_GPUTransferBufferCreateInfo){
            .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, .size = (Uint32)bytes,
        });
    void *dst = SDL_MapGPUTransferBuffer(g_app_for_sdlgpu->gpu_device, tb, false);
    memcpy(dst, data, bytes);
    SDL_UnmapGPUTransferBuffer(g_app_for_sdlgpu->gpu_device, tb);
    SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(g_app_for_sdlgpu->gpu_device);
    SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
    SDL_UploadToGPUBuffer(cp,
        &(SDL_GPUTransferBufferLocation){ .transfer_buffer = tb, .offset = 0 },
        &(SDL_GPUBufferRegion){ .buffer = b->gpu, .offset = 0, .size = (Uint32)bytes },
        false);
    SDL_EndGPUCopyPass(cp);
    SDL_SubmitGPUCommandBuffer(cmd);
    SDL_ReleaseGPUTransferBuffer(g_app_for_sdlgpu->gpu_device, tb);
    b->bytes = bytes; b->type = type;
    return (uintptr_t)b;
}
static void sg_destroy_buffer(BackendBuffer h) {
    SgBuffer *b = (SgBuffer*)h; if (!b) return;
    SDL_ReleaseGPUBuffer(g_app_for_sdlgpu->gpu_device, b->gpu);
    free(b);
}
```

- [ ] **Step 2: `sg_make_shader` 実装**

```c
typedef struct SgShader {
    SDL_GPUShader *vs;
    SDL_GPUShader *fs;
    ShaderReflection refl;
} SgShader;

static BackendShader sg_make_shader(const ShaderDesc *d) {
    SgShader *s = (SgShader*)calloc(1, sizeof(SgShader));
    s->refl = *d->refl;
    s->vs = SDL_CreateGPUShader(g_app_for_sdlgpu->gpu_device,
        &(SDL_GPUShaderCreateInfo){
            .code = (Uint8*)d->vs_spirv, .code_size = d->vs_bytes,
            .entrypoint = "vs_main",
            .format = SDL_GPU_SHADERFORMAT_SPIRV,
            .stage = SDL_GPU_SHADERSTAGE_VERTEX,
            .num_uniform_buffers = (Uint32)d->refl->ub_count,
            .num_storage_buffers = 0, .num_storage_textures = 0, .num_samplers = 0,
        });
    s->fs = SDL_CreateGPUShader(g_app_for_sdlgpu->gpu_device,
        &(SDL_GPUShaderCreateInfo){
            .code = (Uint8*)d->fs_spirv, .code_size = d->fs_bytes,
            .entrypoint = "fs_main",
            .format = SDL_GPU_SHADERFORMAT_SPIRV,
            .stage = SDL_GPU_SHADERSTAGE_FRAGMENT,
            .num_uniform_buffers = 0,
            .num_storage_buffers = 0, .num_storage_textures = 0,
            .num_samplers = (Uint32)d->refl->tex_count,
        });
    return (uintptr_t)s;
}
static void sg_destroy_shader(BackendShader h) {
    SgShader *s = (SgShader*)h; if (!s) return;
    if (s->vs) SDL_ReleaseGPUShader(g_app_for_sdlgpu->gpu_device, s->vs);
    if (s->fs) SDL_ReleaseGPUShader(g_app_for_sdlgpu->gpu_device, s->fs);
    free(s);
}
```

- [ ] **Step 3: `sg_make_pipeline` 実装 (vertex layout は `refl->attrs[]` から構築)**

```c
typedef struct SgPipeline { SDL_GPUGraphicsPipeline *gpu; ShaderReflection refl; } SgPipeline;

static SDL_GPUTextureFormat sg_swapchain_fmt(App *app) {
    return SDL_GetGPUSwapchainTextureFormat(app->gpu_device, app->window);
}

static BackendPipeline sg_make_pipeline(const PipelineDesc *d) {
    SgShader *sh = (SgShader*)d->shader;
    SgPipeline *p = (SgPipeline*)calloc(1, sizeof(SgPipeline));
    p->refl = *d->refl;

    SDL_GPUVertexAttribute attrs[SGL_MAX_ATTRS];
    for (int i = 0; i < d->refl->attr_count; ++i) {
        SDL_GPUVertexElementFormat fmt;
        switch (d->refl->attrs[i].comp_count) {
            case 1: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT;  break;
            case 2: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2; break;
            case 3: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3; break;
            default: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4;
        }
        attrs[i] = (SDL_GPUVertexAttribute){
            .location = (Uint32)d->refl->attrs[i].slot,
            .buffer_slot = 0,
            .format = fmt,
            .offset = (Uint32)(d->refl->attrs[i].offset_floats * sizeof(float)),
        };
    }
    SDL_GPUVertexBufferDescription vbd = {
        .slot = 0,
        .pitch = (Uint32)(d->refl->vertex_stride_floats * sizeof(float)),
        .input_rate = SDL_GPU_VERTEXINPUTRATE_VERTEX,
    };
    SDL_GPUColorTargetDescription ctd = {
        .format = sg_swapchain_fmt(g_app_for_sdlgpu),
        .blend_state = { /* PoC: blend off */ },
    };
    p->gpu = SDL_CreateGPUGraphicsPipeline(g_app_for_sdlgpu->gpu_device,
        &(SDL_GPUGraphicsPipelineCreateInfo){
            .vertex_shader = sh->vs,
            .fragment_shader = sh->fs,
            .vertex_input_state = {
                .vertex_buffer_descriptions = &vbd,
                .num_vertex_buffers = 1,
                .vertex_attributes = attrs,
                .num_vertex_attributes = (Uint32)d->refl->attr_count,
            },
            .primitive_type = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
            .rasterizer_state = {
                .cull_mode = (d->cull == SGL_CULL_BACK)  ? SDL_GPU_CULLMODE_BACK
                          : (d->cull == SGL_CULL_FRONT) ? SDL_GPU_CULLMODE_FRONT
                          :                                SDL_GPU_CULLMODE_NONE,
            },
            .target_info = { .color_target_descriptions = &ctd, .num_color_targets = 1 },
        });
    return (uintptr_t)p;
}
static void sg_destroy_pipeline(BackendPipeline h) {
    SgPipeline *p = (SgPipeline*)h; if (!p) return;
    SDL_ReleaseGPUGraphicsPipeline(g_app_for_sdlgpu->gpu_device, p->gpu);
    free(p);
}
```

- [ ] **Step 4: `sg_apply_pipeline` / `sg_apply_bindings` / `sg_draw` 実装**

```c
static SgPipeline *g_current_pip;

static void sg_apply_pipeline(BackendPipeline h) {
    g_current_pip = (SgPipeline*)h;
    SDL_BindGPUGraphicsPipeline(g_render_pass, g_current_pip->gpu);
}

static void sg_apply_bindings(const BindingsDesc *b) {
    if (b->vbuf) {
        SgBuffer *vb = (SgBuffer*)b->vbuf;
        SDL_BindGPUVertexBuffers(g_render_pass, 0,
            &(SDL_GPUBufferBinding){ .buffer = vb->gpu, .offset = 0 }, 1);
    }
    // textures は Task 6 で
}

static void sg_draw(int base, int count) {
    SDL_DrawGPUPrimitives(g_render_pass, (Uint32)count, 1, (Uint32)base, 0);
}
```

- [ ] **Step 5: `samples/01_triangle.lua` に config 行を追加**

```lua
-- samples/01_triangle.lua の冒頭
function on_init()
    config({ backend = arg and arg[1] or os.getenv("SGLUA_BACKEND") or "sokol" })
end
```

(他の構造は維持)

- [ ] **Step 6: build + 両 backend で sample 01 動作確認**

```bash
cmake --build build -j
SGLUA_BACKEND=sokol  scripts/run-headless.sh ./build/sglua samples/01_triangle.lua --capture /tmp/01_sk.png --capture-frame 5
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/01_triangle.lua --capture /tmp/01_sg.png --capture-frame 5  # sdlgpu の capture は Task 8 で
file /tmp/01_sk.png
```

期待: sokol 側はオレンジ三角の PNG、sdlgpu 側は capture 未対応のためタイムアウト終了。`--capture-frame 5` を外して目視 (xvfb-run + 写真撮影は不可能なので、ローカル GUI 環境で確認するか、Task 8 完了まで待つ)。

代替: validation を sdl3 内蔵の WARN ログで判断する。`SDL_DrawGPUPrimitives` がエラーなく呼べていれば最低限 OK。

- [ ] **Step 7: コミット**

```bash
git add -A
git commit -m "feat(sdlgpu): make_buffer/shader/pipeline + draw — sample 01 works"
```

---

## Task 5: sdlgpu — sample 02 (頂点カラー、複数 attribute)

**Goal:** vertex layout が複数 attr を扱えることを確認する。多くは Task 4 のコードがそのまま動くはずだが、`refl->attrs[i].slot` と `refl->attrs[i].offset_floats` が正しく設定されていることを念入りに verify する。

**Files:**
- Modify: `samples/02_vertex_color.lua` (config 行追加)

- [ ] **Step 1: `samples/02_vertex_color.lua` の `on_init` に config 行を追加 (sample 01 と同じパターン)**

- [ ] **Step 2: build & 動作確認**

```bash
SGLUA_BACKEND=sokol  scripts/run-headless.sh ./build/sglua samples/02_vertex_color.lua --capture /tmp/02_sk.png --capture-frame 5
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/02_vertex_color.lua &
sleep 2; kill $!  # capture 未対応のため
```

期待: sokol 側は RGB 三角形の PNG、sdlgpu 側は起動エラーなし、validation エラーなし。

- [ ] **Step 3: コミット**

```bash
git add -A
git commit -m "feat(sdlgpu): sample 02 vertex color works (multi-attr layout)"
```

---

## Task 6: sdlgpu — texture binding (sample 03)

**Goal:** sdlgpu で texture/sampler を扱えるようにし、sample 03 を動かす。

**Files:**
- Modify: `src/backend_sdlgpu.c`
- Modify: `samples/03_texture.lua`

- [ ] **Step 1: `sg_make_image` 実装**

```c
typedef struct SgImage {
    SDL_GPUTexture *tex;
    SDL_GPUSampler *smp;
    int w, h;
    SglPixelFormat fmt;
} SgImage;

static BackendImage sg_make_image(const ImageDesc *d) {
    SgImage *im = (SgImage*)calloc(1, sizeof(SgImage));
    im->w = d->w; im->h = d->h; im->fmt = d->fmt;
    SDL_GPUTextureFormat tfmt = (d->fmt == SGL_PF_R8) ? SDL_GPU_TEXTUREFORMAT_R8_UNORM
                                                       : SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
    im->tex = SDL_CreateGPUTexture(g_app_for_sdlgpu->gpu_device,
        &(SDL_GPUTextureCreateInfo){
            .type = SDL_GPU_TEXTURETYPE_2D,
            .format = tfmt,
            .usage = SDL_GPU_TEXTUREUSAGE_SAMPLER,
            .width = (Uint32)d->w, .height = (Uint32)d->h,
            .layer_count_or_depth = 1, .num_levels = 1,
        });
    if (d->data) {
        SDL_GPUTransferBuffer *tb = SDL_CreateGPUTransferBuffer(g_app_for_sdlgpu->gpu_device,
            &(SDL_GPUTransferBufferCreateInfo){
                .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
                .size = (Uint32)d->data_bytes,
            });
        void *dst = SDL_MapGPUTransferBuffer(g_app_for_sdlgpu->gpu_device, tb, false);
        memcpy(dst, d->data, d->data_bytes);
        SDL_UnmapGPUTransferBuffer(g_app_for_sdlgpu->gpu_device, tb);
        SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(g_app_for_sdlgpu->gpu_device);
        SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
        SDL_UploadToGPUTexture(cp,
            &(SDL_GPUTextureTransferInfo){ .transfer_buffer = tb },
            &(SDL_GPUTextureRegion){ .texture = im->tex, .w = (Uint32)d->w, .h = (Uint32)d->h, .d = 1 },
            false);
        SDL_EndGPUCopyPass(cp);
        SDL_SubmitGPUCommandBuffer(cmd);
        SDL_ReleaseGPUTransferBuffer(g_app_for_sdlgpu->gpu_device, tb);
    }
    im->smp = SDL_CreateGPUSampler(g_app_for_sdlgpu->gpu_device,
        &(SDL_GPUSamplerCreateInfo){
            .min_filter = SDL_GPU_FILTER_LINEAR, .mag_filter = SDL_GPU_FILTER_LINEAR,
            .address_mode_u = SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
            .address_mode_v = SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
            .address_mode_w = SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        });
    return (uintptr_t)im;
}
static void sg_destroy_image(BackendImage h) {
    SgImage *im = (SgImage*)h; if (!im) return;
    if (im->tex) SDL_ReleaseGPUTexture(g_app_for_sdlgpu->gpu_device, im->tex);
    if (im->smp) SDL_ReleaseGPUSampler(g_app_for_sdlgpu->gpu_device, im->smp);
    free(im);
}
```

- [ ] **Step 2: `sg_apply_bindings` を texture 対応に拡張**

```c
static void sg_apply_bindings(const BindingsDesc *b) {
    if (b->vbuf) {
        SgBuffer *vb = (SgBuffer*)b->vbuf;
        SDL_BindGPUVertexBuffers(g_render_pass, 0,
            &(SDL_GPUBufferBinding){ .buffer = vb->gpu, .offset = 0 }, 1);
    }
    if (b->texture_count > 0 && b->refl) {
        SDL_GPUTextureSamplerBinding tsb[8];
        int n = 0;
        for (int i = 0; i < b->texture_count; ++i) {
            // refl->texs[] から name 一致を探し、smp_slot を求める
            for (int j = 0; j < b->refl->tex_count; ++j) {
                if (strcmp(b->refl->texs[j].name, b->textures[i].name) == 0) {
                    SgImage *im = (SgImage*)b->textures[i].image;
                    int slot = b->refl->texs[j].smp_slot;
                    if (slot < 0) break;
                    if (slot >= 8) break;
                    tsb[slot] = (SDL_GPUTextureSamplerBinding){ .texture = im->tex, .sampler = im->smp };
                    if (slot + 1 > n) n = slot + 1;
                    break;
                }
            }
        }
        if (n > 0) SDL_BindGPUFragmentSamplers(g_render_pass, 0, tsb, (Uint32)n);
    }
}
```

- [ ] **Step 3: `samples/03_texture.lua` の `on_init` に config 行を追加**

- [ ] **Step 4: build & 動作確認**

```bash
cmake --build build -j
SGLUA_BACKEND=sokol  scripts/run-headless.sh ./build/sglua samples/03_texture.lua --capture /tmp/03_sk.png --capture-frame 5
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/03_texture.lua &
sleep 2; kill $!
```

期待: sokol 側はチェッカー貼り三角の PNG、sdlgpu 側はエラーなしで動く。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(sdlgpu): texture+sampler binding — sample 03 works"
```

---

## Task 7: sdlgpu — uniform block (sample 04)

**Goal:** sdlgpu で uniform block (mvp 行列) を渡せるようにし、sample 04 を動かす。

**Files:**
- Modify: `src/backend_sdlgpu.c`
- Modify: `samples/04_mvp.lua`

- [ ] **Step 1: `sg_apply_uniforms` を実装**

SDL_GPU では Push 系 API で uniform を渡す。VS 側 (mvp は vertex stage の前提):

```c
static void sg_apply_uniforms(const void *data, size_t bytes) {
    // PoC: vs の uniform block 0 にプッシュ
    SDL_PushGPUVertexUniformData(g_app_for_sdlgpu->gpu_cmd, 0, data, (Uint32)bytes);
}
```

> **メモ:** 現行 sokol 側も `sg_apply_uniforms(0, ...)` で ub[0] のみ。sample 04 は VS 側のみ uniform を使うので Push に固定で問題ない。FS 側 uniform 対応は将来。

- [ ] **Step 2: `samples/04_mvp.lua` の `on_init` に config 行を追加**

- [ ] **Step 3: build & 動作確認**

```bash
cmake --build build -j
SGLUA_BACKEND=sokol  scripts/run-headless.sh ./build/sglua samples/04_mvp.lua --capture /tmp/04_sk.png --capture-frame 30
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/04_mvp.lua &
sleep 3; kill $!
```

期待: sokol 側は回転している三角の PNG、sdlgpu 側はエラーなし。

- [ ] **Step 4: コミット**

```bash
git add -A
git commit -m "feat(sdlgpu): uniform block via push — sample 04 works"
```

---

## Task 8: sdlgpu capture (PNG 出力)

**Goal:** sdlgpu でも `--capture` / Lua `capture()` が PNG を吐けるようにする。これで両 backend 4 サンプル全部が capture 比較できるようになる。

**Files:**
- Modify: `src/backend_sdlgpu.c`

- [ ] **Step 1: `sg_capture` 実装**

```c
#include "stb_image_write.h"

static bool sg_capture(App *app, const char *path) {
    if (!app->gpu_swapchain_tex) return false;
    int w = app->last_w, h = app->last_h;
    Uint32 stride = (Uint32)w * 4;
    Uint32 bytes  = stride * (Uint32)h;

    SDL_GPUTransferBuffer *tb = SDL_CreateGPUTransferBuffer(app->gpu_device,
        &(SDL_GPUTransferBufferCreateInfo){
            .usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD, .size = bytes,
        });

    SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
    SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
    SDL_DownloadFromGPUTexture(cp,
        &(SDL_GPUTextureRegion){ .texture = app->gpu_swapchain_tex, .w = (Uint32)w, .h = (Uint32)h, .d = 1 },
        &(SDL_GPUTextureTransferInfo){ .transfer_buffer = tb });
    SDL_EndGPUCopyPass(cp);
    SDL_GPUFence *fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
    SDL_WaitForGPUFences(app->gpu_device, true, &fence, 1);
    SDL_ReleaseGPUFence(app->gpu_device, fence);

    void *src = SDL_MapGPUTransferBuffer(app->gpu_device, tb, false);
    uint8_t *rgba = (uint8_t*)malloc(bytes);
    memcpy(rgba, src, bytes);
    SDL_UnmapGPUTransferBuffer(app->gpu_device, tb);
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);

    // SDL_GPU の swapchain format が BGRA8 の場合は swizzle
    if (SDL_GetGPUSwapchainTextureFormat(app->gpu_device, app->window) == SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM) {
        for (Uint32 i = 0; i < bytes; i += 4) { uint8_t t = rgba[i]; rgba[i] = rgba[i+2]; rgba[i+2] = t; }
    }

    int ok = stbi_write_png(path, w, h, 4, rgba, (int)stride);
    free(rgba);
    return ok != 0;
}
```

- [ ] **Step 2: build & 両 backend 全サンプルで capture を回す**

```bash
cmake --build build -j
for s in samples/0[1-4]_*.lua; do
  base=$(basename "$s" .lua)
  for bk in sokol sdlgpu; do
    SGLUA_BACKEND=$bk scripts/run-headless.sh ./build/sglua "$s" --capture "/tmp/${base}_${bk}.png" --capture-frame 5
    echo "  $bk $s -> exit $? -> /tmp/${base}_${bk}.png"
  done
done
ls -la /tmp/0[1-4]_*_*.png
file /tmp/0[1-4]_*_*.png | head
```

期待: 4 サンプル × 2 backend の計 8 PNG が生成され、目視で同等の絵が出る (色配置が backend で BGRA/RGBA 違いの場合は swizzle で吸収済み)。

- [ ] **Step 3: コミット**

```bash
git add -A
git commit -m "feat(sdlgpu): capture via SDL_DownloadFromGPUTexture and PNG write"
```

---

## Task 9: README + ドキュメント整備

**Goal:** README に backend 切替方法と既知制約を反映。`tasks.md` に「Phase 3 Done」相当の脚注を入れる。

**Files:**
- Modify: `README.md`
- Modify: `tasks.md` (任意)

- [ ] **Step 1: README に backend セクション追加**

`## 実行` の直後に追加:

```markdown
## Backend 切替

sglua は内部に 2 つの GPU backend を持つ:

- `sokol` (default) — sokol_gfx (Vulkan)
- `sdlgpu` — SDL3 GPU API (Vulkan / Metal / D3D12 を SDL3 が抽象)

切替は Lua の `on_init` 内で `config({ backend = "sdlgpu" })` を呼ぶ。
サンプルでは `arg[1]` または環境変数 `SGLUA_BACKEND` を見るパターンを採用:

```lua
function on_init()
    config({ backend = arg and arg[1] or os.getenv("SGLUA_BACKEND") or "sokol" })
end
```

```sh
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/01_triangle.lua
```
```

`## 未実装 (将来)` から `SDL3 GPU backend (Phase 3、cross-platform)` を削除する。

- [ ] **Step 2: 全サンプル smoke test の最終確認**

```bash
for s in samples/0[1-4]_*.lua; do
  for bk in sokol sdlgpu; do
    SGLUA_BACKEND=$bk timeout 5 scripts/run-headless.sh ./build/sglua "$s" \
      --capture "/tmp/final_$(basename $s .lua)_${bk}.png" --capture-frame 5
  done
done
echo "---"
ls -la /tmp/final_*.png | wc -l   # 8 を期待
```

- [ ] **Step 3: コミット**

```bash
git add README.md
git commit -m "docs: document sdl3 gpu backend and config() switch"
```

---

## 完了条件 (全タスク終了時)

- `cmake --build build -j` が clean に通る
- `SGLUA_BACKEND=sokol scripts/run-headless.sh ./build/sglua samples/0X_*.lua --capture` が 4 サンプル全部成功
- `SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/0X_*.lua --capture` が 4 サンプル全部成功
- 両 backend で capture PNG の絵が等価 (目視チェック)
- README に backend 切替が反映
- `g_backend = &g_backend_sokol` が default のとき、既存サンプルが backend 切替コード追加前と同じ挙動 (リグレッションなし)
