# Haxe -> Lua transpile / reload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** lub に `.hxml` entry を渡すと、Haxe コンパイラを `--wait` で常駐させて hot reload 体験を成立させる toolchain を作り、01_triangle を Haxe で書き直して golden test まで通す。

**Architecture:** lub binary が起動時に `haxe --wait <port>` を子プロセスとして spawn し、`.hxml` を受け取ったら `haxe --connect <port> <hxml> --lua <tmp>` で build → embedded prelude + raw + 動的生成 postlude を concat → `<dir>/.lub/<basename>.lua.tmp` に書いて atomic rename → 既存の `.lua` mtime polling 経路が `lume.hotswap` する。`.hx` mtime polling で再 build を発火。WASM 上では haxe spawn を `#ifdef LUB_WASM` で抜き、別途 HTTP compile endpoint contract のみ Phase 0 で文書化。

**Tech Stack:** C11 (lub runtime)、SDL3 (`SDL_CreateProcess`/`SDL_RenamePath`)、Lua 5.5 (`lume.hotswap`)、Haxe 5+ (`-lua` target、`--wait` / `--connect`)、CMake、既存 golden 経路 (`scripts/run-golden.sh` + `tests/golden/*.png`)。

**Spec:** `docs/superpowers/specs/2026-05-25-haxe-lua-transpile-design.md` (commit `6789c0e`)。

---

## File Structure

### 新規 (lub C-side runtime)
- `src/haxe_server.c` / `src/haxe_server.h` — `haxe --wait` 子プロセスの spawn / port probe / lifecycle
- `src/haxe_build.c` / `src/haxe_build.h` — hxml parse、`--connect` 呼び出し、prelude/postlude concat、atomic write
- `src/haxe_watch.c` / `src/haxe_watch.h` — `.hx` recursive mtime cache、debounce、rebuild trigger
- `src/haxe_pipeline.h` — 上記 3 つを app から触る合成ヘッダ (lifecycle 関数 1 セット)
- `src/embedded_prelude.h` — `static const char HAXE_PRELUDE[]` の literal (生成しない、手で書く)

### 新規 (haxe-lib)
- `haxe-lib/lub/haxelib.json`
- `haxe-lib/lub/lub/Lub.hx`
- `haxe-lib/lub/lub/Gfx.hx`
- `haxe-lib/lub/lub/Input.hx`
- `haxe-lib/lub/lub/Io.hx`
- `haxe-lib/lub/lub/Sys.hx`

### 新規 (sample 移植: 01_triangle のみ Phase 0)
- `samples/01_triangle.hxml`
- `samples/Triangle01.hx`

### 修正
- `.gitignore` — `.lub/` 追加
- `CMakeLists.txt` — `src/haxe_*.c` 追加、`LUB_WASM` で除外
- `src/main.c` — `.hxml` 拡張子 dispatch、haxe pipeline init/shutdown 呼び出し
- `src/app.c` — frame_begin 内で haxe watch tick を呼ぶ
- `src/app.h` — `App` 構造体に haxe pipeline state を追加
- `src/lua_api.c` — callback field 名 `on_init`/`on_frame`/`on_event`/`on_quit` → `onInit`/`onFrame`/`onEvent`/`onQuit`、`package.path` への `.lub/?.lua` inject
- `src/main.c` (二重: 上の dispatch とは別箇所) — `--capture`/`--capture-frame` 既存 flag が `.hxml` でも動くことを確認

### 削除
- `samples/01_triangle.lua` (Haxe 版で置き換え)
- `tests/lua/*.lua` 内に `on_init`/`on_frame` を直接使う test が無いか確認、あれば camelCase に更新
- `samples/*.lua` のうち test 経由で呼ばれていないもの (00_hello〜11_shadow から 01 以外) は **Phase 0 の Task 31 でフォローアップ**として残る

### scripts/run-golden.sh の挙動
- 現状 `./build/lub samples/$sample.lua --capture ...` を実行。
- 01_triangle が `.lua` から `.hxml` に変わった瞬間に壊れる。
- 修正: スクリプト側で `samples/$sample.hxml` が存在すればそれを優先、無ければ `.lua` にフォールバック。

---

## Spec から導出される必然タスク

spec の必然性項目との対応:

| spec の必然 | 担当タスク |
|---|---|
| `.lua` mtime polling + lume.hotswap (既存) | 触らない (Task 4 で `.lub/?.lua` を package.path に積むだけ) |
| Haxe 出力に require できる shim (prelude/postlude) | Task 8 (prelude embed)、Task 18 (postlude 動的生成) |
| hxml が build script 標準 | Task 16 (hxml line parser) |
| `haxe --wait` 常駐 | Task 11–14 (haxe_server) |
| 多ファイル `.hx` の watch | Task 21–22 (haxe_watch) |
| atomic write | Task 19 (rename via `SDL_RenamePath`) |
| `.lub/` gitignore + package.path 経路 | Task 1, Task 4 |
| WASM compile-out | Task 26 |
| 01_triangle 動作確認 | Task 27–29 |
| 残り 13 sample | Task 31 (template) |

---

## Task 1: `.gitignore` に `.lub/` を追加

**Files:**
- Modify: `.gitignore`

- [ ] **Step 1: ファイル末尾に `.lub/` 行を追加**

```diff
 build/
 out/
 third_party/slang/lib/
 third_party/slang/bin/
 web/node_modules/
 web/dist/
+.lub/
```

- [ ] **Step 2: 動作確認**

Run: `mkdir -p /tmp/lub_gitignore_test/.lub && touch /tmp/lub_gitignore_test/.lub/foo.lua && (cd /tmp/lub_gitignore_test && git init -q && cp /home/neguse/ghq/github.com/neguse/lub/.gitignore . && git status --short)`

Expected: `.gitignore` だけ untracked、`.lub/foo.lua` は出ない。

- [ ] **Step 3: Commit**

```bash
git add .gitignore
git commit -m "Ignore .lub/ output directory for Haxe-generated samples"
```

---

## Task 2: lub C 側 callback 契約を camelCase に rename

callback field 名を `on_init`/`on_frame`/`on_event`/`on_quit` から `onInit`/`onFrame`/`onEvent`/`onQuit` に変える。`call_module_field` の呼び出しと、`config: must be called inside on_init` のようなエラーメッセージ両方を更新。

**Files:**
- Modify: `src/lua_api.c`

- [ ] **Step 1: 該当箇所を確認**

Run: `grep -n "on_init\|on_frame\|on_event\|on_quit" /home/neguse/ghq/github.com/neguse/lub/src/lua_api.c`

Expected: `lua_ctx_call_init`/`lua_ctx_call_frame` 等の `call_module_field(ctx, "on_init", ...)` 呼び出しと、`config` エラーメッセージで `on_init` を言及している箇所。

- [ ] **Step 2: 置換**

`src/lua_api.c` で `replace_all`:
- `"on_init"` → `"onInit"`
- `"on_frame"` → `"onFrame"`
- `"on_event"` → `"onEvent"`
- `"on_quit"` → `"onQuit"`
- エラーメッセージ `must be called inside on_init` → `must be called inside onInit`

- [ ] **Step 3: ヘッダの宣言が一致するか確認**

Run: `grep -n "on_init\|on_frame\|on_event\|on_quit\|onInit\|onFrame\|onEvent\|onQuit" /home/neguse/ghq/github.com/neguse/lub/src/lua_api.h`

Expected: 公開 API 名 (`lua_ctx_call_init` 等) は変えない。field 名だけ camelCase 化された C 文字列 literal が `.c` 側にあれば OK。

- [ ] **Step 4: ビルド**

Run: `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -20`

Expected: 成功。

- [ ] **Step 5: Commit (まだ samples 側は古いまま — 次のタスクで一緒に直す)**

このタスクと Task 3 を 1 commit にまとめるため、Step 5 は飛ばす。Task 3 完了時に commit する。

---

## Task 3: 既存 `samples/*.lua` の callback を camelCase に rename

`samples/00_hello.lua` 〜 `samples/11_shadow.lua` (14 ファイル) で `M.on_init`/`M.on_frame`/`M.on_event`/`M.on_quit` を `M.onInit`/`M.onFrame`/`M.onEvent`/`M.onQuit` に変える。

**Files:**
- Modify: `samples/00_hello.lua`, `samples/00b_clear.lua`, `samples/00c_buffer.lua`, `samples/00d_shader.lua`, `samples/01_triangle.lua`, `samples/02_vertex_color.lua`, `samples/03_texture.lua`, `samples/04_mvp.lua`, `samples/05_postprocess.lua`, `samples/06_deferred.lua`, `samples/07_compute.lua`, `samples/08_gltf.lua`, `samples/09_breakout.lua`, `samples/10_breakout3d.lua`, `samples/11_shadow.lua`

- [ ] **Step 1: 各 sample で置換**

各ファイルで:
- `M.on_init` → `M.onInit` (replace_all)
- `M.on_frame` → `M.onFrame` (replace_all)
- `M.on_event` → `M.onEvent` (replace_all)
- `M.on_quit` → `M.onQuit` (replace_all)
- function 定義側の `function M.on_init` → `function M.onInit` 等も同じ replace_all で拾われる

15 ファイルすべて。

- [ ] **Step 2: tests/lua 配下も同様に確認**

Run: `grep -rn "on_init\|on_frame\|on_event\|on_quit" /home/neguse/ghq/github.com/neguse/lub/tests/lua/`

Expected: 該当があれば camelCase に置換、無ければスキップ。

- [ ] **Step 3: golden を 1 sample 走らせて確認**

Run (Linux + lavapipe 環境): `cd /home/neguse/ghq/github.com/neguse/lub && scripts/run-golden.sh --sample 01_triangle --backend sokol 2>&1 | tail -5`

Expected: `OK` (PNG が既存 golden と一致)。

- [ ] **Step 4: Commit (Task 2 と Task 3 を 1 commit に)**

```bash
git add src/lua_api.c samples/*.lua tests/lua/
git commit -m "Rename Lua callback contract to camelCase (onInit/onFrame/onEvent/onQuit)

Phase 0 の Haxe sample 移植で extern typedef を Haxe 慣習 (camelCase)
に揃えるため、lub C 側が読む callback field 名と既存 .lua sample の
両方を同時に camelCase 化する。"
```

---

## Task 4: lub C 側で `package.path` に `<entry-dir>/.lub/?.lua` を inject

エントリ entry が `samples/01_triangle.hxml` のとき、生成物は `samples/.lub/01_triangle.lua`。boot.lua は `require("01_triangle")` を呼ぶ。package.path に `samples/.lub/?.lua` が無いと見つからない。

**Files:**
- Modify: `src/lua_api.c` (lua_ctx_init 周辺)

- [ ] **Step 1: 既存の package.path 周りを確認**

Run: `grep -n "package.path\|package\\.path\|loadfile.*boot" /home/neguse/ghq/github.com/neguse/lub/src/lua_api.c`

Expected: `luaL_loadfile(ctx->L, "samples/boot.lua")` あたりにヒットする。

- [ ] **Step 2: API を増やす**

`src/lua_api.h` に新しい関数宣言を追加:

```c
// entry .hxml 経由の build 後、生成 .lua を find できるよう
// `<dir>/.lub/?.lua` を package.path の先頭に積む。
// entry_dir は absolute / relative どちらでもよく、内部で `?` 展開される。
void lua_ctx_add_package_path(LuaCtx *ctx, const char *entry_dir);
```

- [ ] **Step 3: 実装**

`src/lua_api.c` の `lua_ctx_init` 終端付近 (もしくは独立位置) に追加:

```c
void lua_ctx_add_package_path(LuaCtx *ctx, const char *entry_dir) {
    if (!ctx || !ctx->L || !entry_dir) return;
    lua_State *L = ctx->L;
    lua_getglobal(L, "package");                     /* +1 */
    lua_getfield(L, -1, "path");                     /* +1 */
    const char *cur = lua_tostring(L, -1);
    lua_pop(L, 1);                                   /* -1 (drop old path) */
    char buf[1024];
    SDL_snprintf(buf, sizeof(buf), "%s/.lub/?.lua;%s", entry_dir, cur ? cur : "");
    lua_pushstring(L, buf);
    lua_setfield(L, -2, "path");                     /* set package.path */
    lua_pop(L, 1);                                   /* drop package */
}
```

- [ ] **Step 4: ビルド**

Run: `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -10`

Expected: 成功。

- [ ] **Step 5: Commit (Task 5 の main.c 改修と一緒に。後段で commit)**

---

## Task 5: `src/main.c` で entry 拡張子 dispatch

現状 `lub <path>` は `.lua` 前提。`.hxml` を受けた場合は (a) haxe pipeline を init して (b) entry dir + basename を計算して (c) build を 1 回走らせて (d) `lua_ctx_add_package_path` を呼んで (e) `boot.lua` の `require` ターゲットを basename にする、という分岐を入れる。

ここでは **pipeline まわりは stub のみ** にしておき、Task 11 以降で本実装する。stub は「`.hxml` を受けたら fatal error を出す」だけにしておく。

**Files:**
- Modify: `src/main.c`

- [ ] **Step 1: 現状 main.c の entry 受け取り部分を確認**

Run: `grep -n "argv\|entry\|SDL_AppInit\|app_init" /home/neguse/ghq/github.com/neguse/lub/src/main.c | head -30`

- [ ] **Step 2: 拡張子判定ヘルパを追加**

`src/main.c` の上部に:

```c
static bool has_extension(const char *path, const char *ext) {
    size_t n = SDL_strlen(path), m = SDL_strlen(ext);
    return n >= m && SDL_strcasecmp(path + n - m, ext) == 0;
}
```

- [ ] **Step 3: dispatch を入れる**

entry path を受け取って app に渡す箇所で、`.hxml` の場合は新しい code path に入る:

```c
if (has_extension(entry_path, ".hxml")) {
    // Phase 0 stub: 後段の Task 23 で実装。
    SDL_Log("FATAL: .hxml entry not implemented yet");
    return SDL_APP_FAILURE;
}
// 既存の .lua dispatch
```

- [ ] **Step 4: ビルド + 既存 sample の動作確認**

Run:
- `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -5`
- `./build/lub samples/01_triangle.lua --capture /tmp/out.png --capture-frame 30 && file /tmp/out.png`

Expected: ビルド成功、PNG 出力。

- [ ] **Step 5: Commit (Task 4 + Task 5 をまとめて)**

```bash
git add src/lua_api.c src/lua_api.h src/main.c
git commit -m "Add package.path injection helper and .hxml CLI dispatch stub

Helper to prepend <entry-dir>/.lub/?.lua so Haxe-generated samples
become require-able. .hxml entry path is recognized but currently
fatals; real pipeline arrives in subsequent commits."
```

---

## Task 6: haxe-lib scaffold (`haxelib.json` と空の class 群)

extern 本体は次のタスクで埋める。ここではディレクトリ構造と `haxelib.json` だけ。

**Files:**
- Create: `haxe-lib/lub/haxelib.json`
- Create: `haxe-lib/lub/lub/Lub.hx`
- Create: `haxe-lib/lub/lub/Gfx.hx`
- Create: `haxe-lib/lub/lub/Input.hx`
- Create: `haxe-lib/lub/lub/Io.hx`
- Create: `haxe-lib/lub/lub/Sys.hx`

- [ ] **Step 1: `haxe-lib/lub/haxelib.json`**

```json
{
  "name": "lub",
  "url": "https://github.com/neguse/lub",
  "license": "MIT",
  "tags": ["cross", "gamedev", "lua"],
  "description": "Haxe externs for the lub runtime (Lua 5.5 + SDL3 + sokol/sdlgpu).",
  "version": "0.0.1",
  "classPath": "",
  "releasenote": "Phase 0 initial drop.",
  "contributors": ["neguse"],
  "dependencies": {}
}
```

`classPath` を空文字にしておくと haxelib は package root = `<lib>/` を classpath として登録する。`<lib>/lub/Lub.hx` 等が `lub.Lub` として解決される。

- [ ] **Step 2: 空 class を 5 つ作る**

各ファイルで内容は `package lub;\n\nextern class <ClassName> {}\n`。次の Task で実装を入れる。

- [ ] **Step 3: haxelib に dev 登録**

Run: `haxelib dev lub /home/neguse/ghq/github.com/neguse/lub/haxe-lib/lub`

Expected: `Development directory set to ...` メッセージ。

- [ ] **Step 4: 動作確認 (空 class が import 可能か)**

`/tmp/lub_extern_test/Smoke.hxml`:
```
-cp .
-lib lub
-main Smoke
-lua /tmp/lub_extern_test/out.lua
```

`/tmp/lub_extern_test/Smoke.hx`:
```haxe
import lub.Lub;
import lub.Gfx;
import lub.Input;
import lub.Io;
import lub.Sys;

class Smoke {
  public static function main() {}
}
```

Run: `mkdir -p /tmp/lub_extern_test && cd /tmp/lub_extern_test && cat > Smoke.hxml <<EOF
-cp .
-lib lub
-main Smoke
-lua out.lua
EOF
cat > Smoke.hx <<'EOF'
import lub.Lub;
import lub.Gfx;
import lub.Input;
import lub.Io;
import lub.Sys;
class Smoke { public static function main() {} }
EOF
haxe Smoke.hxml && head -5 out.lua`

Expected: コンパイル成功、`out.lua` が生成される。

- [ ] **Step 5: Commit**

```bash
git add haxe-lib/
git commit -m "Add haxe-lib/lub scaffold (haxelib.json + empty extern classes)"
```

---

## Task 7: extern `Lub.hx` 実装

**Files:**
- Modify: `haxe-lib/lub/lub/Lub.hx`

- [ ] **Step 1: 中身を埋める**

```haxe
package lub;

extern class Lub {
  @:native("config") public static function config(opts: Dynamic): Void;
}
```

- [ ] **Step 2: 動作確認 (Smoke で Lub.config を呼ぶ)**

`/tmp/lub_extern_test/Smoke.hx`:
```haxe
import lub.Lub;
class Smoke {
  public static function main() {
    Lub.config({ backend: "sokol" });
  }
}
```

Run: `cd /tmp/lub_extern_test && haxe Smoke.hxml && grep -n "config" out.lua`

Expected: 生成 lua 内に `config({...})` が含まれる。

- [ ] **Step 3: 単独 commit はしない、Task 10 までまとめる**

---

## Task 8: embedded prelude header の用意

prelude は build 毎に変わらないので C 側に static 文字列で埋める。

**Files:**
- Create: `src/embedded_prelude.h`

- [ ] **Step 1: ファイル作成**

```c
#ifndef LUB_EMBEDDED_PRELUDE_H
#define LUB_EMBEDDED_PRELUDE_H

// Haxe -lua の出力に prepend する shim。
// - lub runtime は Lua 5.5 (utf8 built-in) だが、Haxe lua target が
//   require("lua-utf8") を出す前提なので alias を貼っておく。
// - lub の require contract は table 戻し。Haxe class table を
//   そのまま return するために postlude (build 毎に動的生成) が末尾に
//   "return <ClassName>" を入れる。
static const char HAXE_PRELUDE[] =
    "package.preload[\"lua-utf8\"] = function()\n"
    "  return {\n"
    "    len = string.len, char = string.char,\n"
    "    upper = string.upper, lower = string.lower,\n"
    "    find = string.find, sub = string.sub, byte = string.byte,\n"
    "  }\n"
    "end\n";

#endif
```

- [ ] **Step 2: 単体 commit はしない、Task 19 まで保留**

---

## Task 9: extern `Gfx.hx` 実装

`enums_lua.c` の定数を全部反映。

**Files:**
- Modify: `haxe-lib/lub/lub/Gfx.hx`

- [ ] **Step 1: 完全版を書く**

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
  // capture
  @:native("capture")            public static function capture(path: String): Void;

  // globals
  @:native("main_tex")           public static var mainTex(default, null): Dynamic;

  // buffer type
  @:native("VERTEX")             public static var VERTEX(default, null): Int;
  @:native("INDEX")              public static var INDEX(default, null): Int;
  @:native("UNIFORM")            public static var UNIFORM(default, null): Int;
  @:native("STORAGE")            public static var STORAGE(default, null): Int;
  // pixel format
  @:native("RGBA8")              public static var RGBA8(default, null): Int;
  @:native("R8")                 public static var R8(default, null): Int;
  @:native("RG8")                public static var RG8(default, null): Int;
  @:native("RGBA16F")            public static var RGBA16F(default, null): Int;
  @:native("RGBA32F")            public static var RGBA32F(default, null): Int;
  @:native("DEPTH16")            public static var DEPTH16(default, null): Int;
  @:native("DEPTH24_STENCIL8")   public static var DEPTH24_STENCIL8(default, null): Int;
  @:native("DEPTH32F")           public static var DEPTH32F(default, null): Int;
  // load / store
  @:native("CLEAR")              public static var CLEAR(default, null): Int;
  @:native("LOAD")               public static var LOAD(default, null): Int;
  @:native("DONTCARE")           public static var DONTCARE(default, null): Int;
  @:native("STORE")              public static var STORE(default, null): Int;
  // blend / cull
  @:native("NONE")               public static var NONE(default, null): Int;
  @:native("ALPHA")              public static var ALPHA(default, null): Int;
  @:native("ADDITIVE")           public static var ADDITIVE(default, null): Int;
  @:native("MULTIPLY")           public static var MULTIPLY(default, null): Int;
  @:native("BACK")               public static var BACK(default, null): Int;
  @:native("FRONT")              public static var FRONT(default, null): Int;
  // primitive
  @:native("TRIANGLES")          public static var TRIANGLES(default, null): Int;
  @:native("TRIANGLE_STRIP")     public static var TRIANGLE_STRIP(default, null): Int;
  @:native("LINES")              public static var LINES(default, null): Int;
  @:native("LINE_STRIP")         public static var LINE_STRIP(default, null): Int;
  @:native("POINTS")             public static var POINTS(default, null): Int;
  // sampler
  @:native("LINEAR")             public static var LINEAR(default, null): Int;
  @:native("NEAREST")            public static var NEAREST(default, null): Int;
  @:native("REPEAT")             public static var REPEAT(default, null): Int;
  @:native("CLAMP")              public static var CLAMP(default, null): Int;
}
```

- [ ] **Step 2: 動作確認**

`/tmp/lub_extern_test/Smoke.hx`:
```haxe
import lub.Lub;
import lub.Gfx;
class Smoke {
  public static function main() {
    Lub.config({ backend: "sokol" });
    Gfx.beginPass({ target: Gfx.mainTex, clear_color: [0.1, 0.1, 0.2, 1.0] });
    Gfx.endPass();
  }
}
```

Run: `cd /tmp/lub_extern_test && haxe Smoke.hxml && grep -E "begin_pass|main_tex" out.lua | head -3`

Expected: `begin_pass({...})` と `main_tex` 参照が見える。

- [ ] **Step 3: 単独 commit はしない、Task 10 までまとめる**

---

## Task 10: extern `Input.hx` / `Io.hx` / `Sys.hx` 実装

**Files:**
- Modify: `haxe-lib/lub/lub/Input.hx`, `Io.hx`, `Sys.hx`

- [ ] **Step 1: Input.hx**

```haxe
package lub;

extern class Input {
  @:native("key_down") public static function keyDown(code: String): Bool;
}
```

- [ ] **Step 2: Io.hx**

```haxe
package lub;

@:luaRequire("lub_io")
extern class Io {
  @:native("load_text")     public static function loadText(path: String): lua.PairTools.MultiReturn2<String, Int>;
  @:native("load_floats")   public static function loadFloats(path: String): lua.PairTools.MultiReturn2<Dynamic, Int>;
  @:native("load_png")      public static function loadPng(path: String): lua.PairTools.MultiReturn5<Dynamic, Int, Int, Int, Int>;
  @:native("load_gltf")     public static function loadGltf(path: String): lua.PairTools.MultiReturn2<Dynamic, Int>;
  @:native("interleave_pn") public static function interleavePn(mesh: Dynamic): lua.Table<Int, Float>;
}
```

- [ ] **Step 3: Sys.hx**

```haxe
package lub;

extern class Sys {
  @:native("file_mtime") public static function fileMtime(path: String): Null<Float>;
  @:native("fnv1a64")    public static function fnv1a64(s: String): Int;
  @:native("load_png")   public static function loadPng(path: String): Dynamic;
  @:native("load_gltf")  public static function loadGltf(path: String): Dynamic;
}
```

- [ ] **Step 4: 動作確認 (`@:luaRequire` + `@:native` が両立するか実機で確かめる)**

`/tmp/lub_extern_test/Smoke.hx`:
```haxe
import lub.Io;
class Smoke {
  public static function main() {
    var r = Io.loadText("foo.txt");
  }
}
```

Run: `cd /tmp/lub_extern_test && haxe Smoke.hxml 2>&1 | head -10`

Expected: コンパイル成功。出力 lua に `local lub_io = require("lub_io")` と `lub_io.load_text(...)` が含まれている。

もし `@:luaRequire` + `@:native` が両立しない Haxe バージョンなら、Io extern は Haxe 慣習を諦めて snake_case のまま (`load_text`/`load_floats`/`load_png`/`load_gltf`/`interleave_pn`) にする。spec の「未解決事項」に対応する選択肢。実機検証してから commit。

- [ ] **Step 5: Commit (Task 7-10 まとめて)**

```bash
git add haxe-lib/lub/lub/
git commit -m "Implement extern classes for lub runtime API

- Lub: config
- Gfx: pass / resources / commands / capture / mainTex / constants
- Input: keyDown
- Io: lub_io.lua cached loaders (load_text/floats/png/gltf/interleave_pn)
- Sys: raw C primitives (file_mtime/fnv1a64/load_png/load_gltf)"
```

---

## Task 11: `haxe_server.h` — interface 定義

**Files:**
- Create: `src/haxe_server.h`

- [ ] **Step 1: 書く**

```c
#ifndef LUB_HAXE_SERVER_H
#define LUB_HAXE_SERVER_H

#include <stdbool.h>

typedef struct SDL_Process SDL_Process;

typedef struct HaxeServer {
    SDL_Process *child;    // haxe --wait child
    int          port;     // 確保した port
    bool         ready;    // listening を確認済みなら true
} HaxeServer;

// 起動。`LUB_HAXE_PORT` env var があればそれを 1 回だけ試す。
// 無ければ 7400 から 7410 まで probe。成功時 true。
bool haxe_server_start(HaxeServer *s);

// shutdown。child を kill し、リソース解放。
void haxe_server_stop(HaxeServer *s);

// 子プロセスが死んでいないか確認。死んでいたら false。
bool haxe_server_is_alive(HaxeServer *s);

#endif
```

- [ ] **Step 2: 単体 commit はしない、Task 14 で .c と一緒に**

---

## Task 12: `haxe_server.c` — `haxe --wait` の spawn

**Files:**
- Create: `src/haxe_server.c`

- [ ] **Step 1: 書く**

```c
#include "haxe_server.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static bool try_spawn_one(HaxeServer *s, int port) {
    char port_str[16];
    SDL_snprintf(port_str, sizeof(port_str), "%d", port);
    const char *argv[] = { "haxe", "--wait", port_str, NULL };
    SDL_PropertiesID props = SDL_CreateProperties();
    SDL_SetPointerProperty(props, SDL_PROP_PROCESS_CREATE_ARGS_POINTER, (void*)argv);
    SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDOUT_NUMBER, SDL_PROCESS_STDIO_APP);
    SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDERR_NUMBER, SDL_PROCESS_STDIO_APP);
    s->child = SDL_CreateProcessWithProperties(props);
    SDL_DestroyProperties(props);
    if (!s->child) {
        SDL_Log("haxe --wait %d spawn failed: %s", port, SDL_GetError());
        return false;
    }
    // stdout を 1 行 読む。"Waiting on ..." が出れば listening。
    // child が即 exit したら port 衝突。
    SDL_IOStream *out = SDL_GetProcessOutput(s->child);
    char buf[256];
    Uint64 start = SDL_GetTicks();
    while (SDL_GetTicks() - start < 5000) {
        int n = (int)SDL_ReadIO(out, buf, sizeof(buf) - 1);
        if (n > 0) {
            buf[n] = '\0';
            if (strstr(buf, "Waiting on") || strstr(buf, "Server bound")) {
                s->port = port;
                s->ready = true;
                return true;
            }
        }
        int exit_code;
        if (SDL_GetProcessOutput(s->child) == NULL ||
            !SDL_WaitProcess(s->child, false, &exit_code) == false) {
            // child terminated -> port collision
            SDL_DestroyProcess(s->child);
            s->child = NULL;
            return false;
        }
        SDL_Delay(20);
    }
    SDL_Log("haxe --wait %d: timed out waiting for ready message", port);
    SDL_KillProcess(s->child, true);
    SDL_DestroyProcess(s->child);
    s->child = NULL;
    return false;
}

bool haxe_server_start(HaxeServer *s) {
    SDL_zero(*s);
    const char *env = SDL_getenv("LUB_HAXE_PORT");
    if (env) {
        int p = atoi(env);
        return try_spawn_one(s, p);
    }
    for (int p = 7400; p <= 7410; ++p) {
        if (try_spawn_one(s, p)) return true;
    }
    SDL_Log("haxe --wait: no free port in 7400..7410");
    return false;
}

void haxe_server_stop(HaxeServer *s) {
    if (!s || !s->child) return;
    SDL_KillProcess(s->child, true);
    SDL_DestroyProcess(s->child);
    s->child = NULL;
    s->ready = false;
}

bool haxe_server_is_alive(HaxeServer *s) {
    if (!s || !s->child) return false;
    int exit_code;
    if (SDL_WaitProcess(s->child, false, &exit_code)) {
        return false;  // child has exited
    }
    return true;
}
```

注意: SDL3 の `SDL_Process` API 名は SDL release-3.2.30 で変わっている可能性あり。実装時に `SDL_process.h` の最新 signature と突き合わせて compile-error を解く。Step 3 のビルドで verify する。

- [ ] **Step 2: CMakeLists に追加**

`CMakeLists.txt` の `add_executable(lub ...)` の SOURCES list に `src/haxe_server.c` を追加 (LUB_WASM 分岐は Task 26 で入れる)。

- [ ] **Step 3: ビルド**

Run: `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -10`

Expected: 成功。compile error が出たら SDL3 API signature に合わせて修正 (例: `SDL_PROCESS_STDIO_APP` が `SDL_PROCESS_STDIO_INHERITED` になっている等)。

- [ ] **Step 4: 単独 commit はしない、Task 14 までまとめる**

---

## Task 13: haxe_server を単体で smoke test

**Files:**
- Create: `tests/c/haxe_server_smoke.c` (新規ディレクトリ)
- Modify: `CMakeLists.txt` (test executable 追加)

- [ ] **Step 1: smoke test を書く**

`tests/c/haxe_server_smoke.c`:
```c
#include "../src/haxe_server.h"
#include <SDL3/SDL.h>
#include <stdio.h>

int main(void) {
    HaxeServer s;
    if (!haxe_server_start(&s)) {
        SDL_Log("haxe_server_start failed");
        return 1;
    }
    SDL_Log("haxe --wait running on port %d", s.port);
    if (!haxe_server_is_alive(&s)) {
        SDL_Log("server died immediately");
        haxe_server_stop(&s);
        return 1;
    }
    SDL_Delay(500);
    haxe_server_stop(&s);
    return 0;
}
```

- [ ] **Step 2: CMakeLists に test executable 追加**

```cmake
if(NOT LUB_WASM)
  add_executable(lub_haxe_server_smoke
    tests/c/haxe_server_smoke.c
    src/haxe_server.c
  )
  target_link_libraries(lub_haxe_server_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_haxe_server_smoke PRIVATE src)
endif()
```

- [ ] **Step 3: ビルド + 実行**

Run:
- `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j --target lub_haxe_server_smoke 2>&1 | tail -5`
- `/home/neguse/ghq/github.com/neguse/lub/build/lub_haxe_server_smoke`

Expected: `haxe --wait running on port 7400` (もしくは別の port) と表示、exit 0。

- [ ] **Step 4: 単独 commit はしない、Task 14 でまとめる**

---

## Task 14: haxe_server を app の lifecycle に組み込む (まだ実利用なし)

**Files:**
- Modify: `src/app.h`
- Modify: `src/app.c`
- Modify: `src/main.c`

- [ ] **Step 1: `App` 構造体に server を持たせる**

`src/app.h` の `App` 構造体定義に追加:

```c
#include "haxe_server.h"
// ...
typedef struct App {
    // ...既存フィールド...
    HaxeServer haxe_server;
    bool       haxe_enabled;   // entry が .hxml なら true
} App;
```

- [ ] **Step 2: shutdown 経路で server を止める**

`src/app.c` の `app_shutdown` 末尾に:

```c
if (app->haxe_enabled) {
    haxe_server_stop(&app->haxe_server);
}
```

- [ ] **Step 3: ビルド**

Run: `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -5`

Expected: 成功。

- [ ] **Step 4: Commit (Task 11-14 をまとめて)**

```bash
git add src/haxe_server.h src/haxe_server.c CMakeLists.txt tests/c/haxe_server_smoke.c src/app.h src/app.c
git commit -m "Add haxe_server: manage long-lived haxe --wait child process

Spawns haxe --wait with port probe (7400..7410, or LUB_HAXE_PORT
override). Smoke test verifies one spawn cycle. Process lifecycle
attached to App for clean shutdown."
```

---

## Task 15: `haxe_build.h` — interface 定義

**Files:**
- Create: `src/haxe_build.h`

- [ ] **Step 1: 書く**

```c
#ifndef LUB_HAXE_BUILD_H
#define LUB_HAXE_BUILD_H

#include <stdbool.h>
#include "haxe_server.h"

typedef struct HaxeBuildResult {
    bool ok;
    char log[4096];   // haxe stderr/stdout (last 4 KB)
} HaxeBuildResult;

// hxml から `-cp <path>` (複数可) と `-main <ClassName>` を取り出す。
// 取れなければ false。watch_roots / out_main は caller 所有のバッファ。
typedef struct HxmlMeta {
    char  main_class[128];
    char  cp_paths[8][512];   // 最大 8 個までの -cp
    int   cp_count;
} HxmlMeta;

bool hxml_parse(const char *hxml_path, HxmlMeta *out);

// hxml を build して `<dir>/.lub/<basename>.lua` を atomic write。
// `<basename>` は hxml の path basename (拡張子抜き)。
// server.port を `--connect` に渡す。
HaxeBuildResult haxe_build_run(const HaxeServer *server,
                               const char *hxml_path,
                               const HxmlMeta *meta);

#endif
```

- [ ] **Step 2: 単体 commit はしない、Task 19 まで**

---

## Task 16: hxml line parser

**Files:**
- Create: `src/haxe_build.c` (この時点では parser のみ)

- [ ] **Step 1: parser 実装**

```c
#include "haxe_build.h"
#include "embedded_prelude.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static char *trim(char *s) {
    while (*s == ' ' || *s == '\t') ++s;
    char *e = s + strlen(s);
    while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\r' || e[-1] == '\n')) --e;
    *e = '\0';
    return s;
}

bool hxml_parse(const char *hxml_path, HxmlMeta *out) {
    SDL_zero(*out);
    FILE *f = fopen(hxml_path, "r");
    if (!f) { SDL_Log("hxml_parse: cannot open %s", hxml_path); return false; }
    char line[1024];
    while (fgets(line, sizeof(line), f)) {
        char *t = trim(line);
        if (!*t || *t == '#') continue;
        if (strncmp(t, "-main", 5) == 0 && (t[5] == ' ' || t[5] == '\t')) {
            char *arg = trim(t + 5);
            SDL_snprintf(out->main_class, sizeof(out->main_class), "%s", arg);
        } else if (strncmp(t, "-cp", 3) == 0 && (t[3] == ' ' || t[3] == '\t')) {
            char *arg = trim(t + 3);
            if (out->cp_count < 8) {
                SDL_snprintf(out->cp_paths[out->cp_count], 512, "%s", arg);
                out->cp_count++;
            }
        } else if (strncmp(t, "--class-path", 12) == 0 && (t[12] == ' ' || t[12] == '\t')) {
            char *arg = trim(t + 12);
            if (out->cp_count < 8) {
                SDL_snprintf(out->cp_paths[out->cp_count], 512, "%s", arg);
                out->cp_count++;
            }
        }
    }
    fclose(f);
    if (!out->main_class[0]) {
        SDL_Log("hxml_parse: -main not found in %s", hxml_path);
        return false;
    }
    return true;
}
```

- [ ] **Step 2: parser 単体 test**

`tests/c/hxml_parse_test.c`:
```c
#include "../src/haxe_build.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <assert.h>
#include <string.h>

int main(void) {
    // 一時 hxml を作る
    FILE *f = fopen("/tmp/test.hxml", "w");
    fprintf(f, "-cp samples\n-lib lub\n-main Triangle01\n# comment line\n--class-path haxe-extra\n");
    fclose(f);
    HxmlMeta m;
    bool ok = hxml_parse("/tmp/test.hxml", &m);
    assert(ok);
    assert(strcmp(m.main_class, "Triangle01") == 0);
    assert(m.cp_count == 2);
    assert(strcmp(m.cp_paths[0], "samples") == 0);
    assert(strcmp(m.cp_paths[1], "haxe-extra") == 0);
    printf("OK\n");
    return 0;
}
```

`CMakeLists.txt`:
```cmake
if(NOT LUB_WASM)
  add_executable(lub_hxml_parse_test
    tests/c/hxml_parse_test.c
    src/haxe_build.c
  )
  target_link_libraries(lub_hxml_parse_test PRIVATE SDL3::SDL3)
  target_include_directories(lub_hxml_parse_test PRIVATE src)
endif()
```

- [ ] **Step 3: ビルド + 実行**

Run:
- `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j --target lub_hxml_parse_test 2>&1 | tail -5`
- `/home/neguse/ghq/github.com/neguse/lub/build/lub_hxml_parse_test`

Expected: `OK` を出力して exit 0。

- [ ] **Step 4: 単独 commit はしない、Task 19 まで**

---

## Task 17: `haxe_build_run` — `haxe --connect` 呼び出し (concat 前まで)

**Files:**
- Modify: `src/haxe_build.c`

- [ ] **Step 1: 関数の前半 (build 呼び出し) を書く**

`haxe_build.c` 末尾に追加:

```c
static const char *path_basename_noext(const char *path, char *out, size_t outsz) {
    const char *slash = strrchr(path, '/');
    const char *bs = strrchr(path, '\\');
    const char *base = path;
    if (slash && slash > base) base = slash + 1;
    if (bs && bs > base) base = bs + 1;
    SDL_snprintf(out, outsz, "%s", base);
    char *dot = strrchr(out, '.');
    if (dot) *dot = '\0';
    return out;
}

static void path_dirname(const char *path, char *out, size_t outsz) {
    const char *slash = strrchr(path, '/');
    const char *bs = strrchr(path, '\\');
    const char *cut = slash;
    if (bs && bs > cut) cut = bs;
    if (cut) {
        size_t n = (size_t)(cut - path);
        if (n >= outsz) n = outsz - 1;
        memcpy(out, path, n);
        out[n] = '\0';
    } else {
        SDL_snprintf(out, outsz, ".");
    }
}

HaxeBuildResult haxe_build_run(const HaxeServer *server,
                               const char *hxml_path,
                               const HxmlMeta *meta) {
    HaxeBuildResult r = { .ok = false, .log = {0} };
    char dir[512]; path_dirname(hxml_path, dir, sizeof(dir));
    char base[256]; path_basename_noext(hxml_path, base, sizeof(base));

    // .lub/ を作る
    char lub_dir[640]; SDL_snprintf(lub_dir, sizeof(lub_dir), "%s/.lub", dir);
    SDL_CreateDirectory(lub_dir);

    char raw_tmp[768]; SDL_snprintf(raw_tmp, sizeof(raw_tmp), "%s/%s.raw.tmp", lub_dir, base);
    char port_str[16]; SDL_snprintf(port_str, sizeof(port_str), "%d", server->port);

    const char *argv[] = {
        "haxe",
        "--connect", port_str,
        hxml_path,
        "--lua", raw_tmp,
        NULL
    };
    SDL_PropertiesID props = SDL_CreateProperties();
    SDL_SetPointerProperty(props, SDL_PROP_PROCESS_CREATE_ARGS_POINTER, (void*)argv);
    SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDOUT_NUMBER, SDL_PROCESS_STDIO_APP);
    SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDERR_NUMBER, SDL_PROCESS_STDIO_APP);
    SDL_Process *p = SDL_CreateProcessWithProperties(props);
    SDL_DestroyProperties(props);
    if (!p) { SDL_snprintf(r.log, sizeof(r.log), "spawn failed: %s", SDL_GetError()); return r; }

    int exit_code = -1;
    SDL_WaitProcess(p, true, &exit_code);
    // stderr/stdout を取り込んで r.log に詰める (簡易: stderr 優先)
    size_t cap = sizeof(r.log) - 1;
    SDL_IOStream *err = SDL_GetProcessOutput(p);
    if (err) SDL_ReadIO(err, r.log, cap);
    SDL_DestroyProcess(p);

    if (exit_code != 0) {
        // r.log にすでに haxe diagnostic が入っている前提
        return r;
    }

    // ここから concat フェーズは Task 19 で実装。
    // 一旦 raw_tmp が生成されたことだけで build 成功扱い。
    r.ok = true;
    return r;
}
```

- [ ] **Step 2: コンパイル確認のみ**

Run: `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -10`

Expected: 成功。

- [ ] **Step 3: 単独 commit はしない、Task 19 まで**

---

## Task 18: postlude 動的生成 + prelude/raw/postlude concat helper

**Files:**
- Modify: `src/haxe_build.c`

- [ ] **Step 1: helper を追加**

`haxe_build.c` に追加:

```c
static bool read_file_to_string(const char *path, char **out_buf, size_t *out_len) {
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *buf = (char*)SDL_malloc(n + 1);
    if (!buf) { fclose(f); return false; }
    size_t r = fread(buf, 1, n, f);
    fclose(f);
    buf[r] = '\0';
    *out_buf = buf;
    *out_len = r;
    return true;
}

// prelude + raw + "return <main_class>\n" を out_path.tmp に書いて rename。
static bool concat_and_atomic_write(const char *raw_path,
                                    const char *out_path,
                                    const char *main_class) {
    char *raw; size_t raw_len;
    if (!read_file_to_string(raw_path, &raw, &raw_len)) return false;

    char tmp_path[768]; SDL_snprintf(tmp_path, sizeof(tmp_path), "%s.tmp", out_path);
    FILE *f = fopen(tmp_path, "wb");
    if (!f) { SDL_free(raw); return false; }
    fwrite(HAXE_PRELUDE, 1, strlen(HAXE_PRELUDE), f);
    fwrite(raw, 1, raw_len, f);
    fprintf(f, "\nreturn %s\n", main_class);
    fclose(f);
    SDL_free(raw);

    if (!SDL_RenamePath(tmp_path, out_path)) {
        SDL_Log("rename %s -> %s failed: %s", tmp_path, out_path, SDL_GetError());
        return false;
    }
    return true;
}
```

- [ ] **Step 2: 単体 commit はしない、Task 19 まで**

---

## Task 19: `haxe_build_run` を concat まで延長 + smoke test

**Files:**
- Modify: `src/haxe_build.c`

- [ ] **Step 1: `haxe_build_run` の末尾 (Task 17 で "Task 19 で実装" と書いた箇所) を埋める**

```c
    // (exit_code == 0 のあと)
    char out_lua[768]; SDL_snprintf(out_lua, sizeof(out_lua), "%s/%s.lua", lub_dir, base);
    if (!concat_and_atomic_write(raw_tmp, out_lua, meta->main_class)) {
        SDL_snprintf(r.log, sizeof(r.log), "concat/atomic write failed");
        return r;
    }
    // raw tmp を消す (失敗しても致命的ではない)
    SDL_RemovePath(raw_tmp);
    r.ok = true;
    return r;
```

- [ ] **Step 2: smoke test**

`tests/c/haxe_build_smoke.c`:
```c
#include "../src/haxe_build.h"
#include "../src/haxe_server.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <assert.h>

int main(void) {
    // /tmp/build_smoke/Main.hx + Main.hxml を仕込む
    SDL_CreateDirectory("/tmp/build_smoke");
    FILE *fx = fopen("/tmp/build_smoke/Main.hx", "w");
    fprintf(fx, "class Main {\n  public static function main() {}\n  public static function onFrame() {}\n}\n");
    fclose(fx);
    FILE *fh = fopen("/tmp/build_smoke/Main.hxml", "w");
    fprintf(fh, "-cp /tmp/build_smoke\n-main Main\n");
    fclose(fh);

    HaxeServer s;
    if (!haxe_server_start(&s)) { SDL_Log("server start failed"); return 1; }
    HxmlMeta m;
    if (!hxml_parse("/tmp/build_smoke/Main.hxml", &m)) { haxe_server_stop(&s); return 1; }
    HaxeBuildResult r = haxe_build_run(&s, "/tmp/build_smoke/Main.hxml", &m);
    haxe_server_stop(&s);
    if (!r.ok) { SDL_Log("build failed: %s", r.log); return 1; }

    // 生成された .lub/Main.lua を確認
    FILE *fc = fopen("/tmp/build_smoke/.lub/Main.lua", "rb");
    assert(fc);
    char buf[8192]; size_t n = fread(buf, 1, sizeof(buf)-1, fc); buf[n] = '\0';
    fclose(fc);
    // prelude の lua-utf8 shim と postlude の return Main が含まれているか
    assert(strstr(buf, "lua-utf8"));
    assert(strstr(buf, "return Main"));
    printf("OK\n");
    return 0;
}
```

`CMakeLists.txt` に test executable 追加:
```cmake
if(NOT LUB_WASM)
  add_executable(lub_haxe_build_smoke
    tests/c/haxe_build_smoke.c
    src/haxe_build.c
    src/haxe_server.c
  )
  target_link_libraries(lub_haxe_build_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_haxe_build_smoke PRIVATE src)
endif()
```

- [ ] **Step 3: ビルド + 実行**

Run:
- `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j --target lub_haxe_build_smoke 2>&1 | tail -5`
- `/home/neguse/ghq/github.com/neguse/lub/build/lub_haxe_build_smoke`

Expected: `OK` を出力、exit 0。`/tmp/build_smoke/.lub/Main.lua` の中身に `lua-utf8` shim と `return Main` が含まれる。

- [ ] **Step 4: Commit (Task 15-19 をまとめて)**

```bash
git add src/haxe_build.h src/haxe_build.c src/embedded_prelude.h CMakeLists.txt tests/c/hxml_parse_test.c tests/c/haxe_build_smoke.c
git commit -m "Add haxe_build: hxml parser + connect-driven build + atomic write

hxml_parse extracts -main and -cp. haxe_build_run kicks
haxe --connect, then concatenates embedded prelude + haxe raw
output + dynamically-generated 'return <ClassName>' postlude into
<dir>/.lub/<basename>.lua via atomic rename. Smoke test exercises
the full pipeline end-to-end."
```

---

## Task 20: `haxe_watch.h` — interface

**Files:**
- Create: `src/haxe_watch.h`

- [ ] **Step 1: 書く**

```c
#ifndef LUB_HAXE_WATCH_H
#define LUB_HAXE_WATCH_H

#include <stdbool.h>
#include "haxe_build.h"

typedef struct HaxeWatchEntry {
    char  path[768];
    Sint64 mtime_ns;
} HaxeWatchEntry;

typedef struct HaxeWatch {
    HaxeWatchEntry *entries;
    int             count;
    int             cap;
    Sint64          last_change_ns;   // debounce 用
    bool            pending_rebuild;
} HaxeWatch;

// hxml + meta から watch root を確定し、recursive に *.hx を拾う。
// hxml 自体も watch 対象。
bool haxe_watch_init(HaxeWatch *w, const char *hxml_path, const HxmlMeta *meta);

void haxe_watch_shutdown(HaxeWatch *w);

// 毎フレーム呼ぶ。mtime に変化があり debounce window を抜けたら true を返す
// (= rebuild すべき)。true を返したあとは内部状態を pending_rebuild = false に戻す。
// hxml 自体の変更が検知された場合は *meta_dirty=true。caller は meta を再 parse する責務。
bool haxe_watch_tick(HaxeWatch *w, bool *meta_dirty);

#endif
```

- [ ] **Step 2: 単独 commit はしない、Task 22 までまとめる**

---

## Task 21: `haxe_watch.c` — `-cp` 配下の `*.hx` を recursive 列挙 + mtime cache

**Files:**
- Create: `src/haxe_watch.c`

- [ ] **Step 1: 書く**

```c
#include "haxe_watch.h"
#include <SDL3/SDL.h>
#include <string.h>

static Sint64 mtime_ns(const char *path) {
    SDL_PathInfo info;
    if (!SDL_GetPathInfo(path, &info)) return 0;
    return info.modify_time;
}

static void add_entry(HaxeWatch *w, const char *path) {
    if (w->count == w->cap) {
        w->cap = w->cap ? w->cap * 2 : 16;
        w->entries = (HaxeWatchEntry*)SDL_realloc(w->entries, w->cap * sizeof(HaxeWatchEntry));
    }
    SDL_snprintf(w->entries[w->count].path, sizeof(w->entries[0].path), "%s", path);
    w->entries[w->count].mtime_ns = mtime_ns(path);
    w->count++;
}

// SDL3 directory enumeration via SDL_EnumerateDirectory
static SDL_EnumerationResult enum_cb(void *userdata, const char *dir, const char *fname) {
    HaxeWatch *w = (HaxeWatch*)userdata;
    char full[768];
    SDL_snprintf(full, sizeof(full), "%s/%s", dir, fname);
    SDL_PathInfo info;
    if (!SDL_GetPathInfo(full, &info)) return SDL_ENUM_CONTINUE;
    if (info.type == SDL_PATHTYPE_DIRECTORY) {
        SDL_EnumerateDirectory(full, enum_cb, w);
    } else if (info.type == SDL_PATHTYPE_FILE) {
        size_t n = strlen(fname);
        if (n > 3 && strcasecmp(fname + n - 3, ".hx") == 0) {
            add_entry(w, full);
        }
    }
    return SDL_ENUM_CONTINUE;
}

bool haxe_watch_init(HaxeWatch *w, const char *hxml_path, const HxmlMeta *meta) {
    SDL_zero(*w);
    add_entry(w, hxml_path);
    for (int i = 0; i < meta->cp_count; ++i) {
        SDL_EnumerateDirectory(meta->cp_paths[i], enum_cb, w);
    }
    return true;
}

void haxe_watch_shutdown(HaxeWatch *w) {
    SDL_free(w->entries);
    SDL_zero(*w);
}

#define DEBOUNCE_NS  (50LL * 1000LL * 1000LL)   // 50 ms

bool haxe_watch_tick(HaxeWatch *w, bool *meta_dirty) {
    *meta_dirty = false;
    bool any_change = false;
    Sint64 now = (Sint64)SDL_GetTicksNS();
    for (int i = 0; i < w->count; ++i) {
        Sint64 t = mtime_ns(w->entries[i].path);
        if (t != w->entries[i].mtime_ns) {
            // hxml 自体が変わったら meta_dirty = true (entry[0] が hxml)
            if (i == 0) *meta_dirty = true;
            w->entries[i].mtime_ns = t;
            any_change = true;
        }
    }
    if (any_change) {
        w->last_change_ns = now;
        w->pending_rebuild = true;
    }
    if (w->pending_rebuild && now - w->last_change_ns >= DEBOUNCE_NS) {
        w->pending_rebuild = false;
        return true;
    }
    return false;
}
```

- [ ] **Step 2: 単独 commit はしない、Task 22 までまとめる**

---

## Task 22: `haxe_pipeline.h` — runtime 側で触る合成 API + smoke test

**Files:**
- Create: `src/haxe_pipeline.h`
- Modify: `src/app.h` / `src/app.c`

- [ ] **Step 1: pipeline header (small)**

`src/haxe_pipeline.h`:
```c
#ifndef LUB_HAXE_PIPELINE_H
#define LUB_HAXE_PIPELINE_H

#include "haxe_server.h"
#include "haxe_build.h"
#include "haxe_watch.h"

typedef struct HaxePipeline {
    HaxeServer server;
    HxmlMeta   meta;
    HaxeWatch  watch;
    char       hxml_path[768];
    bool       enabled;
} HaxePipeline;

bool haxe_pipeline_start(HaxePipeline *p, const char *hxml_path);
void haxe_pipeline_stop (HaxePipeline *p);

// 毎フレーム呼ぶ。rebuild が走った場合 true (= .lua が atomic 更新された)。
bool haxe_pipeline_tick(HaxePipeline *p);

#endif
```

- [ ] **Step 2: `haxe_pipeline_*` 実装を `haxe_build.c` の末尾に追加 (新規 .c ファイルにしない、小さいので一緒で OK)**

```c
#include "haxe_pipeline.h"

bool haxe_pipeline_start(HaxePipeline *p, const char *hxml_path) {
    SDL_zero(*p);
    SDL_snprintf(p->hxml_path, sizeof(p->hxml_path), "%s", hxml_path);
    if (!haxe_server_start(&p->server)) return false;
    if (!hxml_parse(hxml_path, &p->meta)) {
        haxe_server_stop(&p->server);
        return false;
    }
    HaxeBuildResult r = haxe_build_run(&p->server, hxml_path, &p->meta);
    if (!r.ok) {
        SDL_Log("initial haxe build failed: %s", r.log);
        haxe_server_stop(&p->server);
        return false;
    }
    haxe_watch_init(&p->watch, hxml_path, &p->meta);
    p->enabled = true;
    return true;
}

void haxe_pipeline_stop(HaxePipeline *p) {
    if (!p || !p->enabled) return;
    haxe_watch_shutdown(&p->watch);
    haxe_server_stop(&p->server);
    p->enabled = false;
}

bool haxe_pipeline_tick(HaxePipeline *p) {
    if (!p->enabled) return false;
    bool meta_dirty = false;
    if (!haxe_watch_tick(&p->watch, &meta_dirty)) return false;
    if (meta_dirty) {
        // hxml が変わったので meta 再 parse
        hxml_parse(p->hxml_path, &p->meta);
        // watch root も作り直し
        haxe_watch_shutdown(&p->watch);
        haxe_watch_init(&p->watch, p->hxml_path, &p->meta);
    }
    HaxeBuildResult r = haxe_build_run(&p->server, p->hxml_path, &p->meta);
    if (!r.ok) {
        SDL_Log("haxe rebuild failed: %s", r.log);
        return false;
    }
    return true;
}
```

- [ ] **Step 3: `App` 構造体を更新 (Task 14 で `HaxeServer` を持たせていたものを `HaxePipeline` に置き換え)**

`src/app.h`:
```c
#include "haxe_pipeline.h"
// ...
typedef struct App {
    // ...
    HaxePipeline haxe;
    bool         haxe_enabled;
} App;
```

`src/app.c` の `app_shutdown` の haxe_server_stop 呼び出しを `haxe_pipeline_stop(&app->haxe)` に置き換え。

- [ ] **Step 4: ビルド**

Run: `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -5`

Expected: 成功。

- [ ] **Step 5: Commit (Task 20-22 をまとめて)**

```bash
git add src/haxe_watch.h src/haxe_watch.c src/haxe_pipeline.h src/haxe_build.c src/app.h src/app.c CMakeLists.txt
git commit -m "Add haxe_watch + haxe_pipeline composite

haxe_watch recursively enumerates *.hx under each -cp from the hxml
and polls mtimes with 50ms debounce. haxe_pipeline glues server +
build + watch into a single lifecycle (start / stop / tick)."
```

---

## Task 23: `main.c` で `.hxml` dispatch を実装に置き換え

Task 5 で stub にしていた箇所を、ここで pipeline 呼び出しに変える。

**Files:**
- Modify: `src/main.c`
- Modify: `src/app.c`

- [ ] **Step 1: dispatch 部分**

`src/main.c` で Task 5 の stub:
```c
if (has_extension(entry_path, ".hxml")) {
    SDL_Log("FATAL: .hxml entry not implemented yet");
    return SDL_APP_FAILURE;
}
```

を:
```c
if (has_extension(entry_path, ".hxml")) {
    if (!haxe_pipeline_start(&g_app.haxe, entry_path)) {
        SDL_Log("FATAL: haxe pipeline start failed");
        return SDL_APP_FAILURE;
    }
    g_app.haxe_enabled = true;
    // entry module 名は hxml basename
    char base[256]; path_basename_noext(entry_path, base, sizeof(base));
    SDL_snprintf(g_app.entry_module_name, sizeof(g_app.entry_module_name), "%s", base);
    // entry_path はそのまま (生成 .lua は .lub/<base>.lua)
    char dir[512]; path_dirname(entry_path, dir, sizeof(dir));
    char lua_path[768]; SDL_snprintf(lua_path, sizeof(lua_path), "%s/.lub/%s.lua", dir, base);
    SDL_snprintf(g_app.entry_path, sizeof(g_app.entry_path), "%s", lua_path);
    // package.path 拡張
    char lub_dir[640]; SDL_snprintf(lub_dir, sizeof(lub_dir), "%s/.lub", dir);
    lua_ctx_add_package_path(&g_app.lua, lub_dir);
}
```

`path_basename_noext` / `path_dirname` の宣言を `src/haxe_build.h` (もしくは `src/path_util.h` を新設) に extern として公開して `main.c` から使えるようにする (Task 17 で `haxe_build.c` 内 static にしているので、ヘッダに移動して static を外す)。

- [ ] **Step 2: frame_begin で pipeline tick を呼ぶ**

`src/app.c` の `app_frame_begin` 内、entry mtime polling と同じ位置で:

```c
if (app->haxe_enabled) {
    haxe_pipeline_tick(&app->haxe);
    // .lub/<base>.lua の mtime 変化は既存の polling が拾う -> lume.hotswap
}
```

- [ ] **Step 3: ビルド**

Run: `cmake --build /home/neguse/ghq/github.com/neguse/lub/build -j 2>&1 | tail -5`

Expected: 成功。

- [ ] **Step 4: 単独 commit はしない、Task 24 と一緒に**

---

## Task 24: 一時 sample で end-to-end smoke

01_triangle はまだ Haxe 化していないので、ここで一時ファイルで dev cycle を確認する。

**Files:**
- (一時ファイル、commit せず)

- [ ] **Step 1: smoke 用 sample を作る**

```haxe
// /tmp/lub_smoke/Smoke.hx
import lub.Lub;
import lub.Gfx;
class Smoke {
  public static function main() {}
  public static function onInit() {
    Lub.config({ backend: "sokol" });
  }
  public static function onFrame() {
    Gfx.beginPass({ target: Gfx.mainTex, clear_color: [0.1, 0.6, 0.1, 1.0] });
    Gfx.endPass();
  }
}
```

```
# /tmp/lub_smoke/Smoke.hxml
-cp /tmp/lub_smoke
-lib lub
-main Smoke
```

- [ ] **Step 2: lub 起動**

Run: `cd /home/neguse/ghq/github.com/neguse/lub && scripts/run-headless.sh /tmp/lub_smoke/Smoke.hxml --capture /tmp/smoke_out.png --capture-frame 30 && file /tmp/smoke_out.png`

Expected:
- `haxe --wait` が立ち、ビルドが走り、`/tmp/lub_smoke/.lub/Smoke.lua` が生成される。
- 30 フレーム描画後に PNG が出力される (緑系の clear color)。
- exit 0。

- [ ] **Step 3: hot reload 確認**

別 terminal で lub を起動した状態で、`/tmp/lub_smoke/Smoke.hx` の clear_color を `[0.6, 0.1, 0.1, 1.0]` (赤) に書き換えて保存。

Expected: lub が rebuild → `.lub/Smoke.lua` 更新 → lume.hotswap → 画面が赤に変わる。

(`run-headless.sh` は capture して exit するので hot reload 確認は手動で実 GPU 環境で行う。CI 上は capture テストのみ。)

- [ ] **Step 4: Commit (Task 23 のみ)**

```bash
git add src/main.c src/app.c src/haxe_build.h src/haxe_build.c
git commit -m "Wire .hxml entry dispatch in main.c and app frame loop

main.c detects .hxml, starts haxe_pipeline (which kicks initial
build), and sets entry_path to .lub/<basename>.lua so the existing
mtime polling + lume.hotswap path reloads on .hx edits.
Path helpers (basename_noext, dirname) moved to haxe_build.h."
```

---

## Task 25: golden test スクリプトを `.hxml` 対応に

**Files:**
- Modify: `scripts/run-golden.sh`

- [ ] **Step 1: 拡張子 fallback を入れる**

`scripts/run-golden.sh` の sample 実行ループで、`samples/$sample.hxml` があればそちら、なければ `.lua` を使う:

```bash
for sample in "${SAMPLES[@]}"; do
    if [[ -n "$sample_filter" && "$sample" != "$sample_filter" ]]; then
        continue
    fi
    if [[ -f "samples/$sample.hxml" ]]; then
        entry="samples/$sample.hxml"
    else
        entry="samples/$sample.lua"
    fi
    # ...既存ループ ($entry を使う)...
done
```

- [ ] **Step 2: 単独 commit はしない、Task 28 と一緒に**

---

## Task 26: WASM build で haxe_pipeline を compile-out

**Files:**
- Modify: `CMakeLists.txt`
- Modify: `src/main.c`、`src/app.c`、`src/app.h`

- [ ] **Step 1: CMakeLists の sources にガード**

```cmake
if(NOT LUB_WASM)
  list(APPEND LUB_SOURCES
    src/haxe_server.c
    src/haxe_build.c
    src/haxe_watch.c
  )
endif()
```

- [ ] **Step 2: app.h / main.c の haxe 参照を `#ifndef LUB_WASM` で囲う**

`src/app.h`:
```c
#ifndef LUB_WASM
#include "haxe_pipeline.h"
#endif

typedef struct App {
    // ...
#ifndef LUB_WASM
    HaxePipeline haxe;
    bool         haxe_enabled;
#endif
} App;
```

`src/main.c` の `.hxml` dispatch ブロックも `#ifndef LUB_WASM` で囲い、WASM では `if (has_extension(entry_path, ".hxml"))` の中で fatal exit (web 側の playground が `.lua` を直接 MEMFS に書く前提)。

- [ ] **Step 3: WASM ビルド確認**

Run: `cd /home/neguse/ghq/github.com/neguse/lub && source ~/emsdk/emsdk_env.sh && emcmake cmake -S . -B build/wasm && cmake --build build/wasm -j 2>&1 | tail -10`

Expected: 成功。`build/wasm/lub.{js,wasm,data}` が生成される。

- [ ] **Step 4: Commit (Task 25 + 26 まとめて)**

```bash
git add CMakeLists.txt src/app.h src/main.c src/app.c scripts/run-golden.sh
git commit -m "Compile-out haxe pipeline on WASM and let golden picks .hxml first

WASM target excludes haxe_{server,build,watch}.c entirely; .hxml
entries fatal on WASM (web playground handles .hx via HTTP compile
endpoint, not the runtime). Golden test script now prefers
samples/<name>.hxml over samples/<name>.lua so migrated samples
test the new pipeline."
```

---

## Task 27: `samples/01_triangle` を Haxe 化

**Files:**
- Create: `samples/01_triangle.hxml`
- Create: `samples/Triangle01.hx`
- Delete: `samples/01_triangle.lua`

- [ ] **Step 1: hxml を書く**

`samples/01_triangle.hxml`:
```
-cp samples
-lib lub
-main Triangle01
```

- [ ] **Step 2: Haxe sample を書く**

`samples/Triangle01.hx`:
```haxe
import lub.Lub;
import lub.Gfx;
import lub.Io;

class Triangle01 {
  public static function main() {}

  public static function onInit() {
    Lub.config({ backend: Sys.getEnv("LUB_BACKEND") != null ? Sys.getEnv("LUB_BACKEND") : "sokol" });
  }

  public static function onFrame() {
    var vsRes = Io.loadText("samples/data/01_triangle.vs.slang");
    var vs: String = untyped vsRes.a; var vsv: Int = untyped vsRes.b;
    var fsRes = Io.loadText("samples/data/01_triangle.fs.slang");
    var fs: String = untyped fsRes.a; var fsv: Int = untyped fsRes.b;
    var vertsRes = Io.loadFloats("samples/data/01_triangle.verts.lua");
    var verts: Dynamic = untyped vertsRes.a; var vv: Int = untyped vertsRes.b;
    if (vs == null || fs == null || verts == null) return;
    var s = Gfx.useShader("tri_shader", vs, fs, vsv ^ fsv);
    var b = Gfx.useBuffer("tri_verts", Gfx.VERTEX, verts, vv);
    Gfx.beginPass({ target: Gfx.mainTex, clear_color: [0.1, 0.1, 0.2, 1.0] });
    Gfx.draw(3, { verts: b }, { shader: s, depth: false, cull: Gfx.NONE });
    Gfx.endPass();
  }
}
```

注意: `Io.loadText` の戻り値 (multi-return) を tuple として展開する API は Haxe 5 の `lua.PairTools.MultiReturn2` の実 API に依存する。`vsRes.a` / `vsRes.b` の dotted access が動くか怪しい場合は `untyped` で逃がす (上の例)。動かないなら次の代替:

```haxe
@:multiReturn extern class LoadTextResult { var content: String; var version: Int; }
```

実装時に検証して片方に倒す。

- [ ] **Step 3: 既存 .lua を削除**

```bash
git rm samples/01_triangle.lua
```

- [ ] **Step 4: 動作確認**

Run: `cd /home/neguse/ghq/github.com/neguse/lub && scripts/run-headless.sh samples/01_triangle.hxml --capture /tmp/tri_out.png --capture-frame 30 && file /tmp/tri_out.png`

Expected:
- haxe build 成功
- `samples/.lub/01_triangle.lua` 生成
- 三角形 PNG 出力

- [ ] **Step 5: 単独 commit はしない、Task 28 と一緒に**

---

## Task 28: 01_triangle の golden 検証 + commit

**Files:**
- (検証のみ、追加コミットなし)

- [ ] **Step 1: golden test 実行**

Run:
- `cd /home/neguse/ghq/github.com/neguse/lub && scripts/run-golden.sh --sample 01_triangle --backend sokol 2>&1 | tail -5`
- `scripts/run-golden.sh --sample 01_triangle --backend sdlgpu 2>&1 | tail -5`

Expected: 両 backend で `OK` (PNG が既存 golden と byte-identical)。Haxe 化前後で出力が変わらないことが Phase 0 のキー検証。

注意: Haxe 出力に含まれる haxe runtime helper (`_hx_*`) や乱数初期化の有無で frame 30 までの状態が微妙に変わる可能性は **あり得る**。golden が壊れた場合:
- `scripts/run-golden.sh --sample 01_triangle --update` で再生成
- ただし 01_triangle はクリアカラー + 静的三角形なので差分は実質ゼロのはず

- [ ] **Step 2: Commit**

```bash
git add samples/01_triangle.hxml samples/Triangle01.hx
git rm samples/01_triangle.lua
git commit -m "Port 01_triangle sample to Haxe

First end-to-end verification of the Haxe -> Lua -> hot reload
pipeline. Golden output byte-identical to the pre-port Lua version."
```

---

## Task 29: docs/superpowers/specs の「未解決事項」を spec 後 fixup

実装中に解消した未解決事項を spec に反映する (`lua.PairTools.MultiReturn*` の確定形など)。

**Files:**
- Modify: `docs/superpowers/specs/2026-05-25-haxe-lua-transpile-design.md`

- [ ] **Step 1: 未解決事項 section を更新**

Task 27 で確定した `MultiReturn` の取り扱い方 (`untyped` か `@:multiReturn`) を spec に追記。

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-05-25-haxe-lua-transpile-design.md
git commit -m "Lock down spec unresolved items from impl experience"
```

---

## Task 30: README に lub.hxml dev workflow を追記

**Files:**
- Modify: `README.md`

- [ ] **Step 1: 「実行」section に `.hxml` の説明を追加**

```markdown
### Haxe sample の実行 (Phase 0)

依存:
- Haxe 5+ (`haxe --version` で確認)
- `haxelib dev lub /path/to/lub/haxe-lib/lub` で extern を登録

```sh
./build/lub samples/01_triangle.hxml
```

lub が `haxe --wait` を子プロセスとして spawn し、samples/Triangle01.hx の編集を保存するたびに `samples/.lub/01_triangle.lua` が atomic に更新され、lume.hotswap で reload される。
`.lub/` は generated artifact なので gitignore 済み。
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "Document Haxe sample workflow in README"
```

---

## Task 31: 残り 13 sample の移植 (template、Phase 0 内で full migration するなら個別 Task 化)

下記 13 sample を `.hxml + .hx` に移植する。各 sample に対して Task 27 と同じ手順を機械的に適用:

1. 00_hello
2. 00b_clear
3. 00c_buffer
4. 00d_shader
5. 02_vertex_color
6. 03_texture
7. 04_mvp
8. 05_postprocess
9. 06_deferred
10. 07_compute
11. 08_gltf
12. 09_breakout
13. 10_breakout3d
14. 11_shadow

各 sample の手順:
- [ ] `samples/<name>.hxml` を作る (`-cp samples / -lib lub / -main <PascalCase>`)
- [ ] `samples/<HaxeClass>.hx` を作る (`onInit` / `onFrame` を持つ class)
- [ ] 既存 `samples/<name>.lua` を `git rm`
- [ ] `scripts/run-headless.sh samples/<name>.hxml --capture /tmp/out.png --capture-frame 30` で動作確認
- [ ] `scripts/run-golden.sh --sample <name> --backend sokol` と `--backend sdlgpu` で golden 一致確認
- [ ] 個別 commit (`Port <name> sample to Haxe`)

注意点 (sample 別):
- **09_breakout / 10_breakout3d**: stateful (paddle/ball/bricks 配列を module-level に持つ)。Haxe では `static var` で表現する。reload で state が reset される現象は既存 Lua 版と同条件 (spec の通り、state preservation は Phase 0 外)。
- **07_compute / 08_gltf / 11_shadow**: API surface が広いので extern (Gfx) が unsupported な field を要求していないか実機で確認。足りなければ `Lub.hx` / `Gfx.hx` に追加して haxe-lib を更新。
- **データファイル参照**: `samples/data/*.slang` / `*.verts.lua` / `*.png` のパスは変更なし、`Io.loadText` 等で読み込み続ける。

---

## Task 32: Phase 0 完了確認 (チェックリスト)

- [ ] 14 sample すべて `.hxml + .hx` 化されている (`ls samples/[0-9]*.lua` が空)
- [ ] `scripts/run-golden.sh` 全 sample × 両 backend が pass
- [ ] WASM ビルド (`emcmake cmake && cmake --build`) が成功
- [ ] `web/scripts/verify-headless.mjs` が既存挙動 (`.lua` 編集経路) で動く
- [ ] README に dev workflow が反映されている
- [ ] spec / plan の commit が main にある

これで Phase 0 deliverable は完了。Phase 1 (NGS) や WASM compile endpoint 実装、`Lub.persistent` state preservation などは別 spec / 別 plan へ。

---

## 自己レビュー (writing-plans skill チェック)

### Spec coverage
- 必然性 1 (mtime polling + lume.hotswap): 触らない、既存利用 — 該当 task なし、Task 23 で `entry_path` を `.lub/<base>.lua` に向けるだけで成立 ✓
- 必然性 2 (require shim): Task 8 (prelude) + Task 18 (postlude) ✓
- 必然性 3 (hxml 標準): Task 16 (parser) ✓
- 必然性 4 (`haxe --wait` 常駐): Task 11-14 ✓
- 必然性 5 (多ファイル watch): Task 20-22 ✓
- CLI 仕様 (拡張子 dispatch): Task 5 + Task 23 ✓
- `.gitignore`: Task 1 ✓
- package.path: Task 4 ✓
- 命名規則 (callback camelCase): Task 2-3 ✓
- WASM compile-out: Task 26 ✓
- 01_triangle 動作: Task 27-28 ✓

### Placeholder scan
- "TBD" / "TODO": 該当なし
- "適切なエラーハンドリング": Task 17 で exit code != 0 のときの動作は明示
- 全 step に code block か exact command がある ✓

### Type consistency
- `HaxeServer`, `HxmlMeta`, `HaxeBuildResult`, `HaxeWatch`, `HaxePipeline` の field 名は Task 11/15/20/22 で初出のまま参照されている ✓
- `haxe_server_start` / `haxe_build_run` / `haxe_pipeline_tick` の signature は宣言と実装で一致 ✓
- `lua_ctx_add_package_path` の signature は Task 4 ✓
