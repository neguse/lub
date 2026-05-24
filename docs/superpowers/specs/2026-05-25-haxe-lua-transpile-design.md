# Haxe -> Lua transpile / reload (Phase 0)

roadmap.md の Phase 0 のうち **「Haxe -> Lua の transpile / reload 体験が最小構成で成立している」** を満たすための設計。

## 目的と非目的

### 目的
- `.hxml` を entry にした `lub <entry.hxml>` で、Haxe で書かれた sample が cold compile 待ち無しに hot reload される dev 体験が成立する。
- ライセンス境界を保つ: lub runtime は MIT、haxe binary は dev / build 時に subprocess として叩くだけで link / embed しない。

### 非目的
- production / release ビルドの最適化、build artifact の packaging。
- haxe compiler を WASM で同梱して runtime にバンドルすること。
- 速度面の二次最適化 (例: incremental concat キャッシュ、parallel build)。

## 設計の前提と必然性

設計上の各選択は次の必然性に縛られる。これに無い選択は spec に入れない。

1. **lub の hot reload は entry `.lua` mtime polling + `lume.hotswap`** (app.c:64-77, lua_api.c:1173-1207)。`.lua` を atomic に書き換えれば自動で reload が走る。
2. **Haxe の compile 出力は標準では module を返さない**。`require()` から table を返すための shim が必須。
3. **Haxe の build script 標準は `.hxml`**。lub が自前で classpath / `-D` を扱うと発明物だらけになる。
4. **Phase 0 の文言は「reload 体験」**。cold compile (秒オーダー) では成立しない。`haxe --wait` 常駐が必須。
5. **複数 sample / 複数ファイル の Haxe project が現実**。entry 一個だけ watch する仕組みでは不足。

## CLI

```
lub <entry.lua>      既存。Lua 直接実行。何も変えない。
lub <entry.hxml>     Haxe build + 実行。entry 拡張子で dispatch。
```

- subcommand なし。`build` / `dev` / `run` / `watch` などの verb は持たない。
- 既存実行時 flag (`--capture`, `--capture-frame`) は両 mode で使える。CI / golden は `lub <entry.hxml> --capture out.png --capture-frame 30` だけで run-and-exit できる。
- 設定ファイル (`lub.lua`, `lub.toml`) は持たない。haxe 側の挙動は user が `.hxml` で記述する。
- 例外で `LUB_HAXE_PORT` env var のみ port range override 経路として認める (port 衝突回避の逃げ道)。

## ファイル配置

```
<project>/
  samples/
    foo.hxml                  # haxe build script (commit)
    Foo.hx                    # haxe source (commit)
    Bar.hx                    # helper class (commit)
    .lub/                     # 出力ディレクトリ (gitignore)
      foo.lua                 # 生成 .lua = embedded prelude + haxe raw + embedded postlude
    boot.lua                  # 既存、手書き Lua (commit)
    lub_io.lua                # 既存、手書き Lua (commit)
    data/                     # 既存
  .gitignore                  # .lub/ を含む
```

`.lub/` は dir 単位で gitignore できる。`.gitignore` には `.lub/` 1 行だけ追加。

lub haxe extern は別 dir に置く:

```
<lub-repo>/
  haxe-lib/lub/               # haxelib 形式 (haxelib.json + package lub/ 配下に classes)
    haxelib.json
    lub/                      # package lub
      Lub.hx                  # class Lub (config のみ)
      Gfx.hx                  # class Gfx (描画・GPU・定数)
      Input.hx                # class Input
      Io.hx                   # class Io (lub_io.lua cached loaders)
      Sys.hx                  # class Sys (raw C primitives, 普段使わない)
```

開発者は `haxelib dev lub <lub-repo>/haxe-lib/lub` を 1 回実行して登録。user の hxml は `-lib lub` で参照する。user code は `import lub.Lub; import lub.Gfx;` のように責務単位で取り込む。

## entry hxml の最小例

```
# samples/foo.hxml
-cp samples
-lib lub
-main Foo
```

3 行。`-main` の class 名は haxe の規約で `<同名>.hx` ファイルを探す。`--lua` は **書かない**。lub が runtime で `--lua <.lub/foo.raw.lua>` を append するため (lub が書き先を atomic に制御する必然)。

**hxml basename と haxe class 名の関係**: hxml basename (例: `foo.hxml` の `foo`) は require 名 / 出力 `.lua` basename と一致するので Lua 側からの参照名。haxe class 名 (`-main` で指定) は完全に独立。e.g. `samples/01_triangle.hxml` が `-main Triangle01` を宣言、`Triangle01.hx` がそのクラスを持つ、出力は `samples/.lub/01_triangle.lua` で `require("01_triangle")` から呼ばれる、というのが普通の形。

## Module return contract

Haxe `-lua` 出力は最後に `<MainClass>.main()` を呼ぶ script で、`require()` 結果としては何も返さない。一方、Haxe の class は Lua 出力で「static method を field として持つ table」になる (`Triangle01 = { onInit = function..., onFrame = function..., main = function... }`)。

この性質を使い、lub は **`<MainClass>` table そのものを require の戻り値にする**:
- prelude は最小限の shim だけ
- postlude は build 時に hxml の `-main <ClassName>` から動的生成して `return <ClassName>` を末尾に置く
- `register()` API や `_lub_module` global、`LubModule` typedef は要らない

これに伴い lub C 側 (lua_api.c の `lua_ctx_call_init` / `call_frame` 等が読むフィールド名) も Haxe 慣習に合わせ snake_case (`on_init` 等) から camelCase (`onInit`/`onFrame`/`onEvent`/`onQuit`) に揃える。既存 `samples/*.lua` は Phase 0 の sample 移植で全部置き換わるので、契約変更は移植作業に巻き込む形で吸収する。

**lub binary に embed する prelude** (生成 `.lua` の先頭に concat、static):
```lua
-- haxe std で足りない bits の shim (lub runtime は Lua 5.5 で utf8 built-in だが
-- Haxe lua target が require("lua-utf8") を出す場合に備えて alias を貼っておく)
package.preload["lua-utf8"] = function()
   return { len = string.len, char = string.char, upper = string.upper,
            lower = string.lower, find = string.find, sub = string.sub, byte = string.byte }
end
```

**lub が build 毎に動的生成する postlude** (末尾に concat、`<ClassName>` は hxml の `-main` から取る):
```lua
return <ClassName>
```

**Haxe 側 (典型 sample)**:
```haxe
import lub.Lub;
import lub.Gfx;

class Triangle01 {
  public static function main() {
    // 一度だけの module-load 時処理 (任意)。lub の onInit / onFrame とは別軸。
  }
  public static function onInit() { Lub.config({ backend: "sokol" }); }
  public static function onFrame() { /* Gfx.beginPass / Gfx.draw / Gfx.endPass 等 */ }
}
```

- Haxe 末尾の自動 `Triangle01.main()` は module-load 時に 1 回走る。hot reload で再 require されると `main()` も再実行される。
- lub が呼ぶ `onInit` は startup 時 1 回 (hotswap 後は呼ばれない、既存挙動)、`onFrame` は毎フレーム。

prelude を **外部ファイルにせず lub binary に embed** する理由: 外部化すると path 設定 → CLI flag → config ファイルと派生コストが連鎖し、Phase 0 で必然性が無い。postlude は class 名依存なので embed できず、build 毎に lub が `"return " + className + "\n"` を生成する。

## Externs

extern は `package lub` の下に責務別 class を分ける。user code は `import lub.Lub; import lub.Gfx;` のように必要な部分だけ取り込む。

### 命名方針 (Haxe 標準準拠)

- package: lowercase (`lub`)
- class: PascalCase (`Lub`, `Gfx`, `Input`, `Io`, `Sys`)
- method / variable: camelCase。Lua side が snake_case な場合は `@:native("snake_case")` で結ぶ (`beginPass` ↔ `begin_pass`、`loadText` ↔ `load_text`)
- static constant (UPPER snake): `UPPER_SNAKE_CASE` のまま (`VERTEX`, `RGBA8`, `NONE` 等。Lua side も同名なので `@:native` も冗長だが明示的に付ける)
- entry class の callback メソッド (`onInit`, `onFrame`, `onEvent`, `onQuit`) も camelCase。lub C 側の field 読み出しが camelCase 契約に揃う (Module return contract 節 を参照)

### `haxe-lib/lub/lub/Lub.hx` — runtime entry / config

```haxe
package lub;

extern class Lub {
  @:native("config") public static function config(opts: Dynamic): Void;
}
```

entry class の callback (`onInit` / `onFrame` / `onEvent` / `onQuit`) は extern としては宣言しない。user の class が **そのまま module table になる** ので、user が必要な callback だけ定義すれば lub の C 側がそれを呼ぶ (callback は optional — 定義されていなければ lub は呼ばない)。

### `haxe-lib/lub/lub/Gfx.hx` — GPU / 描画関連すべて

```haxe
package lub;

extern class Gfx {
  // pass
  @:native("begin_pass")         public static function beginPass(opts: Dynamic): Void;
  @:native("end_pass")           public static function endPass(): Void;
  // resources
  @:native("use_shader")         public static function useShader(key: String, vs: String, fs: String, version: Int): Dynamic;
  @:native("use_shader_compute") public static function useShaderCompute(key: String, src: String, version: Int): Dynamic;
  @:native("use_buffer")         public static function useBuffer(key: String, type: Int, data: lua.Table<Int, Float>, version: Int): Dynamic;
  @:native("use_texture")        public static function useTexture(key: String, px: Dynamic, w: Int, h: Int, fmt: Int, version: Int, ?opts: Dynamic): Dynamic;
  // commands
  @:native("draw")               public static function draw(count: Int, bindings: Dynamic, opts: Dynamic): Void;
  @:native("dispatch")           public static function dispatch(x: Int, y: Int, z: Int, bindings: Dynamic, opts: Dynamic): Void;
  // capture (swapchain 由来なので gfx 配下)
  @:native("capture")            public static function capture(path: String): Void;

  // globals
  @:native("main_tex")           public static var mainTex(default, null): Dynamic;

  // constants (enums_lua.c の C-side global integer)
  // buffer type
  @:native("VERTEX")             public static var VERTEX(default, null): Int;
  @:native("INDEX")              public static var INDEX(default, null): Int;
  @:native("UNIFORM")            public static var UNIFORM(default, null): Int;
  @:native("STORAGE")            public static var STORAGE(default, null): Int;
  // pixel format
  @:native("RGBA8")              public static var RGBA8(default, null): Int;
  // ... R8, RG8, RGBA16F, RGBA32F, DEPTH16, DEPTH24_STENCIL8, DEPTH32F
  // load / store
  @:native("CLEAR")              public static var CLEAR(default, null): Int;
  // ... LOAD, DONTCARE, STORE
  // blend / cull
  @:native("NONE")               public static var NONE(default, null): Int;
  // ... ALPHA, ADDITIVE, MULTIPLY, BACK, FRONT
  // primitive
  @:native("TRIANGLES")          public static var TRIANGLES(default, null): Int;
  // ... TRIANGLE_STRIP, LINES, LINE_STRIP, POINTS
  // sampler
  @:native("LINEAR")             public static var LINEAR(default, null): Int;
  // ... NEAREST, REPEAT, CLAMP
}
```

### `haxe-lib/lub/lub/Input.hx` — 入力

```haxe
package lub;

extern class Input {
  @:native("key_down")           public static function keyDown(code: String): Bool;
}
```

### `haxe-lib/lub/lub/Io.hx` — `lub_io.lua` cached loaders (普段使う方)

```haxe
package lub;

@:luaRequire("lub_io")
extern class Io {
  @:native("load_text")    public static function loadText(path: String): lua.PairTools.MultiReturn2<String, Int>;
  @:native("load_floats")  public static function loadFloats(path: String): lua.PairTools.MultiReturn2<Dynamic, Int>;
  @:native("load_png")     public static function loadPng(path: String): lua.PairTools.MultiReturn5<Dynamic, Int, Int, Int, Int>;
  @:native("load_gltf")    public static function loadGltf(path: String): lua.PairTools.MultiReturn2<Dynamic, Int>;
  @:native("interleave_pn") public static function interleavePn(mesh: Dynamic): lua.Table<Int, Float>;
}
```

### `haxe-lib/lub/lub/Sys.hx` — raw C-side primitive (普段は使わない、低 level)

```haxe
package lub;

extern class Sys {
  @:native("file_mtime")         public static function fileMtime(path: String): Null<Float>;
  @:native("fnv1a64")            public static function fnv1a64(s: String): Int;
  @:native("load_png")           public static function loadPng(path: String): Dynamic;
  @:native("load_gltf")          public static function loadGltf(path: String): Dynamic;
}
```

### user code から見た形

```haxe
import lub.Lub;
import lub.Gfx;
import lub.Io;

class Triangle01 {
  public static function main() {
    // 必要なら一度だけの module-load 時処理。空でもよい。
  }
  public static function onInit() {
    Lub.config({ backend: "sokol" });
  }
  public static function onFrame() {
    // Gfx.beginPass / Gfx.draw / Gfx.endPass / Gfx.VERTEX / Gfx.NONE / Gfx.mainTex 等
    // Io.loadText / Io.loadFloats / Io.loadPng 等
  }
}
```

`onInit`/`onFrame` は **public static** で書く。class table の field として Lua 出力に並ぶ。`register()` 呼び出しは不要。lub は hxml の `-main Triangle01` を読んで postlude `return Triangle01` を build 時に注入し、`require` の戻り値が class table そのものになる。

Multi-return の正確な API は Haxe 5 の `lua.PairTools` / `lua.MultiReturn` の挙動を確認して impl 時に確定する。

## Process management

### プロセスツリー
```
lub (parent)
├── haxe --wait <port>          ← 長寿命子プロセス (compilation server)
└── haxe --connect <port> ...   ← rebuild の度に短命 spawn (~100-300ms)
```

### 起動シーケンス (`lub <entry.hxml>`)
1. **port + server spawn**: `LUB_HAXE_PORT` env var があればそれを採用、無ければ 7400 から開始。`SDL_CreateProcess(["haxe", "--wait", "<port>"], ...)` を spawn し、stdout を pipe で受ける。最初の "Waiting on..." 行が来れば成功、子が即 exit したら port 衝突とみなして port + 1 で再試行 (7410 まで)。全滅なら fatal exit。env var で固定された場合は probe せず 1 回試行のみ。
2. **初回 build**:
   - `<dir>/.lub/` を `SDL_CreateDirectory` で再帰作成。
   - haxe 出力の中間 raw を一時ファイルに書かせる。パスは `<dir>/.lub/<basename>.raw.tmp` (lub 内部限りの中間、user に意味のあるファイルではない)。
   ```
   SDL_CreateProcess(["haxe", "--connect", port, "<entry.hxml>",
                      "--lua", "<dir>/.lub/<basename>.raw.tmp"])
   ```
   exit code 待ち。exit ≠ 0 なら haxe diagnostic を log に流して fatal exit (初回失敗で実行する意味なし)。
3. **concat + atomic write**:
   - hxml を parse して `-main <ClassName>` から class 名を取り、postlude を `"return " + className + "\n"` として動的生成。
   - embedded prelude + 中間 raw の内容 + 生成 postlude を `<dir>/.lub/<basename>.lua.tmp` に書く。
   - `SDL_RenamePath` で `<basename>.lua.tmp` → `<basename>.lua` に atomic 差し替え。
   - 中間 raw (`<basename>.raw.tmp`) は削除。
4. **package.path 更新**: lub init 中に `package.path = "<dir>/.lub/?.lua;" .. package.path` を inject (boot.lua より前)。
5. **既存経路**: backend init → Lua state → boot.lua → `require("<basename>")` → `.lub/<basename>.lua` が読まれる。
6. **watch root の記憶**: hxml を line parse して `-cp <path>` をすべて抜き出し、watch root list として保持する。hxml 自体のパスも watch list に入れる。
   - `-lib <name>` 経由で haxelib classpath に入る source (例: `lub` extern) は **watch 対象外**。user は自分の `-cp` 配下しか編集しない前提。`lub` extern を編集したい開発者は明示的に該当 dir を `-cp` で追加するか lub を再起動する。

### main loop での watch (毎フレーム)
- 既存: entry `.lua` mtime polling → 変わったら `lume.hotswap("<basename>")`。
- 追加: hxml + watch root list 配下の `*.hx` を recursive mtime polling。
  - debounce: 50ms 以内の連続変更は最後の一回にまとめる。
  - 変更検知 → 上記「初回 build」の build path をもう一度実行 (server warm なので 100-300ms)。hxml の変更時は `-cp` / `-main` の再 parse もこの中で発生し、watch root list と postlude class 名が新しい hxml に追従する。
  - build fail → 古い `.lub/<basename>.lua` をそのまま残し、haxe diagnostic を log に流す。

### shutdown
- `SDL_KillProcess(server_child, force=true)` で `haxe --wait` を kill。
- `SDL_DestroyProcess` でリソース解放。
- shutdown handler は SIGINT / `SDL_AppQuit` の両経路でこの clean-up を呼ぶ。

### haxe が PATH に無い場合
- 起動時 `SDL_CreateProcess(["haxe", "--version"], ...)` で probe。
- 不在で entry が `.hxml` → fatal exit (build できないので意味なし)。
- 不在で entry が `.lua` → 何もしない (haxe 抜きの既存経路)。

### WASM build
- `SDL_CreateProcess` 不可。`#ifdef __EMSCRIPTEN__` で server spawn / 子プロセス管理コードを compile out。
- WASM では `.hxml` entry を受けても build は走らない。代わりに web/ 側の編集 UI が HTTP compile endpoint に `.hx` を投げ、戻った `.lua` を `FS.writeFile` で MEMFS に書く (既存 syncFiles 経路を流用)。lub runtime 側の mtime polling は同一。

## Web playground (HTTP compile endpoint)

WASM 環境では haxe を WASM 内で動かさず、外部の compile service に投げる。

### Endpoint 仕様
```
POST <HAXE_COMPILE_URL>
Content-Type: application/json
body: {
  "hxml": "<.hxml の内容>",
  "files": { "<path>": "<.hx 内容>", ... }
}
reply (200 OK):  { "ok": true, "lua": "<生成 .lua>", "log": "<haxe stdout>" }
reply (4xx):     { "ok": false, "log": "<haxe diagnostic>" }
```

### deployment
- try.haxe.org は **使わない** (license / 安定性 / CORS の懸念で外部依存にしない)。
- 第一候補: **Cloudflare Worker + container 上で haxe binary を駆動** する self-hosted service。web/ が既に Wrangler を扱っている前提を活かす。
- Phase 0 のスコープは contract と fallback 経路まで。endpoint 本体の実装 / デプロイは別 spec / 別 PR で扱う。

### web/ 側の挙動
- editor は `.hx` / `.hxml` を CodeMirror で扱える (`@codemirror/legacy-modes/mode/haxe.cjs` を既に node_modules に持つ)。
- 編集後 debounce 300ms (既存 syncFiles と同じ) で endpoint へ POST。
- 200 OK → 戻ってきた `.lua` を MEMFS の `.lub/<basename>.lua` に `FS.writeFile`。lub の mtime polling が拾って hot reload。
- endpoint URL は build 時 env (`VITE_HAXE_COMPILE_URL`) から注入。未設定なら `.hx` 編集を read-only にして従来 `.lua` 編集経路だけ有効化。

## Lua 5.5 互換

- lub runtime は Lua 5.5。
- Haxe 5 の `-lua` target は最高 `lua_ver=5.4`。`-D lua_ver=5.3` または `5.4` で出力。
- Lua 5.4 → 5.5 は後方互換性が高く、Haxe 出力の `.lua` が 5.5 で動く想定。prelude の `package.preload["lua-utf8"]` shim と合わせ、互換性問題が出たら追加 shim を prelude に積む。
- 検証: Phase 0 implementation 時に最小 sample で 5.5 上で確実に動くことを確認する。

## サンプル移植 (Phase 0 deliverable)

現状の `samples/*.lua` (14 個: `00_hello.lua` 〜 `11_shadow.lua`) を `.hxml + .hx` に移植する。toolchain と extern が固まれば各 sample の移植は mechanical translation。移植順:

1. `01_triangle` — 最小、extern の最初の動作確認に好適。
2. `00_hello`, `00b_clear`, `00c_buffer`, `00d_shader` — 基本 API カバー。
3. `02_vertex_color`, `03_texture`, `04_mvp`, `05_postprocess` — 描画系の典型。
4. `06_deferred`, `07_compute`, `08_gltf` — 高度系。
5. `09_breakout`, `10_breakout3d` — stateful な sample。reload で gameplay state が reset される現象は現状の Lua sample と同じ挙動になる前提。
6. `11_shadow` — render target 系の総合確認。

各 sample の移植が完了するごとに既存 `samples/<name>.lua` を削除し、`samples/<name>.hxml` + `samples/<Name>.hx` に置き換える。`samples/.lub/<name>.lua` は generated。

## テスト / golden 統合

既存 `scripts/run-golden.sh` は `lub samples/01_triangle.lua --capture out.png --capture-frame 30` を実行して PNG diff する。移植後は entry を `.hxml` に切り替えるだけで同経路が回る:

```sh
lub samples/01_triangle.hxml --capture out.png --capture-frame 30
```

lub は:
1. `haxe --wait` を立て
2. `01_triangle.hxml` を build
3. `samples/.lub/01_triangle.lua` を書き
4. backend init + Lua boot + 30 frame run + capture + exit

までを 1 コマンドで通すので、別 build verb は要らず golden の構造は変わらない。

WASM playground の verify (`web/scripts/verify-headless.mjs`) は HTTP compile endpoint が必要なため、endpoint デプロイ後に別途扱う。Phase 0 で web 側 verify を Haxe に切り替えるのは optional。

## 工程と切り出し

Phase 0 spec のうち実装は次の順:

1. **lub runtime の `.hxml` dispatch + haxe server 管理 + atomic write + watch**: C/C++ 側の変更。`#ifdef __EMSCRIPTEN__` の分岐込み。
2. **`haxe-lib/lub/` の extern 整備**: Lub.hx / LubIo.hx / haxelib.json。
3. **prelude / postlude を lub binary に embed**: 文字列 literal として持つ。
4. **01_triangle の `.hxml` + `.hx` 移植 + golden 比較**: 最初の動作確認 sample。
5. **残り 13 sample の mechanical 移植**。
6. **WASM compile endpoint contract のみ**: 実装は Phase 0 外。

## 未解決事項

- Haxe 5 の `lua.PairTools.MultiReturn*` の正確な型名は impl 時に確定 (`lub_io.lua` の multi-return を Haxe extern で正しく受ける書き方)。
- haxelib `lub` の version 戦略 (semver / lub runtime と lockstep)。
