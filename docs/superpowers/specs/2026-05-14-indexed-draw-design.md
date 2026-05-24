# 2026-05-14 — Indexed draw 対応 設計

## ゴール

lub の描画経路に **indexed draw** を追加する。`use_buffer` の INDEX 型を実際に動作するパスにし、`draw` を indices 有無で内部分岐させる。両 backend (sokol / sdlgpu) で動作。golden 比較で決定的に固定。glTF メッシュ取扱いを始めとする後続作業の前提インフラ。

## 非ゴール

- u16 index サポート — u32 一本。後で sub-type 追加できる余地は残すが今回は実装しない。
- instancing。
- `draw_indexed` のような別 Lua 関数の追加。`draw` に統一する (`use_buffer` の type 引数で内部分岐するのと同じ流儀)。
- 既存 sample (01〜07) を indexed に書き換える — 既存は非 indexed のまま維持、regression を出さない。
- web playground 側のテスト追加 — indexed draw 単独では sample が無いため (golden は native のみ)。WGPU 経路の検証は後続の glTF サンプル (`08_gltf`) で行う。

## Lua API

### `use_buffer(key, type, data, version)` (既存、INDEX 経路を実動作化)

```lua
use_buffer("ibuf", INDEX, {0,1,2, 1,2,3}, version)
```

- `type == INDEX` の時、`data` は Lua 数値 table。要素を `(uint32_t)lua_tonumber(...)` で truncate して u32 buffer に詰める。
- 0.5 や -1 を渡しても弾かない (silent truncate / wrap)。caller responsibility。
- VERTEX / STORAGE 経路は不変 (float)。
- 既存 enum `SGL_BUFFER_INDEX` の allow list はそのまま (既に許可されている)。

### `draw(count, resources, options)` (既存、indices 受け入れ)

```lua
draw(36, {
   verts = vb_ref,
   indices = ib_ref,           -- 任意。INDEX 型 buffer ref。あれば indexed draw に切替
   uniforms = { mvp = {...} },
}, { shader = sh_ref, depth = true, depth_write = true, cull = "BACK" })
```

- `resources.indices` 不在: 従来通り非 indexed。`count` は vertex 数。
- `resources.indices` あり (INDEX 型 BufferRef): indexed draw。`count` は index 数。
- 「`count` はプリミティブカウント = `indices` があれば index 数、無ければ vertex 数」を README に明記。
- `resources.indices` が VERTEX/STORAGE 型の buffer を指していたら `luaL_error`。

## C 側

### `BindingsDesc` 拡張 (`src/backend.h`)

```c
typedef struct BindingsDesc {
    const ShaderReflection *refl;
    BackendBuffer vbuf;
    BackendBuffer ibuf;      // 0 = 非 indexed
    int texture_count;
    struct {
        const char *name;
        BackendImage image;
    } textures[8];
} BindingsDesc;
```

`ibuf == 0` で非 indexed、非 0 で indexed。type は常に u32 (PoC 確定)。

### `RenderBackend.draw` シグネチャ (不変)

```c
void (*draw)(int base, int count);
```

`draw_indexed` 等の追加メソッドは設けない。backend 実装は直前の `apply_bindings` で受けた `bind.ibuf` の有無を内部状態として保持し、`draw` 内で分岐する。

### `make_buffer` を `void *` 化 (`src/backend.h` + 両 backend)

```c
// 旧
BackendBuffer (*make_buffer)(SglBufferType type, const float *data, size_t bytes);
// 新
BackendBuffer (*make_buffer)(SglBufferType type, const void *data, size_t bytes);
```

- 両 backend の実装は受けたバイト列をそのまま GPU buffer に転送しているはずで、`float *` → `void *` への変更は呼び出し側のキャスト 1 つ消すだけ。
- VERTEX / STORAGE buffer の意味論は不変。

### Pipeline cache key 拡張 (`src/pipeline.{h,c}`)

`pipeline_cache_get` のシグネチャに `bool is_indexed` を追加。sokol path で `sg_pipeline_desc.index_type` を `SG_INDEXTYPE_NONE / UINT32` のどちらにするか決める用。

- ハッシュ計算に 1 bit 追加。同じ shader + 同じ blend/cull/depth の pipeline でも、indexed と非 indexed で 2 つキャッシュされる。
- sdlgpu は index type を pipeline ではなく bind 時に指定する API なので、`is_indexed` を受けても無視しても良い (キーには含めて構わない、メモリ上 2 entry に分かれるが PoC スコープで影響ゼロ)。

### `l_draw` (`src/lua_api.c`) 改修フロー

```
1. options を読む (既存)
2. resources を walk:
   - kind == "buffer" の key == "verts" 相当 → bind.vbuf
   - kind == "buffer" の key == "indices" → INDEX 型確認後 bind.ibuf
       INDEX 以外なら luaL_error
   - kind == "texture" → bind.textures[]
3. is_indexed = (bind.ibuf != 0)
4. pipeline = pipeline_cache_get(..., is_indexed, ...)
5. apply_pipeline(pipeline)
6. apply_bindings(&bind)
7. apply_uniforms(resources.uniforms)
8. backend->draw(0, count)
```

resources walk のロジックは既存とほぼ同じで、「indices という名前で渡された buffer ref」のみ追加処理。

### `use_buffer` INDEX 経路実装 (`src/lua_api.c`)

```c
if (type == SGL_BUFFER_INDEX && !allocate_empty) {
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
    data = idx;
    new_bytes = (size_t)n * sizeof(uint32_t);
}
```

VERTEX / STORAGE は既存パスそのまま。`data` 型を `void *` に統一すれば free も共通でいける (`if (data) free(data);`)。

## Backend 実装

### sokol (`src/backend_sokol.c`)

- `make_buffer`: `float *` → `void *` の引数変更を反映。中身は今 `sg_make_buffer({ .data = { .ptr = data, .size = bytes }, ... })` だろうから、ポインタ受けるだけ。
- `apply_bindings`: `sg_bindings.index_buffer` を `bind->ibuf` から (handle 1:1 変換で) 設定。`bind->ibuf == 0` の時は 0 を入れる (sokol は index_buffer == 0 を「無し」と扱う)。
- `make_pipeline`: `desc->is_indexed ? SG_INDEXTYPE_UINT32 : SG_INDEXTYPE_NONE` を `sg_pipeline_desc.index_type` に設定。
- `draw`: `sg_draw(base, count, 1)` 単一呼び出しで indexed / 非 indexed どちらでも動く (pipeline の index_type で sokol が判断)。

### sdlgpu (`src/backend_sdlgpu.c`)

- `make_buffer`: 同様に `void *` 化。
- `apply_bindings`: 既存 `vbuf` バインド処理に並べて、`bind->ibuf != 0` なら `SDL_BindGPUIndexBuffer(pass, &(SDL_GPUBufferBinding){ .buffer = ibuf, .offset = 0 }, SDL_GPU_INDEXELEMENTSIZE_32BIT)` を呼ぶ。
- `make_pipeline`: SDL_GPU の pipeline 自体は index type を持たない (bind 時指定) ので変更不要。`is_indexed` フラグは受けて無視。
- `draw`: 内部で「直前の bindings に ibuf があったか」を持ち、`SDL_DrawGPUPrimitives` / `SDL_DrawGPUIndexedPrimitives` に分岐。
  - 実装方式は backend 内部に static / context 状態を持つか、`apply_bindings` 時に `last_indexed` flag をセットする。後者推奨 (apply_bindings → apply_pipeline → draw の流れで生存期間が明確)。

## テスト

### 検証用最小サンプル (新規)

```
tests/lua/test_indexed_draw.lua       # 4 頂点 + 6 index で quad を indexed draw
tests/golden/test_indexed_draw_sokol.png
tests/golden/test_indexed_draw_sdlgpu.png
```

- 既存 samples ディレクトリは汚さない (samples は「低レベル API のデモ」という意図を保つため)。`tests/lua/` 配下に置く。
- shader は最小: vs で頂点座標そのまま (clip space)、fs で固定色 (vertex id を gradient で振ってもいいが、indexed draw の動作確認だけが目的なので fixed color)。

```lua
-- tests/lua/test_indexed_draw.lua (要点)
local lub_io = dofile("samples/lub_io.lua")

function on_init()
   config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function on_frame()
   local vs = lub_io.load_text("tests/lua/test_indexed_draw.vs.slang")
   local fs = lub_io.load_text("tests/lua/test_indexed_draw.fs.slang")
   use_shader("sh", vs, fs, 1)

   -- 4 corner verts of a quad
   use_buffer("vb", VERTEX, { -0.5,-0.5, 0.5,-0.5, 0.5,0.5, -0.5,0.5 }, 1)
   -- 6 indices = 2 triangles
   use_buffer("ib", INDEX,  { 0,1,2, 0,2,3 }, 1)

   begin_pass({ target = main_tex, clear_color = {0.05, 0.05, 0.05, 1} })
   draw(6, { verts = "vb", indices = "ib" }, { shader = "sh", depth = false })
   end_pass()
end
```

shader ペアは最小の slang (pos2 → clip pos、固定色出力)。

### Golden 駆動

- `scripts/run-golden.sh` の SAMPLES リストは samples 限定なので、別 script を立てるか、既存 script を拡張するかの選択。
- 推奨: 既存 `scripts/run-golden.sh` に `--test` 系の追加路を作るより、**専用 script を 1 個**追加:

```
scripts/run-test-golden.sh        # tests/lua/test_*.lua を巡回して capture + cmp
tests/golden/                     # 既存ディレクトリを共用
```

- 中身は run-golden.sh から流用、`SAMPLES=` 部分を `TESTS=(test_indexed_draw)` に、入力 lua パスを `tests/lua/${test}.lua` に差し替えるだけ。
- どちらも `scripts/run-headless.sh` を経由してヘッドレス capture するのは同じ。

### lavapipe + xvfb での決定性

- 既存 sample 群と同じく lavapipe ICD 強制 + capture-frame 30。両 backend で byte-identical を期待。

## 影響範囲

| ファイル | 変更内容 |
|---|---|
| `src/backend.h` | `BindingsDesc.ibuf` 追加。`make_buffer` を `void *` 化。`PipelineDesc.is_indexed` 追加 (sokol 用) |
| `src/backend_sokol.c` | `make_buffer` 型変更を反映。`apply_bindings` で index_buffer 設定。`make_pipeline` で `index_type` 設定。`draw` は不変 |
| `src/backend_sdlgpu.c` | `make_buffer` 型変更を反映。`apply_bindings` で `SDL_BindGPUIndexBuffer`。`draw` で indexed/非 indexed 分岐 |
| `src/pipeline.h`, `src/pipeline.c` | `pipeline_cache_get` の引数と key に `is_indexed` 追加 |
| `src/lua_api.c` | `l_use_buffer` INDEX 経路を u32 packing。`l_draw` で `resources.indices` を `bind.ibuf` に流し、`is_indexed` を pipeline cache に渡す |
| `tests/lua/test_indexed_draw.lua` (新規) | 検証用 lua |
| `tests/lua/test_indexed_draw.vs.slang` (新規) | 最小 vs |
| `tests/lua/test_indexed_draw.fs.slang` (新規) | 最小 fs (固定色) |
| `tests/golden/test_indexed_draw_sokol.png` (新規) | golden |
| `tests/golden/test_indexed_draw_sdlgpu.png` (新規) | golden |
| `scripts/run-test-golden.sh` (新規) | tests/lua 配下を巡回する golden runner |
| `README.md` | `use_buffer` の INDEX 経路と `draw` の `resources.indices` を API ドキュメントに追記、count 意味の説明を一文追加 |

## エラーハンドリング

| 状況 | 挙動 |
|------|------|
| `use_buffer(INDEX, ..., empty table)` | `luaL_error("use_buffer: empty data")` (既存と同じ) |
| `use_buffer(INDEX, ..., non-number element)` | `lua_tonumber` が 0 を返すため silent 0 詰め。validation 強化はやらない (PoC、caller 責任) |
| `draw(count, { indices = vertex_buffer_ref }, ...)` | INDEX 型でない buffer を indices として渡したら `luaL_error` |
| `draw(count, { indices = bad_key }, ...)` | 存在しない key の BufferRef なら既存の resource 解決失敗エラーに合流 |
| backend draw 失敗 | 既存と同じく backend ログ + 非致命 |

## Out of scope (明示)

- u16 index buffer (`INDEX16` enum 等の sub-type)
- 動的な index type 切替 (同じ key で u16 → u32 を入れ替える)
- instancing (`SDL_DrawGPUPrimitives` の instance count パラメータの活用)
- 既存 sample の indexed 化
- web playground の sample 巡回への追加 (sample 自体が無いため)
- 性能 (数百万 index でのバッチ最適化)

## オープン項目 (実装時に確認)

1. sokol_gfx の `sg_pipeline_desc.index_type` を変更すると compute pipeline 周りに副作用が無いか — `pipeline.c` の compute 経路 (`is_compute = true`) でこのフィールドが無視されるかチェック (おそらく問題ない)。
2. sdlgpu backend の現在の `apply_bindings` で internal state を持つ取り回しが既にどうなっているか — backend 内 static or App-attached のどちらに `last_indexed` flag を置くか実装時に決める。
3. `tests/lua/` を新ディレクトリとして掘る判断 — 既存 `tests/golden/` の隣に `tests/lua/` で素直に共存できるか確認 (テストハーネス側の前提を見る)。
