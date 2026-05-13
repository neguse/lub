# Indexed draw Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- []`) syntax for tracking.

**Goal:** sglua の `draw` に indexed draw 経路を実装し、`use_buffer(INDEX, ...)` を実動作させて両 backend (sokol / sdlgpu) で u32 indexed draw を可能にする。

**Architecture:** API surface は据え置き。`use_buffer` は `type==INDEX` の時のみ Lua 数値 table を `uint32_t` 配列として詰める。`draw(count, resources, options)` は `resources.indices` が INDEX 型 buffer ref なら `bind.ibuf` を設定し、pipeline cache に `is_indexed=true` を渡して indexed pipeline を取得、backend が分岐。`make_buffer` の `data` 型は `void *` に統一。検証は `tests/lua/test_indexed_draw.lua` + lavapipe + xvfb で sokol / sdlgpu 両 backend の golden を 1 枚ずつ固定。

**Tech Stack:** C11 / Lua 5.5 / sokol_gfx (Vulkan) / SDL3 GPU / Slang / lavapipe + xvfb (CI/headless)。spec は `docs/superpowers/specs/2026-05-14-indexed-draw-design.md`。

---

## File Map

| ファイル | 役割 | 変更種別 |
|---|---|---|
| `src/backend.h` | `BindingsDesc` に `ibuf` 追加、`PipelineDesc` に `is_indexed` 追加、`make_buffer` を `void *` 化 | modify |
| `src/pipeline.h` | `PipelineKey` に `is_indexed` 追加、`pipeline_cache_get` シグネチャ拡張 | modify |
| `src/pipeline.c` | key 構築に `is_indexed` を入れる、`PipelineDesc` に伝搬 | modify |
| `src/backend_sokol.c` | `make_buffer` 型変更、`apply_bindings` で index_buffer 設定、`make_pipeline` で `index_type` 設定 | modify |
| `src/backend_sdlgpu.c` | `make_buffer` 型変更、`apply_bindings` で index buffer bind、`draw` を indexed/非 indexed に分岐 | modify |
| `src/lua_api.c` | `l_use_buffer` INDEX 経路を u32 packing に、`l_draw` で `resources.indices` 抽出 → `bind.ibuf` + `is_indexed` を pipeline cache に渡す | modify |
| `tests/lua/test_indexed_draw.lua` | quad を 4 頂点 + 6 index で indexed draw | create |
| `tests/lua/test_indexed_draw.vs.slang` | pos2 を clip space にそのまま渡す最小 vs | create |
| `tests/lua/test_indexed_draw.fs.slang` | 固定色 + uv 風グラデーション fs | create |
| `tests/golden/test_indexed_draw_sokol.png` | sokol backend での golden | create (生成) |
| `tests/golden/test_indexed_draw_sdlgpu.png` | sdlgpu backend での golden | create (生成) |
| `scripts/run-test-golden.sh` | `tests/lua/test_*.lua` を巡回する golden runner | create |
| `README.md` | `use_buffer(INDEX, ...)` と `draw` の `resources.indices` を API 節に記述、`count` の意味を 1 行で明記 | modify |

---

## Task 1: テスト fixture を作成して現状の失敗を確認する

**Files:**
- Create: `tests/lua/test_indexed_draw.lua`
- Create: `tests/lua/test_indexed_draw.vs.slang`
- Create: `tests/lua/test_indexed_draw.fs.slang`
- Create: `scripts/run-test-golden.sh`

このタスクは「失敗するテスト」を先に置くフェーズ。実装無しの状態で run すると golden 不在で MISSING になることを確認する。

- [ ] **Step 1: vs シェーダを作成**

`tests/lua/test_indexed_draw.vs.slang`:
```slang
struct VSIn  { float2 pos : POSITION; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

[shader("vertex")]
VSOut vs_main(VSIn i)
{
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.pos * 0.5 + 0.5;
    return o;
}
```

- [ ] **Step 2: fs シェーダを作成**

`tests/lua/test_indexed_draw.fs.slang`:
```slang
struct FSIn { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target
{
    return float4(i.uv.x, i.uv.y, 0.4, 1.0);
}
```

- [ ] **Step 3: lua テストファイルを作成**

`tests/lua/test_indexed_draw.lua`:
```lua
-- 4 頂点 quad を 6 index で indexed draw する最小テスト。
-- vertex 重複なしで quad を成立させられることが indexed draw の動作確認になる。

local sg_io = dofile("samples/sg_io.lua")

function on_init()
   config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end

function on_frame(t)
   local vs, ver_vs = sg_io.load_text("tests/lua/test_indexed_draw.vs.slang")
   local fs, ver_fs = sg_io.load_text("tests/lua/test_indexed_draw.fs.slang")
   use_shader("sh", vs, fs, ver_vs ~ ver_fs)

   local verts = { -0.6,-0.6,  0.6,-0.6,  0.6,0.6,  -0.6,0.6 }
   use_buffer("vb", VERTEX, verts, 1)
   local indices = { 0,1,2, 0,2,3 }
   use_buffer("ib", INDEX, indices, 1)

   begin_pass({ target = main_tex, clear_color = {0.05, 0.05, 0.1, 1} })
   draw(6, { verts = "vb", indices = "ib" },
        { shader = "sh", depth = false, cull = NONE })
   end_pass()
end
```

- [ ] **Step 4: golden runner script を作成**

`scripts/run-test-golden.sh`:
```bash
#!/usr/bin/env bash
# tests/lua/test_*.lua を巡回して capture + golden cmp する runner.
# scripts/run-golden.sh の test 版。
#
# Usage:
#   scripts/run-test-golden.sh            # check all tests × backends
#   scripts/run-test-golden.sh --update   # regenerate goldens
#   scripts/run-test-golden.sh --test indexed_draw
#   scripts/run-test-golden.sh --backend sokol

set -euo pipefail
cd "$(dirname "$0")/.."

TESTS=(indexed_draw)
BACKENDS=(sokol sdlgpu)
FRAME=30
BINARY=./build/sglua
GOLDEN_DIR=tests/golden

update=0
test_filter=""
backend_filter=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --update)  update=1 ;;
        --test)    test_filter="$2"; shift ;;
        --backend) backend_filter="$2"; shift ;;
        -h|--help)
            sed -n '2,15p' "$0"; exit 0 ;;
        *)
            echo "unknown arg: $1" >&2
            exit 2 ;;
    esac
    shift
done

if [[ ! -x "$BINARY" ]]; then
    echo "binary not built: $BINARY (run: cmake --build build)" >&2
    exit 2
fi

mkdir -p "$GOLDEN_DIR"
tmpdir=$(mktemp -d)
trap 'rm -rf "$tmpdir"' EXIT

pass=0; fail=0; missing=0; updated=0

for t in "${TESTS[@]}"; do
    [[ -n "$test_filter" && "$t" != "$test_filter" ]] && continue
    for backend in "${BACKENDS[@]}"; do
        [[ -n "$backend_filter" && "$backend" != "$backend_filter" ]] && continue

        name="test_${t}"
        out="$tmpdir/${name}_${backend}.png"
        golden="$GOLDEN_DIR/${name}_${backend}.png"

        SGLUA_BACKEND="$backend" scripts/run-headless.sh "$BINARY" \
            "tests/lua/${name}.lua" --capture "$out" --capture-frame "$FRAME" \
            >"$tmpdir/${name}_${backend}.log" 2>&1 || true

        if [[ ! -f "$out" ]]; then
            echo "FAIL ${name} ${backend}: capture not produced (see $tmpdir/${name}_${backend}.log)"
            fail=$((fail + 1)); continue
        fi
        if [[ $update -eq 1 ]]; then
            cp "$out" "$golden"
            echo "UPDATED ${golden}"
            updated=$((updated + 1)); continue
        fi
        if [[ ! -f "$golden" ]]; then
            echo "MISSING ${golden} (run with --update to create)"
            missing=$((missing + 1)); continue
        fi
        if cmp -s "$out" "$golden"; then
            echo "PASS ${name} ${backend}"
            pass=$((pass + 1))
        else
            echo "FAIL ${name} ${backend}: $out != $golden"
            fail=$((fail + 1))
        fi
    done
done

echo "---"
if [[ $update -eq 1 ]]; then echo "updated: $updated"; exit 0; fi
echo "pass: $pass  fail: $fail  missing: $missing"
[[ $fail -eq 0 && $missing -eq 0 ]]
```

- [ ] **Step 5: 実行権限を付与**

```bash
chmod +x scripts/run-test-golden.sh
```

- [ ] **Step 6: ビルドして fixture を確認する**

```bash
cmake --build build -j
scripts/run-test-golden.sh
```

Expected: `MISSING tests/golden/test_indexed_draw_*.png` が両 backend で出る (実装前なので drawing 自体は妙な絵になる可能性があるが capture 自体は走る or run-headless が non-zero exit する。ここでは golden が未作成という事実が見えれば OK)。

- [ ] **Step 7: Commit**

```bash
git add tests/lua/test_indexed_draw.lua tests/lua/test_indexed_draw.vs.slang tests/lua/test_indexed_draw.fs.slang scripts/run-test-golden.sh
git commit -m "test(indexed_draw): add failing test fixture for indexed draw"
```

---

## Task 2: `make_buffer` シグネチャを `void *` 化

**Files:**
- Modify: `src/backend.h:103`
- Modify: `src/backend_sokol.c:527`
- Modify: `src/backend_sdlgpu.c:255`

`float *` → `void *` への型変更。実装は中で受けたバイトを GPU buffer に流すだけなのでロジック変化なし。caller (`src/lua_api.c:244`) は今 `(SglBufferType)type, data, new_bytes` を渡しており、`data` は `float *` 型のローカル。`void *` 化に伴い `data` を `void *` 型に変える (これは後続 Task 4 と統合される)。

- [ ] **Step 1: backend.h の vtable シグネチャを変更**

`src/backend.h` の `make_buffer` 行 (`backend.h:103`):
```c
// 旧
BackendBuffer   (*make_buffer)(SglBufferType type, const float *data, size_t bytes);
// 新
BackendBuffer   (*make_buffer)(SglBufferType type, const void *data, size_t bytes);
```

- [ ] **Step 2: sokol backend の関数シグネチャを変更**

`src/backend_sokol.c:527` の `sk_make_buffer`:
```c
// 旧
static BackendBuffer sk_make_buffer(SglBufferType type, const float *data, size_t bytes) {
// 新
static BackendBuffer sk_make_buffer(SglBufferType type, const void *data, size_t bytes) {
```

中身は変更不要 (`sg_update_buffer(sb->buf, &(sg_range){ .ptr = data, .size = bytes });` などは `const void *` を受ける)。

- [ ] **Step 3: sdlgpu backend の関数シグネチャを変更**

`src/backend_sdlgpu.c:255` の `sg_make_buffer`:
```c
// 旧
static BackendBuffer sg_make_buffer(SglBufferType type, const float *data, size_t bytes) {
// 新
static BackendBuffer sg_make_buffer(SglBufferType type, const void *data, size_t bytes) {
```

中身の `sg_upload_to_buffer(b->gpu, data, bytes, false)` も `const void *` を受ける想定。型のキャストが必要なら呼び出し側の関数シグネチャを `src/backend_sdlgpu.c:178` の `sg_upload_to_buffer` 周辺で確認し合わせる。具体的には `sg_upload_to_buffer` の `data` パラメータが `const void *` なら何もしない、`const float *` なら同様に `const void *` 化する。

- [ ] **Step 4: caller (lua_api.c) 側の data 型を一旦そのまま、コンパイル**

`src/lua_api.c:228` の `float *data` のまま、`g_backend->make_buffer((SglBufferType)type, data, new_bytes)` は `float *` → `const void *` の暗黙変換で通る (warning 出るなら -Wno-...; 通常 C では float* -> void* は OK)。Task 4 で `void *` に揃える。

```bash
cmake --build build -j
```

Expected: ビルド成功、警告なし (もし `-Wpedantic` で float -> void cast 警告が出るなら Task 4 まで一時的に許容、または `(const void *)data` キャストを明示)。

- [ ] **Step 5: 既存 sample で regression が無いことを確認**

```bash
scripts/run-golden.sh --sample 01_triangle
scripts/run-golden.sh --sample 03_texture
```

Expected: `PASS 01_triangle sokol`, `PASS 01_triangle sdlgpu`, `PASS 03_texture sokol`, `PASS 03_texture sdlgpu`。

- [ ] **Step 6: Commit**

```bash
git add src/backend.h src/backend_sokol.c src/backend_sdlgpu.c
git commit -m "refactor(backend): make_buffer takes const void* instead of const float*"
```

---

## Task 3: `BindingsDesc.ibuf` と `PipelineDesc.is_indexed` フィールドを追加

**Files:**
- Modify: `src/backend.h:44` (PipelineDesc), `src/backend.h:66` (BindingsDesc)
- Modify: `src/pipeline.h:10` (PipelineKey), `src/pipeline.h:33` (pipeline_cache_get sig)
- Modify: `src/pipeline.c:36-95` (key 構築 + desc 構築)
- Modify: `src/lua_api.c:659` (pipeline_cache_get 呼び出し)

このタスクでは「フィールドを足すだけ」「全 caller に `false` / `0` を渡すだけ」とする。挙動変化はゼロ。後続タスクで実際に使う。

- [ ] **Step 1: `BindingsDesc` に `ibuf` フィールドを追加**

`src/backend.h:66-74` を以下に変更:
```c
typedef struct BindingsDesc {
    const ShaderReflection *refl; // for resolving texture name -> slot. NULL = skip texture binding.
    BackendBuffer vbuf;           // 0 = none
    BackendBuffer ibuf;           // 0 = none (non-indexed); non-0 = u32 index buffer
    int texture_count;
    struct {
        const char *name;         // matches reflection name
        BackendImage image;
    } textures[8];
} BindingsDesc;
```

- [ ] **Step 2: `PipelineDesc` に `is_indexed` フィールドを追加**

`src/backend.h:44-56` を以下に変更:
```c
typedef struct PipelineDesc {
    BackendShader shader;
    const ShaderReflection *refl;
    SglBlend blend;
    bool depth_test;
    bool depth_write;
    SglCull cull;
    SglPrimitive primitive;
    int n_color_targets;       // 1..SGL_MAX_COLOR_TARGETS
    SglPixelFormat color_fmts[SGL_MAX_COLOR_TARGETS];
    bool has_depth;            // false = offscreen color-only pass
    bool is_indexed;           // true = pipeline used for indexed draw (sokol: index_type = UINT32)
    bool is_compute;           // true: make_pipeline ignores graphics state and builds a compute pipeline
} PipelineDesc;
```

- [ ] **Step 3: `PipelineKey` に `is_indexed` を追加**

`src/pipeline.h:10-17` を以下に変更 (padding を 1 バイト分縮める):
```c
typedef struct PipelineKey {
    uintptr_t shader_handle;
    uint8_t blend, depth_test, depth_write, cull, primitive, has_depth;
    uint8_t n_color_targets;
    uint8_t color_fmts[SGL_MAX_COLOR_TARGETS];
    uint8_t is_compute; // 1 = compute pipeline (all graphics fields are zero)
    uint8_t is_indexed; // 1 = pipeline used for indexed draw
    uint8_t _pad[3];    // memset-zeroed; memcmp would otherwise hit indeterminate padding
} PipelineKey;
```

- [ ] **Step 4: `pipeline_cache_get` シグネチャに `is_indexed` 引数を追加**

`src/pipeline.h:33-40` を以下に変更:
```c
BackendPipeline pipeline_cache_get(
    PipelineCache *c,
    BackendShader sh, const ShaderReflection *refl,
    SglBlend blend, bool depth_test, bool depth_write,
    SglCull cull, SglPrimitive prim,
    int n_color_targets, const SglPixelFormat *color_fmts,
    bool has_depth, bool is_indexed,
    int64_t current_frame);
```

- [ ] **Step 5: `pipeline.c` の関数本体を更新**

`src/pipeline.c:36-96` を以下に置き換え:
```c
BackendPipeline pipeline_cache_get(
    PipelineCache *c, BackendShader sh, const ShaderReflection *refl,
    SglBlend blend, bool dt, bool dw, SglCull cull, SglPrimitive prim,
    int n_color_targets, const SglPixelFormat *cfmts,
    bool has_depth, bool is_indexed, int64_t current_frame)
{
    if (n_color_targets < 1) n_color_targets = 1;
    if (n_color_targets > SGL_MAX_COLOR_TARGETS) n_color_targets = SGL_MAX_COLOR_TARGETS;
    PipelineKey k;
    memset(&k, 0, sizeof(k));
    k.shader_handle = sh;
    k.blend = (uint8_t)blend;
    k.depth_test = dt ? 1 : 0;
    k.depth_write = dw ? 1 : 0;
    k.cull = (uint8_t)cull;
    k.primitive = (uint8_t)prim;
    k.has_depth = has_depth ? 1 : 0;
    k.is_indexed = is_indexed ? 1 : 0;
    k.n_color_targets = (uint8_t)n_color_targets;
    for (int i = 0; i < n_color_targets; ++i) {
        k.color_fmts[i] = (uint8_t)cfmts[i];
    }
    uint32_t bi = hash_key(&k) & (PIPELINE_BUCKETS - 1);
    for (PipelineEntry *e = c->buckets[bi]; e; e = e->next) {
        if (memcmp(&e->key, &k, sizeof(k)) == 0) {
            e->last_seen_frame = current_frame;
            return e->pip;
        }
    }

    PipelineDesc desc = {
        .shader = sh,
        .refl = refl,
        .blend = blend,
        .depth_test = dt,
        .depth_write = dw,
        .cull = cull,
        .primitive = prim,
        .n_color_targets = n_color_targets,
        .has_depth = has_depth,
        .is_indexed = is_indexed,
    };
    for (int i = 0; i < n_color_targets; ++i) {
        desc.color_fmts[i] = cfmts[i];
    }
    BackendPipeline pip = g_backend->make_pipeline(&desc);

    PipelineEntry *e = (PipelineEntry*)calloc(1, sizeof(PipelineEntry));
    if (!e) return pip;
    e->key = k;
    e->pip = pip;
    e->last_seen_frame = current_frame;
    e->next = c->buckets[bi];
    c->buckets[bi] = e;
    return pip;
}
```

- [ ] **Step 6: `l_draw` の `pipeline_cache_get` 呼び出しを更新 (全引数 false 渡し)**

`src/lua_api.c:659-667` を以下に変更 (関数末尾の `current_frame` の前に `false` を追加):
```c
    BackendPipeline pip = pipeline_cache_get(
        &g_app_for_lua->pip_cache,
        sh_e->u.sh.h, &sh_e->u.sh.refl,
        (SglBlend)blend, depth_test, depth_write,
        (SglCull)cull, (SglPrimitive)prim,
        g_app_for_lua->pass.current_n_color_targets,
        g_app_for_lua->pass.current_color_fmts,
        g_app_for_lua->pass.current_has_depth,
        false,                       // is_indexed (Task 7 で動的化)
        (int64_t)g_app_for_lua->frame_index);
```

- [ ] **Step 7: ビルドして既存 sample で regression なしを確認**

```bash
cmake --build build -j
scripts/run-golden.sh
```

Expected: 既存 11 サンプル × 2 backend = 22 件すべて `PASS`。

- [ ] **Step 8: Commit**

```bash
git add src/backend.h src/pipeline.h src/pipeline.c src/lua_api.c
git commit -m "refactor(pipeline): add is_indexed to PipelineDesc/Key and BindingsDesc.ibuf field"
```

---

## Task 4: `l_use_buffer` の INDEX 経路を u32 packing に

**Files:**
- Modify: `src/lua_api.c:193-253`

`type == SGL_BUFFER_INDEX` の時、Lua 数値 table を `uint32_t` 配列として詰める。VERTEX / STORAGE は既存の float パスのまま。`data` の型を `void *` に変更。

- [ ] **Step 1: `l_use_buffer` の data 処理を type 分岐に**

`src/lua_api.c:217-249` を以下に置き換え (ある程度大きいので関数全体を整理):
```c
    // STORAGE may be allocated empty by passing an integer float-count as arg #3
    // (the compute shader populates it). VERTEX/INDEX must always have a data
    // table.
    bool allocate_empty = (type == SGL_BUFFER_STORAGE) && lua_isinteger(L, 3);
    size_t new_bytes = 0;
    void *data = NULL;
    if (allocate_empty) {
        lua_Integer n = lua_tointeger(L, 3);
        if (n <= 0) return luaL_error(L, "use_buffer: STORAGE float-count must be > 0");
        new_bytes = (size_t)n * sizeof(float);
    } else if (type == SGL_BUFFER_INDEX) {
        luaL_checktype(L, 3, LUA_TTABLE);
        int n = (int)lua_rawlen(L, 3);
        if (n <= 0) return luaL_error(L, "use_buffer: empty data");
        uint32_t *idx = (uint32_t*)malloc((size_t)n * sizeof(uint32_t));
        if (!idx) return luaL_error(L, "use_buffer: out of memory");
        for (int i = 0; i < n; ++i) {
            lua_rawgeti(L, 3, i + 1);
            idx[i] = (uint32_t)lua_tonumber(L, -1);
            lua_pop(L, 1);
        }
        new_bytes = (size_t)n * sizeof(uint32_t);
        data = idx;
    } else {
        // VERTEX / STORAGE with data
        luaL_checktype(L, 3, LUA_TTABLE);
        int n = (int)lua_rawlen(L, 3);
        if (n <= 0) return luaL_error(L, "use_buffer: empty data");
        float *fdata = (float*)malloc((size_t)n * sizeof(float));
        if (!fdata) return luaL_error(L, "use_buffer: out of memory");
        for (int i = 0; i < n; ++i) {
            lua_rawgeti(L, 3, i + 1);
            fdata[i] = (float)lua_tonumber(L, -1);
            lua_pop(L, 1);
        }
        new_bytes = (size_t)n * sizeof(float);
        data = fdata;
    }

    if (e->u.buf.h != 0 && e->u.buf.size_bytes == new_bytes &&
        e->u.buf.type == (SglBufferType)type && data) {
        // in-place update
        g_backend->update_buffer(e->u.buf.h, data, new_bytes);
    } else {
        if (e->u.buf.h != 0) g_backend->destroy_buffer(e->u.buf.h);
        e->u.buf.h = g_backend->make_buffer((SglBufferType)type, data, new_bytes);
        e->u.buf.type = (SglBufferType)type;
        e->u.buf.size_bytes = new_bytes;
    }
    e->version = version;
    if (data) free(data);

    push_buffer_ref(L, key);
    return 1;
}
```

ポイント:
- `data` の型を `void *` に変更 (Task 2 で `make_buffer` シグネチャが `const void *` 化済み)
- INDEX type は `uint32_t` 配列を確保、`(uint32_t)lua_tonumber` で truncate
- VERTEX / STORAGE は既存通り `float` 配列
- `g_backend->update_buffer(h, data, bytes)` は元から `const void *` 受け、変更不要

- [ ] **Step 2: ビルドして既存 sample で regression なしを確認**

```bash
cmake --build build -j
scripts/run-golden.sh
```

Expected: 既存 11 サンプル × 2 backend = 22 件すべて `PASS`。

- [ ] **Step 3: INDEX バッファが GPU 側で u32 として確保されることをスモークテスト**

`tests/lua/test_indexed_draw.lua` を一度実行 (まだ draw 経路は indexed 動かないが、`use_buffer(INDEX, ...)` が malloc + upload する経路は走る、メモリエラーや panic が無いことを確認):

```bash
scripts/run-headless.sh ./build/sglua tests/lua/test_indexed_draw.lua --capture /tmp/test_use_buffer_index.png --capture-frame 30 2>&1 | head -50
```

Expected: クラッシュせずに動く。capture PNG はまだ正しい絵にならない (draw 側が indices を読まない)。

- [ ] **Step 4: Commit**

```bash
git add src/lua_api.c
git commit -m "feat(lua_api): use_buffer INDEX path packs u32 from Lua number table"
```

---

## Task 5: sokol backend の indexed pipeline + index_buffer 反映

**Files:**
- Modify: `src/backend_sokol.c:844-911` (sk_make_pipeline)
- Modify: `src/backend_sokol.c:991-1018` (sk_apply_bindings)

sokol path: pipeline の `index_type` を `is_indexed` に応じて `UINT32 / NONE` に分岐、`apply_bindings` で `sg_bindings.index_buffer` を設定。

- [ ] **Step 1: `sk_make_pipeline` で `index_type` を設定**

`src/backend_sokol.c:909` 直前 (`sg_make_pipeline(&desc)` を呼ぶ前) に追加:
```c
    desc.index_type = d->is_indexed ? SG_INDEXTYPE_UINT32 : SG_INDEXTYPE_NONE;
```

- [ ] **Step 2: `sk_apply_bindings` で `index_buffer` をセット**

`src/backend_sokol.c:991-1018` の `sk_apply_bindings` を以下に書き換え:
```c
static void sk_apply_bindings(const BindingsDesc *b) {
    sg_bindings sb = {0};
    if (b->vbuf) {
        SkBuffer *vb = (SkBuffer*)b->vbuf;
        sb.vertex_buffers[0] = vb->buf;
    }
    if (b->ibuf) {
        SkBuffer *ib = (SkBuffer*)b->ibuf;
        sb.index_buffer = ib->buf;
    }

    // Resolve textures via reflection name → slot.
    if (b->refl) {
        for (int i = 0; i < b->texture_count; ++i) {
            const char *name = b->textures[i].name;
            SkImage *si = (SkImage*)b->textures[i].image;
            if (!name || !si) continue;
            for (int k = 0; k < b->refl->tex_count; ++k) {
                if (strcmp(b->refl->texs[k].name, name) == 0) {
                    int img_slot = b->refl->texs[k].img_slot;
                    int smp_slot = b->refl->texs[k].smp_slot;
                    if (img_slot >= 0 && img_slot < SG_MAX_VIEW_BINDSLOTS)
                        sb.views[img_slot] = si->view;
                    if (smp_slot >= 0 && smp_slot < SG_MAX_SAMPLER_BINDSLOTS)
                        sb.samplers[smp_slot] = si->smp;
                    break;
                }
            }
        }
    }
    sg_apply_bindings(&sb);
}
```

`sk_draw` は不変 (`sg_draw(base, count, 1)` が pipeline の index_type で indexed/非 indexed を分岐する)。

- [ ] **Step 3: ビルドして既存 sample で regression なしを確認**

```bash
cmake --build build -j
scripts/run-golden.sh
```

Expected: 既存 11 サンプル × 2 backend = 22 件すべて `PASS`。Task 7 で `l_draw` から `bind.ibuf` をセットするまでは、sokol path に index buffer は流れてこないので挙動変化ゼロ。

- [ ] **Step 4: Commit**

```bash
git add src/backend_sokol.c
git commit -m "feat(backend_sokol): wire index_buffer in apply_bindings and index_type in pipeline"
```

---

## Task 6: sdlgpu backend の indexed bind + draw 分岐

**Files:**
- Modify: `src/backend_sdlgpu.c:616-650` (sg_apply_bindings)
- Modify: `src/backend_sdlgpu.c:660-663` (sg_draw)

sdlgpu path: `apply_bindings` で `SDL_BindGPUIndexBuffer` を呼ぶ。`draw` は内部の `g_last_indexed` flag を見て `SDL_DrawGPUIndexedPrimitives` / `SDL_DrawGPUPrimitives` に分岐。

- [ ] **Step 1: 内部 state に `g_last_indexed` を追加**

`src/backend_sdlgpu.c` のファイル先頭近く (他の `static ... g_*` 変数の隣) に追加。`g_render_pass`, `g_current_pip` などの static globals がある箇所。具体的には `grep -n "^static .*g_" src/backend_sdlgpu.c` で位置を確認し、その近くに以下を追加:
```c
static bool g_last_indexed = false;
```

- [ ] **Step 2: `sg_apply_bindings` で index buffer を bind**

`src/backend_sdlgpu.c:616-650` の `sg_apply_bindings` を以下に書き換え:
```c
static void sg_apply_bindings(const BindingsDesc *b) {
    if (!g_render_pass) return;
    if (b->vbuf) {
        SgBuffer *vb = (SgBuffer*)b->vbuf;
        if (vb && vb->gpu) {
            SDL_BindGPUVertexBuffers(g_render_pass, 0,
                &(SDL_GPUBufferBinding){ .buffer = vb->gpu, .offset = 0 }, 1);
        }
    }
    if (b->ibuf) {
        SgBuffer *ib = (SgBuffer*)b->ibuf;
        if (ib && ib->gpu) {
            SDL_BindGPUIndexBuffer(g_render_pass,
                &(SDL_GPUBufferBinding){ .buffer = ib->gpu, .offset = 0 },
                SDL_GPU_INDEXELEMENTSIZE_32BIT);
            g_last_indexed = true;
        } else {
            g_last_indexed = false;
        }
    } else {
        g_last_indexed = false;
    }
    // Fragment-stage texture+sampler binding: resolve name->slot via reflection,
    // then issue a single SDL_BindGPUFragmentSamplers covering [0..max_slot].
    if (b->texture_count > 0 && b->refl) {
        SDL_GPUTextureSamplerBinding tsb[8] = {0};
        int max_slot = -1;
        for (int i = 0; i < b->texture_count; ++i) {
            if (!b->textures[i].name) continue;
            for (int j = 0; j < b->refl->tex_count; ++j) {
                if (strcmp(b->refl->texs[j].name, b->textures[i].name) != 0) continue;
                SgImage *im = (SgImage*)b->textures[i].image;
                if (!im || !im->tex || !im->smp) break;
                int slot = b->refl->texs[j].smp_slot;
                if (slot < 0 || slot >= 8) break;
                tsb[slot] = (SDL_GPUTextureSamplerBinding){
                    .texture = im->tex,
                    .sampler = im->smp,
                };
                if (slot > max_slot) max_slot = slot;
                break;
            }
        }
        if (max_slot >= 0) {
            SDL_BindGPUFragmentSamplers(g_render_pass, 0, tsb, (Uint32)(max_slot + 1));
        }
    }
}
```

- [ ] **Step 3: `sg_draw` を indexed/非 indexed に分岐**

`src/backend_sdlgpu.c:660-663` の `sg_draw` を以下に書き換え:
```c
static void sg_draw(int base, int count) {
    if (!g_render_pass) return;
    if (g_last_indexed) {
        SDL_DrawGPUIndexedPrimitives(g_render_pass,
            (Uint32)count, 1, (Uint32)base, 0, 0);
    } else {
        SDL_DrawGPUPrimitives(g_render_pass,
            (Uint32)count, 1, (Uint32)base, 0);
    }
}
```

- [ ] **Step 4: ビルドして既存 sample で regression なしを確認**

```bash
cmake --build build -j
scripts/run-golden.sh
```

Expected: 既存 11 サンプル × 2 backend = 22 件すべて `PASS`。Task 7 で `l_draw` から `bind.ibuf` をセットするまで、sdlgpu path に index buffer は流れず、`g_last_indexed` は常に `false` のまま、`SDL_DrawGPUPrimitives` 経路で動く。

- [ ] **Step 5: Commit**

```bash
git add src/backend_sdlgpu.c
git commit -m "feat(backend_sdlgpu): wire SDL_BindGPUIndexBuffer + indexed draw branch"
```

---

## Task 7: `l_draw` で `resources.indices` を抽出して indexed 経路に流す

**Files:**
- Modify: `src/lua_api.c:606-760`

`resources` walk のなかで Lua key が `"indices"` の buffer ref を見つけたら、INDEX 型を確認して `bind.ibuf` に格納、`is_indexed = true` を pipeline cache に渡す。

- [ ] **Step 1: `l_draw` の resources walk に indices 分岐を追加**

`src/lua_api.c:685-697` の "buffer" kind 処理を以下に書き換え:
```c
            if (strcmp(kind_buf, "buffer") == 0) {
                // resources のキー名で分岐: "indices" は index buffer、それ以外は vertex buffer。
                const char *res_name = lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
                lua_getfield(L, -1, "key");
                const char *bk = lua_tostring(L, -1);
                ResEntry *be = bk ? res_table_get(&g_app_for_lua->res, bk) : NULL;
                lua_pop(L, 1);
                if (be && be->kind == RES_BUFFER) {
                    if (res_name && strcmp(res_name, "indices") == 0) {
                        if (be->u.buf.type != SGL_BUFFER_INDEX) {
                            return luaL_error(L,
                                "draw: 'indices' must be an INDEX buffer (got type %d)",
                                (int)be->u.buf.type);
                        }
                        bind.ibuf = be->u.buf.h;
                    } else if (be->u.buf.type == SGL_BUFFER_VERTEX ||
                               be->u.buf.type == SGL_BUFFER_STORAGE) {
                        // STORAGE buffers can also serve as a vertex source — they
                        // are declared with both vertex_buffer + storage_buffer usage
                        // so the same buffer can flow from compute write to draw read.
                        bind.vbuf = be->u.buf.h;
                    }
                }
            } else if (strcmp(kind_buf, "texture") == 0) {
```

- [ ] **Step 2: `pipeline_cache_get` の `is_indexed` 引数を動的に**

`src/lua_api.c:659-668` の pipeline_cache_get 呼び出し部 (Task 3 で `false` 固定だった部分) を、`bind.ibuf != 0` の値に置き換え:

```c
    BackendPipeline pip = pipeline_cache_get(
        &g_app_for_lua->pip_cache,
        sh_e->u.sh.h, &sh_e->u.sh.refl,
        (SglBlend)blend, depth_test, depth_write,
        (SglCull)cull, (SglPrimitive)prim,
        g_app_for_lua->pass.current_n_color_targets,
        g_app_for_lua->pass.current_color_fmts,
        g_app_for_lua->pass.current_has_depth,
        (bind.ibuf != 0),
        (int64_t)g_app_for_lua->frame_index);
    g_backend->apply_pipeline(pip);
```

**注意**: `pipeline_cache_get` は現状 `apply_bindings` より前に呼ばれている。`bind.ibuf` は resources walk の後で確定するので、呼び出し順を入れ替える必要がある。具体的には `src/lua_api.c:670-720` 周辺の流れを以下のように再編:

  1. options を読む (既存)
  2. **resources walk を先に** → `bind.vbuf` と `bind.ibuf` を確定
  3. `pipeline_cache_get(..., (bind.ibuf != 0), ...)` → `apply_pipeline`
  4. `apply_bindings(&bind)`
  5. `apply_uniforms`
  6. `g_backend->draw(0, count)`

実コードでこれを成立させるには、現状の `// pipeline state options` ブロック (`src/lua_api.c:636-668`) の `pipeline_cache_get` 呼び出しを「resources walk が終わってから」へ移動する。

`src/lua_api.c:606-758` (関数全体) を以下の構造で書き直す:

```c
static int l_draw(lua_State *L) {
    if (!pass_state_in_pass(&g_app_for_lua->pass)) {
        return luaL_error(L, "draw: must be called inside begin_pass/end_pass");
    }
    int count = (int)luaL_checkinteger(L, 1);
    luaL_checktype(L, 2, LUA_TTABLE); // resources
    luaL_checktype(L, 3, LUA_TTABLE); // options

    // options.shader is required and must be a ShaderRef
    lua_getfield(L, 3, "shader");
    if (!is_sentinel(L, -1, "shader")) {
        lua_pop(L, 1);
        return luaL_error(L, "draw: options.shader required (ShaderRef)");
    }
    lua_getfield(L, -1, "key");
    const char *shader_key = lua_tostring(L, -1);
    char shader_key_buf[128];
    if (shader_key) {
        strncpy(shader_key_buf, shader_key, sizeof(shader_key_buf) - 1);
        shader_key_buf[sizeof(shader_key_buf) - 1] = '\0';
    } else {
        shader_key_buf[0] = '\0';
    }
    lua_pop(L, 2); // pop "key" string and the shader ref

    ResEntry *sh_e = res_table_get(&g_app_for_lua->res, shader_key_buf);
    if (!sh_e || sh_e->kind != RES_SHADER) {
        return luaL_error(L, "draw: shader not found: %s", shader_key_buf);
    }

    // pipeline state options (with defaults)
    int blend = SGL_BLEND_NONE;
    int cull  = SGL_CULL_BACK;
    int prim  = SGL_PRIM_TRIANGLES;
    bool depth_test = true;
    bool depth_write = true;

    lua_getfield(L, 3, "blend");
    if (lua_isinteger(L, -1)) blend = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "cull");
    if (lua_isinteger(L, -1)) cull = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "primitive");
    if (lua_isinteger(L, -1)) prim = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "depth");
    if (!lua_isnoneornil(L, -1)) depth_test = lua_toboolean(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "depth_write");
    if (!lua_isnoneornil(L, -1)) depth_write = lua_toboolean(L, -1);
    lua_pop(L, 1);

    // bindings: walk resources table FIRST so we know whether the draw is
    // indexed (bind.ibuf != 0) before picking a pipeline.
    BindingsDesc bind = {0};
    bind.refl = &sh_e->u.sh.refl;

    lua_pushnil(L);
    while (lua_next(L, 2) != 0) {
        // stack: -2 = key, -1 = value
        if (lua_istable(L, -1)) {
            lua_getfield(L, -1, "__sgl_kind");
            const char *kind = lua_isstring(L, -1) ? lua_tostring(L, -1) : "";
            char kind_buf[16];
            strncpy(kind_buf, kind, sizeof(kind_buf) - 1);
            kind_buf[sizeof(kind_buf) - 1] = '\0';
            lua_pop(L, 1);

            if (strcmp(kind_buf, "buffer") == 0) {
                const char *res_name = lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
                lua_getfield(L, -1, "key");
                const char *bk = lua_tostring(L, -1);
                ResEntry *be = bk ? res_table_get(&g_app_for_lua->res, bk) : NULL;
                lua_pop(L, 1);
                if (be && be->kind == RES_BUFFER) {
                    if (res_name && strcmp(res_name, "indices") == 0) {
                        if (be->u.buf.type != SGL_BUFFER_INDEX) {
                            return luaL_error(L,
                                "draw: 'indices' must be an INDEX buffer (got type %d)",
                                (int)be->u.buf.type);
                        }
                        bind.ibuf = be->u.buf.h;
                    } else if (be->u.buf.type == SGL_BUFFER_VERTEX ||
                               be->u.buf.type == SGL_BUFFER_STORAGE) {
                        bind.vbuf = be->u.buf.h;
                    }
                }
            } else if (strcmp(kind_buf, "texture") == 0) {
                const char *res_name = lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
                lua_getfield(L, -1, "key");
                const char *tk = lua_tostring(L, -1);
                ResEntry *te = tk ? res_table_get(&g_app_for_lua->res, tk) : NULL;
                lua_pop(L, 1);
                if (te && te->kind == RES_TEXTURE && res_name &&
                    bind.texture_count < (int)(sizeof(bind.textures)/sizeof(bind.textures[0])))
                {
                    bind.textures[bind.texture_count].name = res_name;
                    bind.textures[bind.texture_count].image = te->u.tex.h;
                    bind.texture_count++;
                }
            }
        }
        lua_pop(L, 1); // value, key stays for lua_next
    }

    // pipeline lookup (after bindings walk so we know is_indexed)
    BackendPipeline pip = pipeline_cache_get(
        &g_app_for_lua->pip_cache,
        sh_e->u.sh.h, &sh_e->u.sh.refl,
        (SglBlend)blend, depth_test, depth_write,
        (SglCull)cull, (SglPrimitive)prim,
        g_app_for_lua->pass.current_n_color_targets,
        g_app_for_lua->pass.current_color_fmts,
        g_app_for_lua->pass.current_has_depth,
        (bind.ibuf != 0),
        (int64_t)g_app_for_lua->frame_index);
    g_backend->apply_pipeline(pip);
    g_backend->apply_bindings(&bind);

    // uniforms: read resources.uniforms = { ub_member_name = {floats...} } and pack
    // into the shader's first uniform block. PoC: only ub[0] supported.
    lua_getfield(L, 2, "uniforms");
    if (lua_istable(L, -1) && sh_e->u.sh.refl.ub_count > 0) {
        const ShaderUniformBlock *ub = &sh_e->u.sh.refl.ubs[0];
        int total_floats = ub->size_floats;
        if (total_floats < 0) total_floats = 0;
        enum { UB_MAX_FLOATS = 256 };
        float buf[UB_MAX_FLOATS];
        memset(buf, 0, sizeof(buf));
        if (total_floats > UB_MAX_FLOATS) {
            return luaL_error(L, "draw: uniform block too large (%d floats > %d)",
                              total_floats, UB_MAX_FLOATS);
        }
        for (int m = 0; m < ub->member_count; ++m) {
            const ShaderUniformMember *mem = &ub->members[m];
            lua_getfield(L, -1, mem->name);
            if (lua_istable(L, -1)) {
                int n_provided = (int)lua_rawlen(L, -1);
                int copy = n_provided < mem->comp_count ? n_provided : mem->comp_count;
                for (int j = 0; j < copy; ++j) {
                    lua_rawgeti(L, -1, j + 1);
                    if (lua_isnumber(L, -1)) {
                        buf[mem->offset_floats + j] = (float)lua_tonumber(L, -1);
                    }
                    lua_pop(L, 1);
                }
            }
            lua_pop(L, 1); // pop the field (or nil)
        }
        g_backend->apply_uniforms(ub->slot, buf,
                                  (size_t)total_floats * sizeof(float));
    }
    lua_pop(L, 1); // pop "uniforms" field (or nil)

    g_backend->draw(0, count);
    return 0;
}
```

- [ ] **Step 2: ビルドして既存 sample で regression なしを確認**

```bash
cmake --build build -j
scripts/run-golden.sh
```

Expected: 既存 11 サンプル × 2 backend = 22 件すべて `PASS`。indices を渡していない sample は `bind.ibuf == 0` で従来通り。

- [ ] **Step 3: indexed draw のテスト fixture を走らせて視覚確認**

```bash
scripts/run-headless.sh ./build/sglua tests/lua/test_indexed_draw.lua --capture /tmp/idx.png --capture-frame 30
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua tests/lua/test_indexed_draw.lua --capture /tmp/idx_sdl.png --capture-frame 30
```

Expected: `/tmp/idx.png` と `/tmp/idx_sdl.png` が両方とも生成され、quad (4 角形領域) に uv ベースのグラデーション (左下が黒、右上が黄、青みあり) が描かれている。視覚的に同じであれば OK。

- [ ] **Step 4: 両 backend で capture が byte-identical か確認**

```bash
cmp /tmp/idx.png /tmp/idx_sdl.png && echo "BYTE-IDENTICAL"
```

Expected: `BYTE-IDENTICAL` が出る (lavapipe + xvfb + capture-frame 固定での決定性により、両 backend が同じ PNG を出す)。

- [ ] **Step 5: Commit**

```bash
git add src/lua_api.c
git commit -m "feat(lua_api): draw reads resources.indices and routes through indexed pipeline"
```

---

## Task 8: golden 画像を生成して固定する

**Files:**
- Create: `tests/golden/test_indexed_draw_sokol.png`
- Create: `tests/golden/test_indexed_draw_sdlgpu.png`

実装が動いた状態で capture を撮り、それを golden として commit する。以後 `run-test-golden.sh` がこれを基準に regression を検出する。

- [ ] **Step 1: golden を生成**

```bash
scripts/run-test-golden.sh --update
```

Expected:
```
UPDATED tests/golden/test_indexed_draw_sokol.png
UPDATED tests/golden/test_indexed_draw_sdlgpu.png
updated: 2
```

- [ ] **Step 2: 通常モードで PASS を確認**

```bash
scripts/run-test-golden.sh
```

Expected:
```
PASS test_indexed_draw sokol
PASS test_indexed_draw sdlgpu
pass: 2  fail: 0  missing: 0
```

- [ ] **Step 3: golden 画像を視覚確認 (人間チェック)**

```bash
file tests/golden/test_indexed_draw_sokol.png
file tests/golden/test_indexed_draw_sdlgpu.png
```

Expected: 両方とも `PNG image data, 1280 x 720` (sglua のデフォルト window サイズ) と表示される。サイズが大きすぎ / 小さすぎなら capture 設定の問題。

可能なら任意のビューアで PNG を開いて、quad に uv グラデーション (左下が黒〜紫、右上が黄〜緑寄り) が描かれていることを確認。背景は `clear_color = {0.05, 0.05, 0.1, 1}` の濃い紺。

- [ ] **Step 4: Commit**

```bash
git add tests/golden/test_indexed_draw_sokol.png tests/golden/test_indexed_draw_sdlgpu.png
git commit -m "test(indexed_draw): commit golden images for sokol/sdlgpu backends"
```

---

## Task 9: README に API ドキュメント追加

**Files:**
- Modify: `README.md`

`use_buffer` の INDEX 経路と `draw` の `resources.indices`、`count` の意味の説明を追記する。

- [ ] **Step 1: `use_buffer` 説明を更新**

`README.md` の API 節、`use_buffer` の項を以下に書き換える (該当箇所は `## API` 配下の最初の bullet)。現状:
```
- `use_buffer(key, type, data, version)` — GPU buffer 宣言。`type` は `VERTEX` / `INDEX` / `STORAGE`。`data` は float の Lua table。`STORAGE` の場合は ...
```

新しい記述:
```
- `use_buffer(key, type, data, version)` — GPU buffer 宣言。`type` は `VERTEX` / `INDEX` / `STORAGE`。
  - `VERTEX` / `STORAGE`: `data` は float の Lua table。`STORAGE` で `data` の代わりに float 数 (integer) を渡すと中身未初期化で割り当てる (compute shader が後で埋める前提)。`STORAGE` は VBO 兼用で作られるので、compute が書き出したバッファをそのまま draw の `verts` に渡せる。
  - `INDEX`: `data` は Lua 数値 table。`(uint32_t)lua_tonumber` で truncate して u32 配列として GPU に格納する。小数/負値はそのまま truncate / wrap される (caller responsibility)。`draw` の `resources.indices` に渡す indexed buffer はこれで作る。
  - 同 `version` なら再アップロードしない。
```

- [ ] **Step 2: `draw` 説明を更新**

`README.md` の `draw(count, resources, options)` の項を以下に書き換える。現状:
```
- `draw(count, resources, options)` — 描画コマンド。
  - `resources` は名前付き table: `{ verts = bufferRef, diffuse = textureRef, uniforms = { mvp = {...floats} } }`。テクスチャの名前はシェーダ側のリフレクションに突き合わせる。uniform は uniform block の最初のものに pack される。
  - `options` は `{ shader = shaderRef, blend, depth, depth_write, cull, primitive }`。`shader` だけ必須。
```

新しい記述:
```
- `draw(count, resources, options)` — 描画コマンド。
  - `count` はプリミティブカウント: `resources.indices` があれば index 数、無ければ vertex 数。
  - `resources` は名前付き table: `{ verts = bufferRef, indices = bufferRef, diffuse = textureRef, uniforms = { mvp = {...floats} } }`。
    - `verts` は VERTEX または STORAGE 型 buffer。
    - `indices` は任意。INDEX 型 buffer を渡すと u32 indexed draw に切り替わる (`SGL_BUFFER_INDEX` 以外を渡すと Lua エラー)。
    - テクスチャの名前はシェーダ側のリフレクションに突き合わせる。
    - uniform は uniform block の最初のものに pack される。
  - `options` は `{ shader = shaderRef, blend, depth, depth_write, cull, primitive }`。`shader` だけ必須。
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs(api): document use_buffer INDEX path and draw resources.indices"
```

---

## Final verification

実装完了後、全テストを通す:

- [ ] **Step 1: 既存 sample 群が regression なしで通る**

```bash
scripts/run-golden.sh
```

Expected: `pass: 22  fail: 0  missing: 0`

- [ ] **Step 2: 新しい indexed draw テストが通る**

```bash
scripts/run-test-golden.sh
```

Expected: `pass: 2  fail: 0  missing: 0`

- [ ] **Step 3: コミット履歴を確認**

```bash
git log --oneline master..HEAD
```

Expected (9 タスク分の commit):
```
docs(api): document use_buffer INDEX path and draw resources.indices
test(indexed_draw): commit golden images for sokol/sdlgpu backends
feat(lua_api): draw reads resources.indices and routes through indexed pipeline
feat(backend_sdlgpu): wire SDL_BindGPUIndexBuffer + indexed draw branch
feat(backend_sokol): wire index_buffer in apply_bindings and index_type in pipeline
feat(lua_api): use_buffer INDEX path packs u32 from Lua number table
refactor(pipeline): add is_indexed to PipelineDesc/Key and BindingsDesc.ibuf field
refactor(backend): make_buffer takes const void* instead of const float*
test(indexed_draw): add failing test fixture for indexed draw
```

実装完了。spec の全要件 (`use_buffer` INDEX u32 path / `draw` indices 分岐 / 両 backend の indexed draw / golden 固定 / README 更新) がカバーされている。
