# Sample Live Edit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 全サンプルを「外部ファイル入力 + version=content-hash」に統一し、編集すると次フレームから反映される live-edit 体験を実装する。

**Architecture:** `RenderBackend` vtable に `update_buffer` / `update_image` を追加し、`use_*` の version 違い時に in-place update を選択する経路を入れる。`use_shader` は recompile + handle 差し替え + pipeline cache sweep。Lua 側に `samples/sg_io.lua` の helper モジュールを置き、C 側 primitives (`file_mtime` / `fnv1a64` / `load_png`) と組み合わせる。

**Tech Stack:**
- C11 (sokol_gfx, SDL3 GPU)
- C++17 (Slang via shader.cpp)
- Lua 5.5
- stb_image.h (PNG decode、新規 vendor)

---

## File Structure

```
src/
├── lua_api.c             modify  use_buffer/texture/shader 拡張、helper Lua 関数追加
├── backend.h             modify  update_buffer / update_image を vtable 追加
├── backend_sokol.c       modify  make_buffer/image を dynamic_update 化、update_* 実装
├── backend_sdlgpu.c      modify  update_* 実装 (cycle=true)
├── pipeline.h            modify  pipeline_cache_invalidate_shader 宣言
├── pipeline.c            modify  pipeline_cache_invalidate_shader 実装
└── stb_impl.c            modify  STB_IMAGE_IMPLEMENTATION 追加

third_party/stb/
└── stb_image.h           create  vendor (single-header)

samples/
├── sg_io.lua             create  load_text/load_floats/load_png + path→{mtime,hash,parsed} cache
├── 01_triangle.lua       modify  helper 経由に refactor
├── 02_vertex_color.lua   modify  同
├── 03_texture.lua        modify  同
├── 04_mvp.lua            modify  同
└── data/                 create  サンプル入力ファイル群
    ├── 01_triangle.vs.slang
    ├── 01_triangle.fs.slang
    ├── 01_triangle.verts.lua
    ├── 02_vcol.vs.slang
    ├── 02_vcol.fs.slang
    ├── 02_vcol.verts.lua
    ├── 03_tex.vs.slang
    ├── 03_tex.fs.slang
    ├── 03_tex.verts.lua
    ├── 03_tex.png
    ├── 04_mvp.vs.slang
    ├── 04_mvp.fs.slang
    └── 04_mvp.verts.lua

README.md                 modify  Live edit セクション追加、未実装欄から hot reload 削除
```

**確認済み既存事項:**
- sokol_gfx は `usage = { .vertex_buffer/.index_buffer = true, .immutable / .dynamic_update = true }` の struct flag を持つ。`sg_update_buffer(buf, &sg_range)` / `sg_update_image(img, &sg_image_data)` で更新可能。
- sdlgpu は transfer buffer 経由で upload。`SDL_UploadToGPUBuffer(..., true)` の cycle=true で in-flight 衝突を回避。
- `PipelineKey` は `uintptr_t shader_handle` を持ち、shader handle が一致する entry を線形 walk で sweep 可能。

---

## Task 1: stb_image.h vendor + 共有 TU 化

**Files:**
- Create: `third_party/stb/stb_image.h`
- Modify: `src/stb_impl.c`

- [ ] **Step 1: stb_image.h を取得**

```bash
curl -L -o third_party/stb/stb_image.h \
  https://raw.githubusercontent.com/nothings/stb/master/stb_image.h
```

期待: `third_party/stb/stb_image.h` が存在 (~280KB)。

- [ ] **Step 2: src/stb_impl.c を更新**

現在の `stb_impl.c` は `STB_IMAGE_WRITE_IMPLEMENTATION` のみ。`STB_IMAGE_IMPLEMENTATION` も同 TU で展開する:

```c
// src/stb_impl.c (置き換え)
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "stb_image_write.h"

#define STB_IMAGE_IMPLEMENTATION
#define STBI_NO_HDR
#define STBI_NO_LINEAR
#define STBI_ONLY_PNG
#include "stb_image.h"
```

`STBI_NO_HDR` / `STBI_NO_LINEAR` / `STBI_ONLY_PNG` で不要コードを除外しサイズ削減。

- [ ] **Step 3: ビルド確認**

```bash
cmake --build build -j 2>&1 | tail -10
```

期待: warning なし、`stb_impl.c` リンク成功。既存サンプル動作不変。

- [ ] **Step 4: コミット**

```bash
git add third_party/stb/stb_image.h src/stb_impl.c
git commit -m "build: vendor stb_image.h for PNG decode"
```

---

## Task 2: Lua API に file_mtime / fnv1a64 を追加

**Files:**
- Modify: `src/lua_api.c`

- [ ] **Step 1: include 追加**

`src/lua_api.c` の include ブロック付近に:

```c
#include <sys/stat.h>
#include <time.h>
```

(既に他 include があるので並べる)

- [ ] **Step 2: l_file_mtime を追加**

`src/lua_api.c` の他の `static int l_*` 関数群と同位置に追加:

```c
static int l_file_mtime(lua_State *L) {
    const char *path = luaL_checkstring(L, 1);
    struct stat st;
    if (stat(path, &st) != 0) {
        lua_pushnil(L);
        return 1;
    }
    int64_t ns = (int64_t)st.st_mtim.tv_sec * 1000000000LL
               + (int64_t)st.st_mtim.tv_nsec;
    lua_pushinteger(L, (lua_Integer)ns);
    return 1;
}
```

- [ ] **Step 3: l_fnv1a64 を追加**

```c
static int l_fnv1a64(lua_State *L) {
    size_t n;
    const char *s = luaL_checklstring(L, 1, &n);
    uint64_t h = 0xcbf29ce484222325ULL;          // FNV offset basis
    for (size_t i = 0; i < n; ++i) {
        h ^= (unsigned char)s[i];
        h *= 0x100000001b3ULL;                    // FNV prime
    }
    lua_pushinteger(L, (lua_Integer)h);            // Lua 5.5 integers are 64-bit signed
    return 1;
}
```

- [ ] **Step 4: lua_api_register に登録**

`src/lua_api.c` の `lua_api_register` 内 (既存の `use_buffer` 等の登録の隣) に:

```c
    lua_pushcfunction(L, l_file_mtime); lua_setglobal(L, "file_mtime");
    lua_pushcfunction(L, l_fnv1a64);    lua_setglobal(L, "fnv1a64");
```

(lua_api.c 内の register 関数の既存パターンに揃える — もし `lua_register(L, "name", fn)` 形式なら同様に書く)

- [ ] **Step 5: ビルド + 動作確認**

```bash
cmake --build build -j 2>&1 | tail -5
```

期待: warning なし。

`/tmp/test_helpers.lua` を作って:

```lua
function on_init() end
function on_event(e) end
function on_quit() end
function on_frame()
   print("mtime CMakeLists.txt:", file_mtime("CMakeLists.txt"))
   print("mtime nonexistent:", file_mtime("nonexistent_file_xyz"))
   print("fnv1a64('hello'):", fnv1a64("hello"))    -- 期待値: 0xa430d84680aabd0b → -6712327145812870389 (signed 表示)
   os.exit(0)
end
```

```bash
./build/sglua /tmp/test_helpers.lua
```

期待: `mtime CMakeLists.txt: <integer>` 表示、`mtime nonexistent: nil` 表示、`fnv1a64('hello'): -6712327145812870389` 表示。

- [ ] **Step 6: コミット**

```bash
git add src/lua_api.c
git commit -m "feat(lua): add file_mtime and fnv1a64 primitives"
```

---

## Task 3: Lua API に load_png を追加

**Files:**
- Modify: `src/lua_api.c`

- [ ] **Step 1: include 追加**

`src/lua_api.c` の include 群に:

```c
#include "stb_image.h"
```

- [ ] **Step 2: l_load_png を追加**

```c
static int l_load_png(lua_State *L) {
    const char *path = luaL_checkstring(L, 1);
    int w, h, ch;
    unsigned char *pixels = stbi_load(path, &w, &h, &ch, 4);  // force RGBA
    if (!pixels) {
        SDL_Log("load_png: %s: %s", path, stbi_failure_reason());
        lua_pushnil(L);
        return 1;
    }
    int n = w * h * 4;
    lua_createtable(L, n, 0);
    for (int i = 0; i < n; ++i) {
        lua_pushinteger(L, pixels[i]);
        lua_rawseti(L, -2, i + 1);
    }
    stbi_image_free(pixels);
    lua_pushinteger(L, w);
    lua_pushinteger(L, h);
    lua_pushinteger(L, SGL_PF_RGBA8);
    return 4;  // (table, w, h, fmt)
}
```

- [ ] **Step 3: lua_api_register に登録**

```c
    lua_pushcfunction(L, l_load_png); lua_setglobal(L, "load_png");
```

(他 helper の隣に並べる)

- [ ] **Step 4: ビルド + 動作確認**

```bash
cmake --build build -j 2>&1 | tail -5
```

検証用 PNG を一時作成 (テクスチャは samples 用に後で作るので、ここでは CMakeLists.txt も含む既知 PNG が無いため、Lua で 1x1 RGBA PNG をエンコードしてテストするのは手間。代わりに stb の自己テストで OK とし、本格検証は Sample 03 refactor 時に行う):

`/tmp/test_loadpng.lua`:

```lua
function on_init() end
function on_event(e) end
function on_quit() end
function on_frame()
   local t, w, h, fmt = load_png("nonexistent.png")
   print("nonexistent:", t, w, h, fmt)   -- nil x4 期待
   os.exit(0)
end
```

```bash
./build/sglua /tmp/test_loadpng.lua
```

期待: `nonexistent: nil nil nil nil`、`load_png: ... can't fopen` エラーログ。

- [ ] **Step 5: コミット**

```bash
git add src/lua_api.c
git commit -m "feat(lua): add load_png primitive (stb_image)"
```

---

## Task 4: backend.h に update_buffer / update_image を追加

**Files:**
- Modify: `src/backend.h`
- Modify: `src/backend_sokol.c`
- Modify: `src/backend_sdlgpu.c`

このタスクではヘッダ追加とスタブ (`assert(false)` で落ちる空実装) のみ。実装は Task 5 (sokol) / Task 6 (sdlgpu)、wiring は Task 8 (use_buffer) / Task 9 (use_texture) で。

- [ ] **Step 1: backend.h に vtable field を追加**

`src/backend.h` の `RenderBackend` 構造体内、`destroy_*` の直後に:

```c
    void (*update_buffer)(BackendBuffer h, const void *data, size_t bytes);
    void (*update_image)(BackendImage h, const void *data, size_t bytes);
```

- [ ] **Step 2: backend_sokol にスタブ追加**

`src/backend_sokol.c` の vtable initializer 上方の関数定義群に:

```c
static void sk_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
    (void)h; (void)data; (void)bytes;
    SDL_Log("sk_update_buffer: not yet implemented");
    SDL_assert(0);
}
static void sk_update_image(BackendImage h, const void *data, size_t bytes) {
    (void)h; (void)data; (void)bytes;
    SDL_Log("sk_update_image: not yet implemented");
    SDL_assert(0);
}
```

`g_backend_sokol` initializer に:

```c
    .update_buffer = sk_update_buffer,
    .update_image  = sk_update_image,
```

(`.destroy_pipeline` の隣等に並べる)

- [ ] **Step 3: backend_sdlgpu にスタブ追加**

`src/backend_sdlgpu.c` は既存の static 関数を `sg_*` プレフィクスで命名している (sokol の同名 API と TU が分かれているため衝突しない)。本タスクでも同パターンに従う:

```c
static void sg_update_buffer_be(BackendBuffer h, const void *data, size_t bytes) {
    (void)h; (void)data; (void)bytes;
    SDL_Log("sg_update_buffer_be: not yet implemented");
    SDL_assert(0);
}
static void sg_update_image_be(BackendImage h, const void *data, size_t bytes) {
    (void)h; (void)data; (void)bytes;
    SDL_Log("sg_update_image_be: not yet implemented");
    SDL_assert(0);
}
```

(`_be` suffix は backend internal の意。`sg_make_buffer` 等と同様 static なので外部から見えない)

`g_backend_sdlgpu` initializer (`.make_image` 等の隣) に:

```c
    .update_buffer = sg_update_buffer_be,
    .update_image  = sg_update_image_be,
```

- [ ] **Step 4: ビルド確認**

```bash
cmake --build build -j 2>&1 | tail -5
```

期待: warning なし、リンク成功。既存サンプルは未だ update を呼ばないので動作不変。

- [ ] **Step 5: 既存 4 サンプルが両 backend で通ることを確認**

```bash
for s in samples/0[1-4]_*.lua; do
  scripts/run-headless.sh ./build/sglua "$s" --capture /tmp/p_$(basename "$s").png --capture-frame 30 2>&1 | tail -2
  echo "=> $s exit=$?"
done
```

期待: 全 exit 0。

- [ ] **Step 6: コミット**

```bash
git add src/backend.h src/backend_sokol.c src/backend_sdlgpu.c
git commit -m "feat(backend): add update_buffer/update_image vtable slots (stubs)"
```

---

## Task 5: backend_sokol を dynamic_update + update_buffer / update_image 実装

**Files:**
- Modify: `src/backend_sokol.c`

- [ ] **Step 1: sk_make_buffer を dynamic_update + 初回 update に変更**

`src/backend_sokol.c:404` 付近の `sk_make_buffer` を以下に置換:

```c
static BackendBuffer sk_make_buffer(SglBufferType type, const float *data, size_t bytes) {
    sg_buffer h = sg_make_buffer(&(sg_buffer_desc){
        .size = bytes,
        .usage = {
            .vertex_buffer  = (type == SGL_BUFFER_VERTEX),
            .index_buffer   = (type == SGL_BUFFER_INDEX),
            .dynamic_update = true,
        },
        // dynamic_update: initial data はここで渡せないので make 後に update
    });
    if (h.id == SG_INVALID_ID) return 0;
    if (data && bytes > 0) {
        sg_update_buffer(h, &(sg_range){ .ptr = data, .size = bytes });
    }
    return (uintptr_t)h.id;
}
```

- [ ] **Step 2: sk_make_image を dynamic_update + 初回 update に変更**

`src/backend_sokol.c:422` 付近の `sk_make_image` 内の `img_desc` を:

```c
    sg_image_desc img_desc = {
        .width = d->w,
        .height = d->h,
        .pixel_format = pf,
        .usage = { .dynamic_update = true },
    };
    // dynamic_update なので初期データは make 時に渡さず、make 後に update
```

そして `si->img = sg_make_image(&img_desc);` の直後に:

```c
    if (si->img.id == SG_INVALID_ID) { free(si); return 0; }
    if (d->data && d->data_bytes > 0) {
        sg_update_image(si->img, &(sg_image_data){
            .mip_levels[0] = { .ptr = d->data, .size = d->data_bytes },
        });
    }
```

(現行の `if (d->data) { img_desc.data.mip_levels[0] = ... }` ブロックを削除)

- [ ] **Step 3: sk_update_buffer / sk_update_image を実装**

Task 4 で入れたスタブを置換:

```c
static void sk_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
    if (!h || !data || bytes == 0) return;
    sg_update_buffer((sg_buffer){ .id = (uint32_t)h },
                     &(sg_range){ .ptr = data, .size = bytes });
}

static void sk_update_image(BackendImage h, const void *data, size_t bytes) {
    if (!h || !data || bytes == 0) return;
    SkImage *si = (SkImage*)h;
    sg_update_image(si->img, &(sg_image_data){
        .mip_levels[0] = { .ptr = data, .size = bytes },
    });
}
```

- [ ] **Step 4: ビルド + 既存 4 サンプル smoke**

```bash
cmake --build build -j 2>&1 | tail -5
for s in samples/0[1-4]_*.lua; do
  scripts/run-headless.sh ./build/sglua "$s" --capture /tmp/sk_$(basename "$s").png --capture-frame 30 2>&1 | grep -i "error\|warn" | head -3
  echo "=> $s exit=$?"
done
```

期待: warning なし、4 sample exit 0。validation warnings があっても以前から既知のもののみ。capture PNG が以前と同じ視覚結果になっていれば OK (生成タイミングが変わらない限り byte-identical のはず)。

- [ ] **Step 5: 旧 capture との byte-identical 比較 (リグレッションテスト)**

このタスク前にコミット済みの状態の capture を `git stash` で取り直すのは面倒。代わりに、現フェーズの sokol capture と sdlgpu capture (まだ update 未実装) を比較:

```bash
for s in samples/0[1-4]_*.lua; do
  SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua "$s" \
      --capture /tmp/sd_$(basename "$s").png --capture-frame 30 2>&1 | tail -1
  cmp /tmp/sk_$(basename "$s").png /tmp/sd_$(basename "$s").png && echo "$s: identical" || echo "$s: DIFFER"
done
```

期待: 4 sample 全て `identical`。sdlgpu 側はまだ make_buffer/image を変えていないので、sokol で dynamic_update に切り替えても両 backend 出力が一致するはず。

- [ ] **Step 6: コミット**

```bash
git add src/backend_sokol.c
git commit -m "feat(sokol): switch make_buffer/image to dynamic_update + impl update_*"
```

---

## Task 6: backend_sdlgpu の update_buffer / update_image 実装

**Files:**
- Modify: `src/backend_sdlgpu.c`

sdlgpu は make_buffer/image の usage flag に変更不要 (既に transfer-buffer 経路)。upload helper を抽出し、update でも再利用する。

- [ ] **Step 1: 既存の sg_make_buffer の upload 部分を helper に切り出し**

`src/backend_sdlgpu.c:149` 付近の `sg_make_buffer` を読み、内部の "transfer buffer 作成 → map → memcpy → unmap → command buffer で upload" 部分を以下のような helper に切り出す:

```c
// 既存ファイル内の static 関数として追加 (sg_make_buffer の手前)
static bool sg_upload_to_buffer(SDL_GPUBuffer *dst, const void *data, size_t bytes, bool cycle) {
    SDL_GPUDevice *dev = g_app->gpu_device;
    SDL_GPUTransferBuffer *tbuf = SDL_CreateGPUTransferBuffer(dev,
        &(SDL_GPUTransferBufferCreateInfo){
            .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
            .size  = (Uint32)bytes,
        });
    if (!tbuf) { SDL_Log("sg_upload_to_buffer: tbuf: %s", SDL_GetError()); return false; }

    void *map = SDL_MapGPUTransferBuffer(dev, tbuf, false);
    if (!map) {
        SDL_Log("sg_upload_to_buffer: map: %s", SDL_GetError());
        SDL_ReleaseGPUTransferBuffer(dev, tbuf);
        return false;
    }
    memcpy(map, data, bytes);
    SDL_UnmapGPUTransferBuffer(dev, tbuf);

    SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(dev);
    if (!cmd) {
        SDL_Log("sg_upload_to_buffer: cmd: %s", SDL_GetError());
        SDL_ReleaseGPUTransferBuffer(dev, tbuf);
        return false;
    }
    SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
    SDL_UploadToGPUBuffer(cp,
        &(SDL_GPUTransferBufferLocation){ .transfer_buffer = tbuf, .offset = 0 },
        &(SDL_GPUBufferRegion){ .buffer = dst, .offset = 0, .size = (Uint32)bytes },
        cycle);
    SDL_EndGPUCopyPass(cp);
    SDL_SubmitGPUCommandBuffer(cmd);
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    return true;
}
```

注: 既存ファイルでは backend 関数群の中で `g_app->gpu_device` 経路で device に到達している (file-static `g_app` 変数あり)。helper も同じ経路を使う。

`sg_make_buffer` 本体内の対応コードを `sg_upload_to_buffer(b->gpu, data, bytes, false)` の呼び出しに置換。

- [ ] **Step 2: 同様に image 用の upload helper を切り出し**

`sg_make_image` 内の transfer buffer + `SDL_UploadToGPUTexture` 部分を:

```c
static bool sg_upload_to_image(SDL_GPUTexture *dst, int w, int h,
                               const void *data, size_t bytes, bool cycle) {
    SDL_GPUDevice *dev = g_app->gpu_device;
    SDL_GPUTransferBuffer *tbuf = SDL_CreateGPUTransferBuffer(dev,
        &(SDL_GPUTransferBufferCreateInfo){
            .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
            .size  = (Uint32)bytes,
        });
    if (!tbuf) { SDL_Log("sg_upload_to_image: tbuf: %s", SDL_GetError()); return false; }
    void *map = SDL_MapGPUTransferBuffer(dev, tbuf, false);
    if (!map) {
        SDL_Log("sg_upload_to_image: map: %s", SDL_GetError());
        SDL_ReleaseGPUTransferBuffer(dev, tbuf);
        return false;
    }
    memcpy(map, data, bytes);
    SDL_UnmapGPUTransferBuffer(dev, tbuf);

    SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(dev);
    if (!cmd) {
        SDL_Log("sg_upload_to_image: cmd: %s", SDL_GetError());
        SDL_ReleaseGPUTransferBuffer(dev, tbuf);
        return false;
    }
    SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
    SDL_UploadToGPUTexture(cp,
        &(SDL_GPUTextureTransferInfo){ .transfer_buffer = tbuf, .offset = 0 },
        &(SDL_GPUTextureRegion){
            .texture = dst,
            .w = (Uint32)w, .h = (Uint32)h, .d = 1,
        },
        cycle);
    SDL_EndGPUCopyPass(cp);
    SDL_SubmitGPUCommandBuffer(cmd);
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    return true;
}
```

`sg_make_image` 内の transfer buffer 部分を `sg_upload_to_image(si->tex, w, h, d->data, d->data_bytes, false)` 呼び出しに置換。

- [ ] **Step 3: sg_update_buffer_be / sg_update_image_be を実装**

Task 4 で入れたスタブを置換 (sdlgpu の BackendBuffer は `SgBuffer*`、BackendImage は `SgImage*` キャスト):

```c
static void sg_update_buffer_be(BackendBuffer h, const void *data, size_t bytes) {
    if (!h || !data || bytes == 0) return;
    SgBuffer *b = (SgBuffer*)h;
    sg_upload_to_buffer(b->gpu, data, bytes, /*cycle=*/true);
}

static void sg_update_image_be(BackendImage h, const void *data, size_t bytes) {
    if (!h || !data || bytes == 0) return;
    SgImage *si = (SgImage*)h;
    sg_upload_to_image(si->tex, si->w, si->h, data, bytes, /*cycle=*/true);
}
```

(SgBuffer / SgImage は backend_sdlgpu.c:41-63 で定義されている)

- [ ] **Step 4: ビルド + 4 サンプル smoke (両 backend)**

```bash
cmake --build build -j 2>&1 | tail -5
for s in samples/0[1-4]_*.lua; do
  for be in sokol sdlgpu; do
    SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua "$s" \
        --capture /tmp/${be}_$(basename "$s").png --capture-frame 30 2>&1 | tail -1
    echo "  $be $s exit=$?"
  done
  cmp /tmp/sokol_$(basename "$s").png /tmp/sdlgpu_$(basename "$s").png \
    && echo "$s: identical" || echo "$s: DIFFER"
done
```

期待: 全 exit 0、4 ペア全て identical。

- [ ] **Step 5: コミット**

```bash
git add src/backend_sdlgpu.c
git commit -m "feat(sdlgpu): factor upload helpers and impl update_*"
```

---

## Task 7: ResEntry.version を 64-bit 化

**Files:**
- Modify: `src/resources.h`
- Modify: `src/lua_api.c`

FNV-1a 64 ハッシュを version として渡せるよう、`ResEntry.version` を `int` から `int64_t` に拡張する。`lua_api.c` の各 `l_use_*` の `(int)luaL_checkinteger` も `(int64_t)` に変更。

- [ ] **Step 1: resources.h を更新**

`src/resources.h:15` の `int version;` を `int64_t version;` に変更。`<stdint.h>` インクルードを (まだなければ) 追加。

- [ ] **Step 2: lua_api.c の 3 箇所を更新**

```bash
grep -n "int version = (int)luaL_checkinteger" src/lua_api.c
```

期待: 3 行 (l_use_buffer / l_use_texture / l_use_shader)。それぞれを:

```c
int64_t version = (int64_t)luaL_checkinteger(L, <slot>);
```

に変更。`e->version == version` の比較は型変換不要 (両側 int64_t)。

- [ ] **Step 3: ビルド + 4 サンプル両 backend smoke**

```bash
cmake --build build -j 2>&1 | tail -5
for s in samples/0[1-4]_*.lua; do
  for be in sokol sdlgpu; do
    SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua "$s" \
        --capture /tmp/${be}_$(basename "$s").png --capture-frame 30 2>&1 | tail -1
  done
  cmp /tmp/sokol_$(basename "$s").png /tmp/sdlgpu_$(basename "$s").png \
    && echo "$s: identical" || echo "$s: DIFFER"
done
```

期待: 全 identical (version=1 固定なので動作不変)。

- [ ] **Step 4: コミット**

```bash
git add src/resources.h src/lua_api.c
git commit -m "refactor(resources): widen ResEntry.version to int64_t for hash versioning"
```

---

## Task 8: lua_api use_buffer に in-place update path を追加

**Files:**
- Modify: `src/lua_api.c`

- [ ] **Step 1: l_use_buffer を拡張**

`src/lua_api.c:83` 付近の `l_use_buffer`、現在のコードは「version 違うと destroy + make」する。これを「同サイズなら update、違うサイズなら destroy + make」に変更:

`l_use_buffer` 内の `if (e->u.buf.h != 0) g_backend->destroy_buffer(e->u.buf.h);` 行 (lua_api.c:115) を以下に置換:

```c
    size_t new_bytes = (size_t)n * sizeof(float);
    if (e->u.buf.h != 0 && e->u.buf.size_bytes == new_bytes && e->u.buf.type == (SglBufferType)type) {
        // in-place update
        g_backend->update_buffer(e->u.buf.h, data, new_bytes);
    } else {
        if (e->u.buf.h != 0) g_backend->destroy_buffer(e->u.buf.h);
        e->u.buf.h = g_backend->make_buffer((SglBufferType)type, data, new_bytes);
        e->u.buf.type = (SglBufferType)type;
        e->u.buf.size_bytes = new_bytes;
    }
```

(直後の `e->u.buf.h = ...; e->u.buf.type = ...; e->u.buf.size_bytes = ...;` 3 行は上の else ブロックに吸収するので削除)

`e->version = version;` と `free(data);` はそのまま。

- [ ] **Step 2: ビルド + 4 サンプル smoke (両 backend、byte-identical)**

```bash
cmake --build build -j 2>&1 | tail -5
for s in samples/0[1-4]_*.lua; do
  for be in sokol sdlgpu; do
    SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua "$s" \
        --capture /tmp/${be}_$(basename "$s").png --capture-frame 30 2>&1 | tail -1
  done
  cmp /tmp/sokol_$(basename "$s").png /tmp/sdlgpu_$(basename "$s").png \
    && echo "$s: identical" || echo "$s: DIFFER"
done
```

期待: 全 identical。既存サンプルは version=1 固定で update path に入らないため挙動不変。

- [ ] **Step 3: in-place update 経路の積極テスト**

`/tmp/test_update_buffer.lua`:

```lua
local v1 = { 0.0, 0.5, 0.0,   -0.5, -0.5, 0.0,   0.5, -0.5, 0.0 }
local v2 = { 0.0, 0.7, 0.0,   -0.5, -0.5, 0.0,   0.5, -0.5, 0.0 }

local vs = [[ struct VSIn { float3 pos : POSITION; }; struct VSOut { float4 pos : SV_Position; };
[shader("vertex")] VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; } ]]
local fs = [[ [shader("fragment")] float4 fs_main() : SV_Target { return float4(1,0.5,0,1); } ]]

local frame = 0
function on_init() config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" }) end
function on_event() end
function on_quit() end
function on_frame()
   frame = frame + 1
   local s = use_shader("us", vs, fs, 1)
   local v = (frame < 15) and v1 or v2
   local ver = (frame < 15) and 1 or 2
   local b = use_buffer("ub", VERTEX, v, ver)
   begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
      draw(3, { verts = b }, { shader = s })
   end_pass()
end
```

```bash
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua /tmp/test_update_buffer.lua \
      --capture /tmp/upd_${be}.png --capture-frame 30 2>&1 | tail -2
done
cmp /tmp/upd_sokol.png /tmp/upd_sdlgpu.png && echo "identical" || echo "DIFFER"
```

期待: 両 capture identical、頂点 v2 が反映された変形三角形が描画されている (cap frame=30 はフレーム 15 以降)。

- [ ] **Step 4: コミット**

```bash
git add src/lua_api.c
git commit -m "feat(lua): use_buffer in-place update on version mismatch (same size)"
```

---

## Task 9: lua_api use_texture に in-place update path を追加

**Files:**
- Modify: `src/lua_api.c`

- [ ] **Step 1: l_use_texture を拡張**

`src/lua_api.c:126` 付近の `l_use_texture`、現在は version 違いで destroy + make。これを (w,h,fmt) が同じで data がある時に update_image を選ぶように変更:

`l_use_texture` 内の `if (e->u.tex.h != 0) g_backend->destroy_image(e->u.tex.h);` 周辺 (lua_api.c:170-179) を以下に置換:

```c
    size_t new_bytes = pixels ? (size_t)w * (size_t)h * (size_t)bpp : 0;
    bool same_shape = (e->u.tex.h != 0)
                      && (e->u.tex.w == w)
                      && (e->u.tex.h_ == h)
                      && (e->u.tex.fmt == (SglPixelFormat)fmt);
    if (same_shape && pixels && new_bytes > 0) {
        // in-place update
        g_backend->update_image(e->u.tex.h, pixels, new_bytes);
    } else {
        if (e->u.tex.h != 0) g_backend->destroy_image(e->u.tex.h);
        ImageDesc d = {
            .fmt = (SglPixelFormat)fmt,
            .w = w, .h = h,
            .data = pixels,
            .data_bytes = new_bytes,
        };
        e->u.tex.h = g_backend->make_image(&d);
        e->u.tex.w   = w;
        e->u.tex.h_  = h;
        e->u.tex.fmt = (SglPixelFormat)fmt;
    }
    e->version   = version;
```

(既存の `ImageDesc d = {...}; e->u.tex.h = ...` ブロックは else に吸収済み)

- [ ] **Step 2: ビルド + 4 サンプル byte-identical**

```bash
cmake --build build -j 2>&1 | tail -5
for s in samples/0[1-4]_*.lua; do
  for be in sokol sdlgpu; do
    SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua "$s" \
        --capture /tmp/${be}_$(basename "$s").png --capture-frame 30 2>&1 | tail -1
  done
  cmp /tmp/sokol_$(basename "$s").png /tmp/sdlgpu_$(basename "$s").png \
    && echo "$s: identical" || echo "$s: DIFFER"
done
```

期待: 全 identical。

- [ ] **Step 3: テクスチャ in-place update テスト**

Sample 03 をベースにテクスチャだけ version 切り替え:

`/tmp/test_update_tex.lua`:

```lua
-- 16x16 RGBA pixels: フレーム < 15 は赤、 >= 15 は緑
local function pixels(c)
   local t = {}
   for i = 1, 16*16 do
      t[#t+1] = c[1]; t[#t+1] = c[2]; t[#t+1] = c[3]; t[#t+1] = 255
   end
   return t
end

local verts = { 0,0.5,0, 0,0,  -0.5,-0.5,0, 0,1,  0.5,-0.5,0, 1,0 }
local vs = [[ struct VSIn{float3 pos:POSITION; float2 uv:TEXCOORD0;};
struct VSOut{float4 pos:SV_Position; float2 uv:TEXCOORD0;};
[shader("vertex")] VSOut vs_main(VSIn i){ VSOut o; o.pos=float4(i.pos,1); o.uv=i.uv; return o;} ]]
local fs = [[ Texture2D<float4> diffuse; SamplerState diffuse_sampler;
struct FSIn{float2 uv:TEXCOORD0;};
[shader("fragment")] float4 fs_main(FSIn i):SV_Target { return diffuse.Sample(diffuse_sampler, i.uv); } ]]

local frame = 0
function on_init() config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" }) end
function on_event() end function on_quit() end
function on_frame()
   frame = frame + 1
   local s = use_shader("ts", vs, fs, 1)
   local b = use_buffer("tb", VERTEX, verts, 1)
   local color = (frame < 15) and {255, 0, 0} or {0, 255, 0}
   local ver = (frame < 15) and 1 or 2
   local tx = use_texture("ttex", 16, 16, RGBA8, pixels(color), ver)
   begin_pass({ target = main_tex, clear_color = {0,0,0,1} })
      draw(3, { verts = b, diffuse = tx }, { shader = s })
   end_pass()
end
```

```bash
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua /tmp/test_update_tex.lua \
      --capture /tmp/utex_${be}.png --capture-frame 30 2>&1 | tail -2
done
cmp /tmp/utex_sokol.png /tmp/utex_sdlgpu.png && echo "identical" || echo "DIFFER"
```

期待: 両 capture identical、緑色の三角形が描画されている。

- [ ] **Step 4: コミット**

```bash
git add src/lua_api.c
git commit -m "feat(lua): use_texture in-place update on version mismatch (same shape)"
```

---

## Task 10: pipeline_cache_invalidate_shader を追加

**Files:**
- Modify: `src/pipeline.h`
- Modify: `src/pipeline.c`

- [ ] **Step 1: pipeline.h に宣言追加**

`src/pipeline.h` の末尾 (extern 宣言 / pipeline_cache_get の隣) に:

```c
// 指定 shader handle を参照する全 pipeline entry を破棄しキャッシュから外す。
// shader recompile で旧 handle が無効になる際に呼ぶ。
void pipeline_cache_invalidate_shader(PipelineCache *c, uintptr_t old_shader);
```

- [ ] **Step 2: pipeline.c に実装追加**

`src/pipeline.c` 末尾 (`pipeline_cache_shutdown` の隣) に:

```c
void pipeline_cache_invalidate_shader(PipelineCache *c, uintptr_t old_shader) {
    for (int i = 0; i < PIPELINE_BUCKETS; ++i) {
        PipelineEntry **prev = &c->buckets[i];
        PipelineEntry *e = c->buckets[i];
        while (e) {
            PipelineEntry *next = e->next;
            if (e->key.shader_handle == old_shader) {
                if (e->pip) g_backend->destroy_pipeline(e->pip);
                *prev = next;
                free(e);
            } else {
                prev = &e->next;
            }
            e = next;
        }
    }
}
```

- [ ] **Step 3: ビルド + 既存サンプルが動くことを確認**

```bash
cmake --build build -j 2>&1 | tail -5
scripts/run-headless.sh ./build/sglua samples/01_triangle.lua --capture /tmp/p1.png --capture-frame 30 2>&1 | tail -1
echo "exit=$?"
```

期待: warning なし、exit 0。まだ呼び出し側はないので動作不変。

- [ ] **Step 4: コミット**

```bash
git add src/pipeline.h src/pipeline.c
git commit -m "feat(pipeline): add pipeline_cache_invalidate_shader for shader recompile"
```

---

## Task 11: lua_api use_shader に recompile + sweep を追加

**Files:**
- Modify: `src/lua_api.c`

- [ ] **Step 1: l_use_shader を拡張**

`src/lua_api.c:191` 付近の `l_use_shader` を読む。現行は version 違い時の destroy + make ブロックがあるが、「**新 shader compile が成功した時のみ pipeline cache を sweep + 旧 shader を destroy + e->version 更新**」の順に再構成する。

既存実装は `shader_compile(vs, fs, ...)` (shader.h:67) で SPIR-V + reflection を取得し、`g_backend->make_shader(&desc)` で BackendShader を得るパターン (lua_api.c:215 周辺)。

既存実装 (`lua_api.c:206-235`) は `ShaderBlob vsb, fsb` + `ShaderReflection refl` + `ShaderTargetBackend tgt` で `shader_compile` を呼び、失敗時は `luaL_error` (Lua 例外で kill)。Hot reload では失敗時も旧 shader で描画継続したいので **`SDL_Log` ログ + 旧 handle 維持 + `e->version` 据え置き** に変更する。

既存の destroy + make ブロック (`lua_api.c:226-229`) を以下に置換:

```c
    // version mismatch path. Compile into LOCAL temporaries first; only swap on success.
    char err[1024];
    ShaderBlob vsb = {0}, fsb = {0};
    ShaderReflection new_refl;
    ShaderTargetBackend tgt = (g_backend && g_backend->name &&
                                strcmp(g_backend->name, "sdlgpu") == 0)
                              ? SHADER_TARGET_SDLGPU
                              : SHADER_TARGET_SOKOL;
    if (!shader_compile(vs, fs, tgt, &vsb, &fsb, &new_refl, err, sizeof(err))) {
        shader_blob_free(&vsb);
        shader_blob_free(&fsb);
        SDL_Log("use_shader: recompile failed for key '%s': %s (keeping old)", key, err);
        // 旧 handle 維持、version 据え置き。次回 version 違いで再試行。
        push_shader_ref(L, key);
        return 1;
    }
    ShaderDesc sd = {
        .vs_spirv = vsb.spirv, .vs_bytes = vsb.bytes,
        .fs_spirv = fsb.spirv, .fs_bytes = fsb.bytes,
        .refl = &new_refl,
    };
    BackendShader new_h = g_backend->make_shader(&sd);
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
    if (!new_h) {
        SDL_Log("use_shader: make_shader failed for key '%s' (keeping old)", key);
        push_shader_ref(L, key);
        return 1;
    }

    // success: sweep pipeline cache for old shader, then destroy.
    BackendShader old_h = e->u.sh.h;
    if (old_h) {
        pipeline_cache_invalidate_shader(&g_app_for_lua->pip_cache, (uintptr_t)old_h);
        g_backend->destroy_shader(old_h);
    }
    e->u.sh.h    = new_h;
    e->u.sh.refl = new_refl;
    e->version   = version;
```

`l_use_shader` 末尾の重複した `shader_blob_free` 呼び出し / `push_shader_ref` の制御フローは置換後の構造に合うよう整える (上記置換ブロックは内部で free を済ませ、最後に push_shader_ref + return する流れ)。

注:
- App field name は `pip_cache` (app.h:55)。
- `ShaderBlob` は `{ const uint32_t *spirv; size_t bytes; }` 形式 (実装時 shader.h で確認)。
- `shader_blob_free` を必ず両ケースで呼ぶ (リーク防止)。

`src/lua_api.c` の include に:

```c
#include "pipeline.h"
```

(既に追加済みなら何もしない)

- [ ] **Step 2: App 構造体の field 名を確認**

```bash
grep -n "PipelineCache\|pip_cache" /home/neguse/ghq/github.com/neguse/sglua/src/app.h
```

期待: `app.h:55` 付近に `PipelineCache pip_cache;` を確認。Step 1 のコード内 `g_app_for_lua->pip_cache` がこれに一致していること。

- [ ] **Step 3: ビルド + 既存 4 サンプル両 backend smoke**

```bash
cmake --build build -j 2>&1 | tail -5
for s in samples/0[1-4]_*.lua; do
  for be in sokol sdlgpu; do
    SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua "$s" \
        --capture /tmp/${be}_$(basename "$s").png --capture-frame 30 2>&1 | tail -1
  done
  cmp /tmp/sokol_$(basename "$s").png /tmp/sdlgpu_$(basename "$s").png \
    && echo "$s: identical" || echo "$s: DIFFER"
done
```

期待: 全 identical (既存サンプルは version=1 固定なので動作不変)。

- [ ] **Step 4: shader recompile + sweep の積極テスト**

`/tmp/test_recompile.lua`:

```lua
local vs = [[ struct VSIn{float3 pos:POSITION;}; struct VSOut{float4 pos:SV_Position;};
[shader("vertex")] VSOut vs_main(VSIn i){VSOut o; o.pos=float4(i.pos,1); return o;} ]]
local fs1 = [[ [shader("fragment")] float4 fs_main():SV_Target{return float4(1,0,0,1);} ]]
local fs2 = [[ [shader("fragment")] float4 fs_main():SV_Target{return float4(0,1,0,1);} ]]
local verts = {0,0.5,0, -0.5,-0.5,0, 0.5,-0.5,0}

local frame = 0
function on_init() config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" }) end
function on_event() end function on_quit() end
function on_frame()
   frame = frame + 1
   local fs = (frame < 15) and fs1 or fs2
   local ver = (frame < 15) and 1 or 2
   local s = use_shader("recomp", vs, fs, ver)
   local b = use_buffer("rb", VERTEX, verts, 1)
   begin_pass({ target = main_tex, clear_color = {0,0,0,1} })
      draw(3, { verts = b }, { shader = s })
   end_pass()
end
```

```bash
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua /tmp/test_recompile.lua \
      --capture /tmp/rec_${be}.png --capture-frame 30 2>&1 | tail -2
done
cmp /tmp/rec_sokol.png /tmp/rec_sdlgpu.png && echo "identical" || echo "DIFFER"
```

期待: 両 capture identical。緑色の三角形 (fs2 が反映されている)。

- [ ] **Step 5: コミット**

```bash
git add src/lua_api.c
git commit -m "feat(lua): use_shader recompile with pipeline cache sweep"
```

---

## Task 12: samples/sg_io.lua helper モジュール

**Files:**
- Create: `samples/sg_io.lua`

- [ ] **Step 1: sg_io.lua を作成**

```lua
-- samples/sg_io.lua
-- File-input helper with mtime fast-path + content hash version.
--
-- Usage:
--   local sg_io = dofile("samples/sg_io.lua")
--   local src,  ver = sg_io.load_text("foo.slang")
--   local tab,  ver = sg_io.load_floats("foo.verts.lua")
--   local px, w, h, fmt, ver = sg_io.load_png("foo.png")
--
-- Cache: path -> { mtime, bytes, hash, parsed }
-- Fast path: same mtime -> return cached parsed + hash (no read, no hash).
-- Slow path: stat, read bytes, fnv1a64 -> if hash differs, reparse.

local M = {}
local cache = {}

local function read_bytes(path)
   local f = io.open(path, "rb")
   if not f then return nil end
   local s = f:read("*a")
   f:close()
   return s
end

-- Returns (parsed, version, changed). changed == true ⇔ hash actually changed.
local function refresh(path, parse_fn)
   local mtime = file_mtime(path)
   if not mtime then return nil end
   local c = cache[path]
   if c and c.mtime == mtime then
      return c.parsed, c.hash, false
   end
   local bytes = read_bytes(path)
   if not bytes then return nil end
   local hash = fnv1a64(bytes)
   if c and c.hash == hash then
      c.mtime = mtime    -- 内容変わってない、mtime だけ更新
      return c.parsed, hash, false
   end
   local parsed = parse_fn(bytes, path)
   if parsed == nil then
      -- parse 失敗、cache 更新せずに前回値を維持
      if c then return c.parsed, c.hash, false end
      return nil
   end
   cache[path] = { mtime = mtime, bytes = bytes, hash = hash, parsed = parsed }
   return parsed, hash, true
end

function M.load_text(path)
   return refresh(path, function(s) return s end)
end

function M.load_floats(path)
   local parsed, ver = refresh(path, function(src, p)
      local chunk, err = load(src, "@" .. p, "t")      -- 既定 env (= 呼び出し元) を使う
      if not chunk then
         print("sg_io.load_floats: parse error in " .. p .. ": " .. tostring(err))
         return nil
      end
      local ok, t = pcall(chunk)
      if not ok then
         print("sg_io.load_floats: exec error in " .. p .. ": " .. tostring(t))
         return nil
      end
      if type(t) ~= "table" then
         print("sg_io.load_floats: " .. p .. " did not return a table")
         return nil
      end
      return t
   end)
   return parsed, ver
end

function M.load_png(path)
   -- PNG は parsed = { px = {...}, w, h, fmt }
   local parsed, ver = refresh(path, function(_bytes, p)
      -- _bytes は使わず C 側に再読み込みさせる
      -- (代替: stb_load_from_memory にしたいが現状未実装)
      local px, w, h, fmt = load_png(p)
      if px == nil then return nil end
      return { px = px, w = w, h = h, fmt = fmt }
   end)
   if not parsed then return nil end
   return parsed.px, parsed.w, parsed.h, parsed.fmt, ver
end

return M
```

注: `load_png` は内部で C の `load_png(path)` を再呼び出しする (バイト列から復元するメモリ版を作るほうが対称的だが、PoC スコープを抑える)。fast-path の mtime check は同様に効くので overhead は最小。

- [ ] **Step 2: 単体動作確認**

`/tmp/test_sg_io.lua`:

```lua
local sg_io = dofile("samples/sg_io.lua")

function on_init() end function on_event() end function on_quit() end
function on_frame()
   -- text load
   local s, v = sg_io.load_text("CMakeLists.txt")
   print("text len:", s and #s, "ver:", v)

   -- floats (.lua) — 既存サンプルから流用しても良いが、ad-hoc に作る
   local f = io.open("/tmp/_floats.lua", "w")
   f:write("return {1,2,3,4}")
   f:close()
   local t, v2 = sg_io.load_floats("/tmp/_floats.lua")
   print("floats:", t and t[1], t and t[4], "ver:", v2)

   -- 2 回目の load_text → 同 hash、ver 同じ
   local s2, v3 = sg_io.load_text("CMakeLists.txt")
   print("ver_match:", v == v3)

   os.exit(0)
end
```

```bash
./build/sglua /tmp/test_sg_io.lua
```

期待: `text len: <数千> ver: <int>`、`floats: 1 4 ver: <int>`、`ver_match: true`。

- [ ] **Step 3: コミット**

```bash
git add samples/sg_io.lua
git commit -m "feat(samples): add sg_io.lua helper with mtime+hash cache"
```

---

## Task 13: Sample 01 を file-input に refactor

**Files:**
- Create: `samples/data/01_triangle.vs.slang`
- Create: `samples/data/01_triangle.fs.slang`
- Create: `samples/data/01_triangle.verts.lua`
- Modify: `samples/01_triangle.lua`

- [ ] **Step 1: 既存 01_triangle.lua から shader / verts を抽出**

`samples/01_triangle.lua` を読み、以下にコピー (内容は既存サンプルそのまま):

`samples/data/01_triangle.vs.slang`:
```slang
struct VSIn  { float3 pos : POSITION; };
struct VSOut { float4 pos : SV_Position; };
[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 1.0);
    return o;
}
```

`samples/data/01_triangle.fs.slang`:
```slang
[shader("fragment")]
float4 fs_main() : SV_Target {
    return float4(1.0, 0.5, 0.0, 1.0);
}
```

(現行 01_triangle.lua のオレンジ色 `{1, 0.5, 0, 1}` に合わせる。実装時には既存ファイル内容を確認して齟齬を無くすこと)

`samples/data/01_triangle.verts.lua`:
```lua
return {
    0.0,  0.5, 0.0,
   -0.5, -0.5, 0.0,
    0.5, -0.5, 0.0,
}
```

- [ ] **Step 2: 01_triangle.lua を sg_io 経由に書き換え**

```lua
-- samples/01_triangle.lua
local sg_io = dofile("samples/sg_io.lua")

function on_init()
    config({ backend = arg and arg[1] or os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
    local vs, vsv = sg_io.load_text("samples/data/01_triangle.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/01_triangle.fs.slang")
    local verts, vv = sg_io.load_floats("samples/data/01_triangle.verts.lua")
    if not vs or not fs or not verts then return end
    local s = use_shader("01_shader", vs, fs, vsv ~ fsv)   -- XOR で結合
    local b = use_buffer("01_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b }, { shader = s })
    end_pass()
end
```

clear_color 値は既存と一致させる (実装時に確認)。

- [ ] **Step 3: ビルド + 両 backend で smoke + capture 比較**

```bash
cmake --build build -j 2>&1 | tail -3
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua samples/01_triangle.lua \
      --capture /tmp/01_${be}.png --capture-frame 30 2>&1 | tail -1
  echo "$be exit=$?"
done
cmp /tmp/01_sokol.png /tmp/01_sdlgpu.png && echo "identical" || echo "DIFFER"
```

期待: 両 exit 0、identical。

(refactor 前後の capture 比較は git stash 等で確認可能だが、視覚的にオレンジ三角形が出れば OK。byte-identical を厳密に求めるなら refactor 前にコミットからの capture を取って比べる)

- [ ] **Step 4: コミット**

```bash
git add samples/01_triangle.lua samples/data/01_triangle.vs.slang \
        samples/data/01_triangle.fs.slang samples/data/01_triangle.verts.lua
git commit -m "refactor(samples): 01_triangle uses sg_io file inputs"
```

---

## Task 14: Sample 02 を file-input に refactor

**Files:**
- Create: `samples/data/02_vcol.vs.slang`
- Create: `samples/data/02_vcol.fs.slang`
- Create: `samples/data/02_vcol.verts.lua`
- Modify: `samples/02_vertex_color.lua`

- [ ] **Step 1: 既存 02_vertex_color.lua から抽出**

実装時に `samples/02_vertex_color.lua` を読み、以下に対応コピー:

- `samples/data/02_vcol.vs.slang` ← 既存 vs (POSITION + COLOR attribute、補間)
- `samples/data/02_vcol.fs.slang` ← 既存 fs (vertex color をそのまま return)
- `samples/data/02_vcol.verts.lua` ← 既存 verts table

- [ ] **Step 2: 02_vertex_color.lua を sg_io 経由に書き換え**

```lua
local sg_io = dofile("samples/sg_io.lua")

function on_init()
    config({ backend = arg and arg[1] or os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
    local vs, vsv = sg_io.load_text("samples/data/02_vcol.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/02_vcol.fs.slang")
    local verts, vv = sg_io.load_floats("samples/data/02_vcol.verts.lua")
    if not vs or not fs or not verts then return end
    local s = use_shader("02_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("02_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b }, { shader = s })
    end_pass()
end
```

(`clear_color` は既存と一致させる)

- [ ] **Step 3: ビルド + 両 backend smoke + identical**

```bash
cmake --build build -j 2>&1 | tail -3
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua samples/02_vertex_color.lua \
      --capture /tmp/02_${be}.png --capture-frame 30 2>&1 | tail -1
done
cmp /tmp/02_sokol.png /tmp/02_sdlgpu.png && echo "identical" || echo "DIFFER"
```

期待: identical。

- [ ] **Step 4: コミット**

```bash
git add samples/02_vertex_color.lua samples/data/02_vcol.vs.slang \
        samples/data/02_vcol.fs.slang samples/data/02_vcol.verts.lua
git commit -m "refactor(samples): 02_vertex_color uses sg_io file inputs"
```

---

## Task 15: Sample 03 を file-input + 外部 PNG に refactor

**Files:**
- Create: `samples/data/03_tex.vs.slang`
- Create: `samples/data/03_tex.fs.slang`
- Create: `samples/data/03_tex.verts.lua`
- Create: `samples/data/03_tex.png` (16x16 RGBA チェッカー)
- Modify: `samples/03_texture.lua`

- [ ] **Step 1: 既存 03_texture.lua の procedural チェッカーを PNG に書き出すワンショット**

`/tmp/gen_checker.lua`:

```lua
-- 既存 03_texture.lua のチェッカー生成ロジックをコピー、最後に capture せず stbi_write_png 経由で書き出す
-- ここでは Lua から PNG を吐き出せないので、代替: capture の仕組みを利用するか、 sglua を使わずに専用 C ツールを書く。
-- 簡単な代案: sglua で 1 フレーム描画して capture するのではなく、Python ワンライナーで生成。
```

代替手段 (より確実): Python で生成

```bash
python3 -c "
from struct import pack
import zlib
W, H = 16, 16
data = bytearray()
for y in range(H):
    data.append(0)  # filter
    for x in range(W):
        c = 255 if ((x // 4) ^ (y // 4)) & 1 else 64
        data.extend([c, c, c, 255])
def chunk(t, d):
    return pack('>I', len(d)) + t + d + pack('>I', zlib.crc32(t + d))
sig = b'\x89PNG\r\n\x1a\n'
ihdr = pack('>IIBBBBB', W, H, 8, 6, 0, 0, 0)
idat = zlib.compress(bytes(data))
with open('samples/data/03_tex.png', 'wb') as f:
    f.write(sig + chunk(b'IHDR', ihdr) + chunk(b'IDAT', idat) + chunk(b'IEND', b''))
print('wrote samples/data/03_tex.png')
"
```

実装時、既存 03_texture.lua のチェッカーが何 px / 何色なのかを確認し、PNG 内容が描画結果に視覚的に一致するよう調整する (色や格子サイズが違う場合は上の Python スクリプトを合わせる)。

- [ ] **Step 2: 既存 03_texture.lua から shader / verts を抽出**

- `samples/data/03_tex.vs.slang` ← 既存 vs (POSITION + TEXCOORD0)
- `samples/data/03_tex.fs.slang` ← 既存 fs (Sample(diffuse, uv))
- `samples/data/03_tex.verts.lua` ← 既存 verts table

- [ ] **Step 3: 03_texture.lua を sg_io 経由に書き換え**

```lua
local sg_io = dofile("samples/sg_io.lua")

function on_init()
    config({ backend = arg and arg[1] or os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
    local vs, vsv = sg_io.load_text("samples/data/03_tex.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/03_tex.fs.slang")
    local verts, vv = sg_io.load_floats("samples/data/03_tex.verts.lua")
    local px, w, h, fmt, pv = sg_io.load_png("samples/data/03_tex.png")
    if not vs or not fs or not verts or not px then return end
    local s = use_shader("03_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("03_verts", VERTEX, verts, vv)
    local t = use_texture("03_tex", w, h, fmt, px, pv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b, diffuse = t }, { shader = s })
    end_pass()
end
```

- [ ] **Step 4: ビルド + 両 backend smoke + identical**

```bash
cmake --build build -j 2>&1 | tail -3
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua samples/03_texture.lua \
      --capture /tmp/03_${be}.png --capture-frame 30 2>&1 | tail -1
done
cmp /tmp/03_sokol.png /tmp/03_sdlgpu.png && echo "identical" || echo "DIFFER"
```

期待: identical。視覚的にチェッカーパターンが描画されている。

- [ ] **Step 5: コミット**

```bash
git add samples/03_texture.lua samples/data/03_tex.vs.slang \
        samples/data/03_tex.fs.slang samples/data/03_tex.verts.lua \
        samples/data/03_tex.png
git commit -m "refactor(samples): 03_texture uses sg_io file inputs (incl. external PNG)"
```

---

## Task 16: Sample 04 を file-input に refactor

**Files:**
- Create: `samples/data/04_mvp.vs.slang`
- Create: `samples/data/04_mvp.fs.slang`
- Create: `samples/data/04_mvp.verts.lua`
- Modify: `samples/04_mvp.lua`

- [ ] **Step 1: 既存 04_mvp.lua から抽出**

- `samples/data/04_mvp.vs.slang` ← 既存 vs (uniforms / mvp)
- `samples/data/04_mvp.fs.slang` ← 既存 fs (vertex color)
- `samples/data/04_mvp.verts.lua` ← 既存 verts table

- [ ] **Step 2: 04_mvp.lua を sg_io 経由に書き換え**

```lua
local sg_io = dofile("samples/sg_io.lua")

local function rot_z(theta)
  local c, s = math.cos(theta), math.sin(theta)
  return { c, s, 0, 0,  -s, c, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }
end

local t = 0
function on_init()
    config({ backend = arg and arg[1] or os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
  t = t + 1/60
  local vs, vsv = sg_io.load_text("samples/data/04_mvp.vs.slang")
  local fs, fsv = sg_io.load_text("samples/data/04_mvp.fs.slang")
  local verts, vv = sg_io.load_floats("samples/data/04_mvp.verts.lua")
  if not vs or not fs or not verts then return end
  local s = use_shader("04_shader", vs, fs, vsv ~ fsv)
  local b = use_buffer("04_verts", VERTEX, verts, vv)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b, uniforms = { mvp = rot_z(t) } },
            { shader = s, depth = false, cull = NONE })
  end_pass()
end
```

- [ ] **Step 3: ビルド + 両 backend smoke + identical**

```bash
cmake --build build -j 2>&1 | tail -3
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua samples/04_mvp.lua \
      --capture /tmp/04_${be}.png --capture-frame 30 2>&1 | tail -1
done
cmp /tmp/04_sokol.png /tmp/04_sdlgpu.png && echo "identical" || echo "DIFFER"
```

期待: identical。視覚的に回転中の (フレーム 30 = t≒0.5 ラジアン回転した) 三角形が描画されている。

- [ ] **Step 4: コミット**

```bash
git add samples/04_mvp.lua samples/data/04_mvp.vs.slang \
        samples/data/04_mvp.fs.slang samples/data/04_mvp.verts.lua
git commit -m "refactor(samples): 04_mvp uses sg_io file inputs"
```

---

## Task 17: Live-edit + pipeline cache sweep の最終検証

**Files:**
- なし (検証のみ、必要なら CHANGELOG / 一時ファイル)

- [ ] **Step 1: live-edit (shader) — 視覚確認**

Sample 01 を起動 (実 GPU 環境):

```bash
./build/sglua samples/01_triangle.lua &
PID=$!
sleep 1
# fs.slang を変える (オレンジ → 紫)
sed -i 's/1.0, 0.5, 0.0, 1.0/0.5, 0.0, 1.0, 1.0/' samples/data/01_triangle.fs.slang
sleep 1
# 元に戻す
sed -i 's/0.5, 0.0, 1.0, 1.0/1.0, 0.5, 0.0, 1.0/' samples/data/01_triangle.fs.slang
sleep 1
kill $PID
```

期待: 三角形がオレンジ → 紫 → オレンジ と変化する。

ヘッドレス環境で確認するなら、capture を 2 回別フレームで取って比較:

```bash
# 元の状態でフレーム 30
scripts/run-headless.sh ./build/sglua samples/01_triangle.lua --capture /tmp/le_orig.png --capture-frame 30
# 編集
sed -i 's/1.0, 0.5, 0.0, 1.0/0.5, 0.0, 1.0, 1.0/' samples/data/01_triangle.fs.slang
# 再起動して capture
scripts/run-headless.sh ./build/sglua samples/01_triangle.lua --capture /tmp/le_edited.png --capture-frame 30
# 戻す
sed -i 's/0.5, 0.0, 1.0, 1.0/1.0, 0.5, 0.0, 1.0/' samples/data/01_triangle.fs.slang
# 違うはず
cmp /tmp/le_orig.png /tmp/le_edited.png && echo "ERROR: SAME" || echo "OK: different"
```

期待: `OK: different` (色が違うので PNG も byte 違う)。

- [ ] **Step 2: live-edit (verts) — sed で頂点を動かして確認**

```bash
cp samples/data/04_mvp.verts.lua /tmp/04_verts.bak
scripts/run-headless.sh ./build/sglua samples/04_mvp.lua --capture /tmp/v_orig.png --capture-frame 30
# 頂点 Y を 0.5 → 0.7 に変更
sed -i 's/0\.5, 0\.0/0.7, 0.0/' samples/data/04_mvp.verts.lua
scripts/run-headless.sh ./build/sglua samples/04_mvp.lua --capture /tmp/v_edited.png --capture-frame 30
mv /tmp/04_verts.bak samples/data/04_mvp.verts.lua
cmp /tmp/v_orig.png /tmp/v_edited.png && echo "ERROR: SAME" || echo "OK: different"
```

期待: `OK: different`。

- [ ] **Step 3: live-edit (texture) — png 差し替え**

```bash
cp samples/data/03_tex.png /tmp/03_tex.bak
scripts/run-headless.sh ./build/sglua samples/03_texture.lua --capture /tmp/t_orig.png --capture-frame 30
# checker のサイズや色を変えた別 PNG を用意
python3 -c "
from struct import pack; import zlib
W,H=16,16
data=bytearray()
for y in range(H):
    data.append(0)
    for x in range(W):
        c = (x*16, y*16, (x+y)*8, 255)
        data.extend(c)
def ch(t,d): return pack('>I',len(d))+t+d+pack('>I',zlib.crc32(t+d))
sig=b'\x89PNG\r\n\x1a\n'
ihdr=pack('>IIBBBBB',W,H,8,6,0,0,0)
idat=zlib.compress(bytes(data))
open('samples/data/03_tex.png','wb').write(sig+ch(b'IHDR',ihdr)+ch(b'IDAT',idat)+ch(b'IEND',b''))
"
scripts/run-headless.sh ./build/sglua samples/03_texture.lua --capture /tmp/t_edited.png --capture-frame 30
mv /tmp/03_tex.bak samples/data/03_tex.png
cmp /tmp/t_orig.png /tmp/t_edited.png && echo "ERROR: SAME" || echo "OK: different"
```

期待: `OK: different`。

- [ ] **Step 4: pipeline cache sweep stress (long-running)**

`/tmp/test_sweep.lua` で shader を 50 回切り替える:

```lua
local vs = [[ struct VSIn{float3 pos:POSITION;}; struct VSOut{float4 pos:SV_Position;};
[shader("vertex")] VSOut vs_main(VSIn i){VSOut o; o.pos=float4(i.pos,1); return o;} ]]
local function fs_with(r, g, b)
   return string.format([[ [shader("fragment")] float4 fs_main():SV_Target{return float4(%f,%f,%f,1);} ]],
                        r, g, b)
end
local verts = {0,0.5,0, -0.5,-0.5,0, 0.5,-0.5,0}
local frame = 0
function on_init() config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" }) end
function on_event() end function on_quit() end
function on_frame()
   frame = frame + 1
   local r = (frame % 50) / 50.0
   local fs = fs_with(r, 0, 1 - r)
   local s = use_shader("sw", vs, fs, frame)
   local b = use_buffer("sb", VERTEX, verts, 1)
   begin_pass({ target = main_tex, clear_color = {0,0,0,1} })
      draw(3, { verts = b }, { shader = s })
   end_pass()
   if frame >= 60 then os.exit(0) end
end
```

```bash
for be in sokol sdlgpu; do
  SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua /tmp/test_sweep.lua 2>&1 | grep -i "error\|leak\|warn" | head -10
  echo "$be done"
done
```

期待: validation error / leak / unexpected warning なし。**(将来 ASAN ビルドが入った時にここで leak 0 を確認できる)**

- [ ] **Step 5: コミット (検証ログを残すため空コミットは不要、何も変更がなければスキップ)**

特に書き換えがなければスキップ。

---

## Task 18: README 更新

**Files:**
- Modify: `README.md`

- [ ] **Step 1: 「Live edit」セクションを追加**

`README.md` の `## Backend 切替` の後 (または適切な位置) に新セクションを追加:

```markdown
## Live edit (file watching)

サンプルは `samples/data/` 配下の外部ファイルから shader / 頂点データ / テクスチャを読み込む。
起動中にファイルを編集すると次フレームから反映される。

仕組み:
- 各サンプル冒頭で `samples/sg_io.lua` を `dofile` で読み込み、`load_text` /
  `load_floats` / `load_png` を経由してリソースを取得する。
- helper は `path → {mtime, content_hash}` のキャッシュを持ち、毎フレームの
  `stat()` 1 回だけで「変化なし」を判定する。mtime 違い時のみ再読み込みして
  FNV-1a 64 ハッシュを取り、それを `version` として `use_*` に渡す。
- C 側は `version` 違いで in-place update (buffer/texture) または recompile
  (shader) を実施。shader recompile 時は旧 shader を参照する pipeline cache
  entry を sweep してリークを防ぐ。

例: `samples/data/01_triangle.fs.slang` の出力色をエディタで書き換えて保存すると、
起動中の `samples/01_triangle.lua` の三角形の色が即座に変わる。

shader の compile error 時は旧 shader を維持してログを出すのみで、クラッシュせず
エディタで修正→保存すれば復帰する。
```

- [ ] **Step 2: `## 未実装 (将来)` セクションから hot reload 行を削除**

該当行 (`Sample 7: ホットリロード ...`) を消す。「Lua 側からの sampler 設定」等は残す。

- [ ] **Step 3: `## サンプル` の表の説明を必要なら微更新**

(現状「単色オレンジ三角形」等の説明は内容変わらないので変更不要。data ファイル経由になった旨を脚注に書きたい場合は追加)

- [ ] **Step 4: コミット**

```bash
git add README.md
git commit -m "docs: live edit section, drop hot reload from future work"
```

---

## Task 19: 最終全体リグレッション + push 準備

**Files:**
- なし (検証のみ)

- [ ] **Step 1: clean build**

```bash
rm -rf build
cmake -S . -B build
cmake --build build -j 2>&1 | tail -10
```

期待: warning なし、リンク成功。

- [ ] **Step 2: 全 sample (00 系含む) を両 backend で smoke**

```bash
for s in samples/0*.lua; do
  for be in sokol sdlgpu; do
    SGLUA_BACKEND=$be scripts/run-headless.sh ./build/sglua "$s" \
        --capture /tmp/final_${be}_$(basename "$s").png --capture-frame 30 2>&1 | tail -1
    echo "  $be $s exit=$?"
  done
done
```

期待: 全 exit 0、validation error の新規発生なし。

- [ ] **Step 3: 4 sample × 2 backend の capture が byte-identical**

```bash
for s in samples/0[1-4]_*.lua; do
  cmp /tmp/final_sokol_$(basename "$s").png /tmp/final_sdlgpu_$(basename "$s").png \
    && echo "$s: identical" || echo "$s: DIFFER"
done
```

期待: 4/4 identical。

- [ ] **Step 4: git status クリーンを確認**

```bash
git status
```

期待: working tree clean (すべてコミット済み)。

- [ ] **Step 5: push 確認 (ユーザーが OK 出した時のみ)**

ユーザーに最終確認を取り、OK が出れば:

```bash
git push origin master
```

---

## Self-review notes

- **Spec coverage**: spec の各セクション (in-place update / pipeline cache sweep / file helper 3 種 / Lua helper / sample refactor 4 つ / README) は Task 1〜19 のいずれかに対応する。version の int64_t 化は spec 「64-bit content hash」要求から導出して Task 7 で行う。
- **No placeholders**: 既存実装で確認した名前 (`shader_compile`, `ShaderTargetBackend`, `ShaderBlob`, `pip_cache`, `SgBuffer`, `SgImage`, `g_app`) を反映済み。
- **Type consistency**: `BackendBuffer` / `BackendImage` / `BackendShader` / `BackendPipeline` は backend.h の既存型を一貫して使用。`pipeline_cache_invalidate_shader(c, uintptr_t old_shader)` のシグネチャは PipelineKey.shader_handle (uintptr_t) と一致。`ResEntry.version` は Task 7 以降全タスクで int64_t。
- **Frequent commits**: 各 Task 末尾でコミット、計 18 commits (Task 17 は検証のみで commit なし)。
