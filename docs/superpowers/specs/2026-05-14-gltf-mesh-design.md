# 2026-05-14 — glTF メッシュ対応 設計

## ゴール

sglua から glTF 2.0 のメッシュデータ (頂点 + index) を読み込んで描画できるようにする。
- 新規 Lua API `load_gltf(path)` を 1 個追加。`load_png` と同形 (raw data 返却 + caller が `use_buffer` に流す)。
- runtime parse、`.glb` / `.gltf` 両形式に cgltf 経由で対応。
- 既存の `sg_io.lua` mtime fast-path + hash version 経路にそのまま乗せて live edit と一貫。
- サンプル 1 個 (`samples/08_gltf.lua`) で Box の法線可視化を回転表示、native (sokol / sdlgpu) + web 3 系統で動作。
- Khronos `Box.glb` (CC0) を vendor。同時に repo の third-party ライセンス整備 (`THIRD_PARTY_LICENSES.md` 新設) も巻き取る。

## 非ゴール

- material (baseColorTexture / baseColorFactor / PBR 系) 読み込み — 別 spec。
- 複数 primitive / 複数 mesh / scene node tree の走査 — `mesh[0].primitives[0]` 固定。
- node transform 反映 (mesh はローカル空間のまま) — 別 spec。
- skinning / morph target / animation。
- 埋め込みテクスチャ (画像 buffer view / data URI) のデコード。
- camera / light。
- web playground での `.glb` 差し替え UX (ドラッグ&ドロップ) — out of scope、将来作業。
- web playground での `.glb` バイナリ編集 UX (CodeMirror では非現実的)。
- sglua 本体ライセンスの確定 (現状 README 末尾「未定」のまま据え置き)。

## 採用ライブラリ

| 用途 | 採用 | 理由 |
|------|------|------|
| glTF パーサ | cgltf (single-header, MIT) | sokol/stb と同じ single-header 文化に揃う。`.glb` / `.gltf` 両対応、外部 `.bin` の相対解決も内部で完結。~3K LOC、追加依存ゼロ。 |

`tinygltf` は C++ + nlohmann/json + stb_image 依存でビルド時間が伸び、mesh のみのスコープでは過剰。自前 parser は glTF spec の edge case が多く PoC スコープを逸脱。

## Lua API 契約

### `load_gltf(path) -> table | nil`

C 側で実装し Lua にグローバルバインド (既存 `load_png` と同階層)。

成功時の返り値 (single Lua table):

```lua
{
  positions   = {x0,y0,z0, x1,y1,z1, ...},  -- 必ず存在
  normals     = {nx,ny,nz, ...},            -- 無ければ nil
  uvs         = {u0,v0, u1,v1, ...},        -- 無ければ nil (TEXCOORD_0 のみ)
  indices     = {i0,i1,i2, ...},            -- 非 indexed なら nil
  vert_count  = N,
  index_count = M,                          -- 非 indexed なら 0
}
```

- 対象は `gltf->meshes[0].primitives[0]` のみ。
- triangle primitive 以外はエラー扱い (`nil` 返却、stderr ログ)。
- POSITION 欠落はエラー扱い。
- TANGENT / COLOR_0 / JOINTS_n / WEIGHTS_n / TEXCOORD_1+ は無視 (path ごとに 1 回だけ警告)。
- indices は uint8 / uint16 / uint32 のいずれであっても Lua integer に統一して返す。
- 失敗時は `nil` のみ返す (`load_png` と同様、エラー文字列は返さない)。診断は stderr。

### `sg_io.load_gltf(path)` (Lua wrapper)

`samples/sg_io.lua` に追加。`load_png` と同形:

```lua
function M.load_gltf(path)
   local parsed, ver = refresh(path, function(_bytes, p)
      return load_gltf(p)
   end)
   if not parsed then return nil end
   return parsed, ver
end
```

- mtime fast-path (`file_mtime` が同値なら parse skip、cache 返却)。
- content-changed 時は file bytes を `fnv1a64` で hash → `ver` を生成。
- 返却 `ver` は `use_buffer(key, ..., ver)` の `version` にそのまま流せる。

### interleave ヘルパ

`samples/sg_io.lua` に小さい helper を 1 個追加:

```lua
function M.interleave_pn(mesh)
   -- mesh.positions (vec3) + mesh.normals (vec3) -> {p.x,p.y,p.z, n.x,n.y,n.z, ...}
   -- normals が nil なら 0,0,1 で埋める (fallback)
   ...
end
```

scope は sample 08 が使う pos3+normal3 (stride 6) のみ。他の組合せ (pos+uv, pos+normal+uv) が必要になったら都度足す方針。

## C 実装

### ファイル追加

- `third_party/cgltf/cgltf.h` — vendor (upstream 最新 release を picked、commit hash を記録)。
- `third_party/cgltf/LICENSE` — cgltf upstream の MIT LICENSE を併置。
- `src/gltf.h` — `load_gltf` の C 側エントリ宣言 (Lua bind 用の signature)。
- `src/gltf.c` — `#define CGLTF_IMPLEMENTATION` を立てた TU。本体実装。

### `src/gltf.c` の責務

1. cgltf で `.glb` / `.gltf` を auto-detect parse。
2. `cgltf_load_buffers` で外部 `.bin` を解決 (`.gltf` 形式の場合)。
3. `mesh[0].primitives[0]` を取り出す。
4. primitive type が triangle でなければエラー。
5. POSITION accessor を float vec3 として走査、Lua table へ push。
6. NORMAL accessor があれば同様。
7. TEXCOORD_0 accessor があれば vec2 として push。
8. indices accessor があれば integer として push (型は uint8/16/32 → cgltf normalize 経由)。
9. 未対応属性に対しては `fprintf(stderr, ...)` で 1 回だけ警告。
10. `cgltf_free` で開放、Lua stack 上に table を残して return 1。

### `src/lua_api.c` への変更

- `load_gltf` Lua C 関数を 1 個 register (既存 `load_png` の隣)。
- 関数本体は `src/gltf.c` の `sgl_load_gltf` を呼んで Lua stack に table を残すだけ。

### Build 統合 (`CMakeLists.txt`)

- include path に `third_party/cgltf` を追加。
- `src/gltf.c` を既存 executable sources に追加。
- WASM target でも同じ source が乗る (cgltf は POSIX 依存無し、Emscripten で素通し)。

## ファイルシステム上の glTF 取扱い

### サポート形式 (runtime)

cgltf の auto-detect により以下を全て受ける:

| 形式 | 構成 | 外部解決 |
|------|------|---------|
| `.glb` | 単一バイナリ (JSON + bin + 画像が chunk に packed) | 不要 |
| `.gltf` + 外部 `.bin` + 外部 `*.png` | JSON の `buffers[].uri` が相対パス | cgltf が `.gltf` の親 dir から相対解決 |
| `.gltf` (data URI 埋め込み) | JSON 内 base64 | 不要 |

mesh-only スコープなのでテクスチャ系は今回読まないが、`buffers[].uri` の相対解決は cgltf に任せて将来の material 対応で再利用できる。

### Live edit (mtime watch)

- `sg_io.load_gltf` は対象パス本体 (`.glb` 又は `.gltf`) の mtime を 1 個 watch するのみ。
- `.gltf` + 外部 `.bin` 構成で外部 `.bin` だけ書き換えても reload しない (大半のオーサリングツールは `.gltf` と `.bin` を同時に書き出すので実害は低い)。README に注記する。
- パース失敗時は前フレームの parsed を維持 (`sg_io.refresh` が既にそうなっている)。
- 初回パース失敗は loud に止める方針 (shader と一貫、起動できないことが分かる方が良い)。

### Vendored asset の置き場

- `samples/data/08_box.glb` — Khronos `glTF-Sample-Assets` リポジトリの `Models/Box/glTF-Binary/Box.glb`。CC0 (Public Domain)、~3KB。
- `samples/data/08_box.glb.LICENSE.md` — CC0 表記 + 出典 URL + 取得 commit。

### ユーザ持ち込みの `.gltf` / `.glb`

- sglua は path を受け取って parse するだけなので、ユーザ自身のアセットは任意の path に置ける。
- ライセンス管理はユーザの責務。README に 1 段落「自分で読ませる glTF のライセンスは利用者責任」と明記。

## Sample 08 構成

### ファイル

```
samples/08_gltf.lua          -- entry
samples/data/08_gltf.vs.slang
samples/data/08_gltf.fs.slang
samples/data/08_box.glb      -- vendored (CC0)
samples/data/08_box.glb.LICENSE.md
```

shader 側は既存 prefix 規約 `08_gltf.*`、`.glb` だけ中身に合わせて `08_box.glb` と命名。

### 描画内容

- 法線可視化: vs で `MVP * pos`、normal を varying として渡す。fs で `normal * 0.5 + 0.5` を RGB 出力。
- MVP は sample 04 と同様、`on_frame` 内で時間に応じて回転行列を組む (Y 軸回転 + 適度な view/proj 行列)。
- テクスチャ無し、UV は使わない。

### Lua フロー

```lua
local sg_io = dofile("samples/sg_io.lua")

function on_init()
   -- shader 準備のみ
end

function on_frame(t)
   local sh_vs, sh_ver_vs = sg_io.load_text("samples/data/08_gltf.vs.slang")
   local sh_fs, sh_ver_fs = sg_io.load_text("samples/data/08_gltf.fs.slang")
   use_shader("gltf_sh", sh_vs, sh_fs, sh_ver_vs ~ sh_ver_fs)

   local mesh, mesh_ver = sg_io.load_gltf("samples/data/08_box.glb")
   local verts = sg_io.interleave_pn(mesh)
   use_buffer("gltf_vb", VERTEX, verts, mesh_ver)
   use_buffer("gltf_ib", INDEX, mesh.indices, mesh_ver)

   begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.15, 1} })
   draw(mesh.index_count,
        { verts = "gltf_vb", indices = "gltf_ib", uniforms = { mvp = make_mvp(t) } },
        { shader = "gltf_sh", depth = true, depth_write = true, cull = "BACK" })
   end_pass()
end
```

(疑似コード。`indices` を `draw` の `resources` で渡すスタイルは既存 API を確認しつつ調整。現状 README の `draw` 仕様は `verts` のみ言及で `indices` の扱いが書かれていないため、実装段階で確認・必要なら API 追加。)

### Pipeline state

- depth テスト ON、depth write ON (背面が見える Box なので必須)。
- backface culling ON。
- primitive = triangle。

## Web playground での扱い

### Build 時 bundling

- WASM ビルドの Emscripten data file package (`sglua.data`) は `samples/data/*` を preload する経路がある。`samples/data/08_box.glb` は自動でその package に含まれる想定。
- CMake の preload 設定が拡張子フィルタしている場合のみ `.glb` を追加 — 実装段階で確認。

### Editor (CodeMirror) 上の見せ方

- `.glb` は editor タブに出さない。
- サンプル 08 を選んだ際は `08_gltf.lua` / `08_gltf.vs.slang` / `08_gltf.fs.slang` の 3 つのみ open。
- `samples.ts` が `/samples/` を fetch して tab 構成するロジックで、`.glb` を除外する filter を追加。

### Live edit の射程 (web)

- `.lua` / `.slang` の編集 → 反映 (既存経路で動く)。
- `.glb` 自体の差し替え UX は提供しない。

### Verify

- `web/scripts/verify-headless.mjs` の sample 巡回 list に `08_gltf` を追加。
- 非黒比率閾値での pass 判定。06 と違って描画は素直に通るはず (single render pass + depth)。
- 06 と同様の `KNOWN_FAILING` には登録しない。

## テスト計画 (native)

- `tests/golden/08_gltf_sokol.png` と `tests/golden/08_gltf_sdlgpu.png` を確定。
- `scripts/run-golden.sh` は samples を自動列挙するか固定リストか実装次第 — 実装段階で確認、固定リストなら 08 を追記。
- lavapipe + xvfb 環境で両 backend が byte-identical になることを確認 → golden 確定。
- ヘッドレス capture: `scripts/run-headless.sh samples/08_gltf.lua --capture out.png --capture-frame 30` で動くこと。

## ライセンス整備

### 新設

- `THIRD_PARTY_LICENSES.md` を repo root に新設。

### `THIRD_PARTY_LICENSES.md` 構造

```markdown
# Third-Party Licenses

sglua bundles or links the following third-party components.

## Vendored single-header / sources

| Component | Path | License | Source |
|-----------|------|---------|--------|
| sokol_gfx | third_party/sokol/sokol_gfx.h | zlib | https://github.com/floooh/sokol |
| stb_image / stb_image_write | third_party/stb/ | Public Domain / MIT | https://github.com/nothings/stb |
| cgltf | third_party/cgltf/cgltf.h | MIT | https://github.com/jkuhlmann/cgltf |
| Slang headers | third_party/slang/include/ | Apache-2.0 + MIT | https://github.com/shader-slang/slang |
| lume | third_party/lume/lume.lua | MIT | https://github.com/rxi/lume |

## Fetched at configure time (gitignored)

| Component | Path | License | Source |
|-----------|------|---------|--------|
| Slang prebuilt | third_party/slang/{lib,bin}/ | Apache-2.0 + MIT | shader-slang/slang releases |

## CMake FetchContent (configure-time source build)

| Component | License | Source |
|-----------|---------|--------|
| SDL3 | zlib | https://github.com/libsdl-org/SDL |
| Lua 5.5 | MIT | https://www.lua.org |

## Bundled sample assets

| Asset | Path | License | Source |
|-------|------|---------|--------|
| Box.glb | samples/data/08_box.glb | CC0 (Public Domain) | https://github.com/KhronosGroup/glTF-Sample-Assets |

各依存先のフルテキストは `third_party/<component>/LICENSE` または上記 Source URL を参照。
```

### 既存 `third_party/*/LICENSE` の補完

- 現状 `third_party/sokol/LICENSE` / `third_party/stb/LICENSE` 等が存在するか未確認 (実装段階で点検)。
- 欠けているものは upstream から取って併置する。1 ファイル数 KB なので作業量小。

### sglua 本体ライセンス

- 現状 README 末尾「未定」のまま据え置き。今回のスコープでは触らない。

## エラーハンドリング

| 状況 | 挙動 |
|------|------|
| ファイル不在 | `sg_io.load_gltf` が nil 返却 (既存 refresh の挙動) |
| cgltf parse 失敗 | `load_gltf` が nil 返却、stderr に診断 |
| `mesh[0]` 不在 | nil 返却 + stderr |
| `primitives[0]` 非 triangle | nil 返却 + stderr |
| POSITION 欠落 | nil 返却 + stderr |
| 未対応属性 (TANGENT 等) | パースは継続、stderr に 1 回だけ警告 |
| 初回パース失敗 | 後段の `interleave_pn` / `use_buffer` で nil を扱えず例外 → 通常の Lua エラーとして loud に止まる (shader 初回失敗と同様の方針) |
| 2 回目以降のパース失敗 | `refresh` が前回 parsed を維持して継続 |

## Out of scope (明示)

- material (baseColorTexture / baseColorFactor / metallic-roughness / normalTexture / emissive / alpha mode 等)
- 複数 primitive / 複数 mesh / scene node tree の走査
- node transform 反映
- skinning / morph target / animation
- 埋め込みテクスチャ (画像 buffer view、data URI image) のデコード
- camera / light の取り込み
- web playground での `.glb` 差し替え / ドラッグ&ドロップ UX
- web playground での `.glb` 編集 UX
- 大きなメッシュ (数十万頂点) でのパフォーマンス最適化 (Lua table 経由のオーバーヘッドが顕在化したら別途検討)
- sglua 本体ライセンス確定

## オープン項目 (実装時に確認)

1. CMake の WASM 向け preload-file 設定が `samples/data/*` 丸ごと拾うか、拡張子フィルタしているか — `.glb` 追加要否を判断。
2. `scripts/run-golden.sh` が sample を自動列挙するか固定リストか — 08 追記要否を判断。
3. `draw` API における indexed draw の経路 — README 現状記述には `indices` の明示が無く、`resources` table 内で `indices = bufferRef` を受ける形か、別関数 `draw_indexed` か、実装段階で API 形状を確認 (必要なら拡張、これは glTF 対応の前提に近い)。
4. 既存 `third_party/*/LICENSE` の所在点検 — 欠けているものを補う。
