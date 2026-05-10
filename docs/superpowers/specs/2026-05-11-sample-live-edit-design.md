# Sample Live Edit (file-input + in-place update) Design

**Date:** 2026-05-11
**Status:** Approved (brainstorming)

## 目的

全サンプルを「外部ファイルからリソースを読み込み、エディタで編集すると次フレームから反映される」live-edit 体験に統一する。`use_buffer` / `use_texture` / `use_shader` の `version` 引数を起点に、in-place update を実装する。

## スコープ

- 既存サンプル `samples/01_triangle.lua` 〜 `samples/04_mvp.lua` を、ファイル入力 helper 経由でリソースを読むように refactor する。新規サンプルは追加しない。
- `samples/00_hello.lua` / `00b_clear.lua` / `00c_buffer.lua` / `00d_shader.lua` は段階確認用で、現状維持。
- 両 backend (sokol / sdlgpu) で動作。capture との互換性を維持し、両 backend の capture PNG が引き続き byte-identical であること。
- pipeline cache の旧 shader entry を sweep する。
- version は 64-bit content hash (FNV-1a) を採用し、mtime はキャッシュ無効化のヒントとしてのみ使用する。

## 非スコープ

- 新規サンプル (5/6/7) の追加 — 既存 4 つで live-edit のショーケースは充足する。
- file watching の C 側常駐機構 (inotify / poll) — Lua 側の per-frame stat で十分。
- shader compile エラー時の前回バイナリ保持の追加実装 — 既に旧 handle を維持する経路があれば維持する範囲とする。
- macOS / Windows 対応 — 既に PoC 全体のスコープ外。

## アーキテクチャ概要

### in-place update の意味論

| API           | version 同 | version 違い + 形状同 | version 違い + 形状違い  |
|---------------|------------|------------------------|----------------------------|
| `use_buffer`  | スキップ   | `update_buffer`        | `destroy + make` (現行通り) |
| `use_texture` | スキップ   | `update_image`         | `destroy + make` (現行通り) |
| `use_shader`  | スキップ   | (形状概念なし。version 違いなら常に) recompile + handle 差し替え + 旧 shader を参照する pipeline cache を sweep + 旧 shader destroy | — |

「形状」: buffer はバイト数、texture は (w, h, format)。shader は形状チェック不要 (recompile が常に新 handle を生む)。

### RenderBackend vtable 拡張

`src/backend.h` の `RenderBackend` に以下を追加する:

```c
void (*update_buffer)(BufferHandle h, const void *data, size_t size);
void (*update_image)(ImageHandle h, const void *data, size_t size);
```

shader update は新メソッド不要 (既存 `make_shader` を再利用し、resources entry の handle を差し替える)。

### sokol backend (`backend_sokol.c`)

- `make_buffer` / `make_image` を `SG_USAGE_DYNAMIC` で作成する経路に変更し、create 直後に `sg_update_buffer` / `sg_update_image` で初期データを流し込む。現行は `IMMUTABLE` + initial data。
- `update_buffer` は `sg_update_buffer(buf, &range)`、`update_image` は `sg_update_image(img, &data)` で実装する。

### sdlgpu backend (`backend_sdlgpu.c`)

- 既存の transfer-buffer ベースの upload 経路をそのまま再利用する関数として切り出す (e.g., `upload_buffer_data` / `upload_image_data` の内部 helper)。
- `update_buffer` / `update_image` はその内部 helper を `cycle = true` で呼ぶ (in-flight GPU 使用との衝突を避ける)。

### shader recompile + pipeline cache sweep

`l_use_shader` (`src/lua_api.c`) は version 違いを検出すると、

1. 新しい source で `make_shader` → 失敗時は旧 handle を維持し、エラーログ + `e->version` を据え置き、何もせずリターン (次回 version 違いで再試行)。
2. 成功時、旧 `e->u.sh.h` を保存して `pipeline_cache_invalidate_shader(&app->pipeline_cache, old_handle)` で旧 shader を参照する pipeline entry を全て destroy + 解放。
3. 旧 shader を `destroy_shader(old_handle)` で破棄。
4. `e->u.sh.h` を新 handle に更新。`e->version` を更新。

`pipeline.h/.c` に追加する関数:

```c
void pipeline_cache_invalidate_shader(PipelineCache *c, uintptr_t old_shader);
```

全バケットを walk し、`entry->key.shader_handle == old_shader` の entry について `g_backend->destroy_pipeline(entry->pip)` を呼び、リンクから外して `free` する。

## File-input helper

### C 側 primitives (Lua API)

| 関数                | 戻り値                                         | 備考                                         |
|---------------------|------------------------------------------------|----------------------------------------------|
| `file_mtime(path)`  | `integer` (ns) または `nil`                    | `clock_gettime` 互換、`stat.st_mtim` を `tv_sec * 1e9 + tv_nsec` に combined |
| `load_png(path)`    | `(table_bytes, w, h, fmt)` または `nil`        | stb_image で `desired_channels = 4` でデコード → fmt = `RGBA8` 固定。R8 等が必要になったら将来 `channels` 引数を追加。 |
| `fnv1a64(s)`        | `integer`                                      | 任意バイト列の FNV-1a 64-bit ハッシュ        |

`stb_image.h` を `third_party/stb/` に vendor 追加し、`src/stb_impl.c` で `STB_IMAGE_IMPLEMENTATION` を定義 (既存 `STB_IMAGE_WRITE_IMPLEMENTATION` と同 TU で OK)。

### Lua 側 helper モジュール (`samples/sg_io.lua`)

各サンプルが `local sg_io = dofile("samples/sg_io.lua")` で読み込む単一モジュール。内部キャッシュ `cache[path] = { mtime, bytes, hash, parsed }` を持つ。

```lua
function sg_io.load_text(path)
   -- 1) stat → mtime
   -- 2) cache[path].mtime == mtime ? → return cached.parsed (= bytes), cached.hash
   -- 3) re-read bytes → fnv1a64
   --    hash 同じ → mtime のみ更新、return cached.parsed, cached.hash
   --    hash 違う → cache 全部更新、return new bytes, new hash

function sg_io.load_floats(path)
   -- 同パターン。parsed = load(bytes, path)() の戻り (table) 

function sg_io.load_png(path)
   -- 同パターン。parsed = { bytes, w, h, fmt } を C の load_png で生成
```

3 関数すべて **mtime fast-path → hash → parse** の同形パターンで、毎フレームのコストは nominally `stat()` 1 回。

## サンプル refactor

### ファイル配置

```
samples/
├── sg_io.lua                       (新規)
├── 01_triangle.lua                 (refactor)
├── 02_vertex_color.lua             (refactor)
├── 03_texture.lua                  (refactor)
├── 04_mvp.lua                      (refactor)
└── data/
    ├── 01_triangle.vs.slang
    ├── 01_triangle.fs.slang
    ├── 01_triangle.verts.lua
    ├── 02_vcol.vs.slang
    ├── 02_vcol.fs.slang
    ├── 02_vcol.verts.lua
    ├── 03_tex.vs.slang
    ├── 03_tex.fs.slang
    ├── 03_tex.verts.lua
    ├── 03_tex.png                  (新規 — 既存の Lua 内 procedural チェッカーを 16x16 PNG に書き出す)
    ├── 04_mvp.vs.slang
    ├── 04_mvp.fs.slang
    └── 04_mvp.verts.lua
```

### サンプルコード形 (例: 01_triangle.lua)

```lua
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
    local s = use_shader("01_shader", vs, fs, vsv ~ fsv)  -- 2 file の hash を XOR で結合
    local b = use_buffer("01_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b }, { shader = s })
    end_pass()
end
```

`vs/fs` の version は両ファイルの hash を XOR で結合する (どちらかが変われば全体の version が変わる)。

## エラーハンドリング

| 異常                                         | 挙動                                                      |
|----------------------------------------------|-----------------------------------------------------------|
| ファイル不在 / 読み込み失敗                  | helper が nil 返却。サンプル側で nil ガード (現フレーム skip)。エラーログ。 |
| .verts.lua が table 以外を return            | Lua エラー。helper は前回値を維持して返す。                |
| .slang の compile error                      | `make_shader` 失敗 → 旧 handle 維持、エラーログ、`e->version` 据え置き。 |
| .png の decode error                         | helper が nil 返却。サンプル側 skip。                      |
| update_buffer / update_image の backend エラー | エラーログ、`e->version` を更新せず次回再試行。           |

## テスト計画

### 自動: ビルド + リグレッション
- `cmake --build` で warning ゼロ。
- 4 sample × 2 backend × capture frame=30 を全パターン実行 → 全 exit 0、両 backend 4 ペア byte-identical。

### 手動: live-edit 動作確認
- Sample 01 起動中に `01_triangle.fs.slang` の出力色を変更 → 次フレームから色変化、ログにエラーなし。
- Sample 03 起動中に `03_tex.png` を別の画像で上書き → 次フレームから貼り替わる。
- Sample 04 起動中に `04_mvp.verts.lua` の頂点を編集 → 三角形の形が変化。

### エラーパス
- Shader source を意図的に壊して保存 → compile エラーログ、旧 shader で描画継続、クラッシュなし。
- PNG を途中で削除 → 当該フレーム skip、ログにエラー、復帰時に再 upload。
- verts.lua を壊す → Lua エラーログ、前回 table 維持で描画継続。

### pipeline cache sweep
- Sample 01 を回しながら shader を 5 回書き換え → backend shutdown 時に leaked pipeline = 0 を ASAN / valgrind で確認、または backend 内 destroy 数カウンタのデバッグログで照合。

## Known issues に残るもの

(本設計実施後の状態)

- Lua 側で 0.5 秒ごと等の throttle を入れない場合、毎フレーム `stat()` 4〜6 回呼ぶ。`stat()` 自体は cheap だが、頻度が気になる場合は将来 throttle や inotify 化。
- shader compile error 時の親切な行番号 / column 表示は Slang diagnostic 任せ。整形は将来。
- macOS / Windows での mtime ns 精度はファイルシステム依存 (HFS+ / NTFS は s 〜 100ns 精度)。本 PoC は Linux 限定なので影響なし。
