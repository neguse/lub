# lub script 層ギャップ分析 (T125)

lub (`../lub`, readonly) の Haxe script 層を tcs で置き換えられるかの判断材料。
2026-07-12 時点の lub 実装の読解と、tcs での最小コード実験
(transpile + `tcs check`) に基づく。確認済みと未確認を区別して記す。

## lub 側の契約 (確認済み)

### エントリと hot reload

- `lub <entry>` は `.hxml` (Haxe pipeline) と Lua module 名の両方を受け付ける (`src/main.c`)
- **訂正 (T126 実測)**: dirname を package.path に足すのは `.hxml` branch のみ。非 hxml entry は
  **cwd 相対の `samples/<mod>/.lub/<mod>.lua` レイアウト前提**で、`samples/boot.lua` と
  `third_party/lume/lume.lua` も cwd 相対にロードされる。lub リポジトリ外で動かすには、
  同じレイアウトを staging した run ディレクトリを作って cwd をそこへ移す
  (tcs 側 `samples/lub/run-lub.sh` で解決。lub への feature request 候補:
  `.lua` entry の任意パス対応)
- C 側が entry (`samples/<mod>/.lub/<mod>.lua`) の mtime を毎フレーム poll し、変更時に
  `lume.hotswap(module_name)` で reload する (`src/app.c` / `src/lua_api.c`)
- したがって `tcs --watch` の出力先をこの staging パスにすれば、lub 側は無変更で hot reload が繋がる

### module 契約

- entry module は require で table を返す必要がある (`samples/boot.lua`)
- C 側はその table の `onInit()` / `onEvent(e)` / `onFrame(dt)` / `onQuit()` を呼ぶ (`src/lua_api.c`)
- callback 名は lowerCamel 固定。Haxe pipeline では lub 側が Haxe 出力へ `return <main>` を後付けして module 化している (`src/haxe_build.c`)

### core API

- **訂正 (T126 実測)**: C runtime が expose するのは **flat global** (`begin_pass`, `end_pass`,
  `config`, `main_tex`, ...) であり、`Gfx` / `Lub` などの namespace table は Haxe pipeline が
  Haxe 出力へ prepend する `HAXE_PRELUDE` (`src/embedded_prelude.h`) が組み立てている。
  `.lua` entry では prelude を通らないため、tcs 側で shim が必要 (G5)
- Haxe の `lub/*.hx` は全て extern (型定義のみ) で、tcs の `--ref` と同じ構図
- 関数名は snake_case (`Gfx.begin_pass`)。Haxe 側は `@:native` で camelCase に見せている
- オプション引数は匿名 table (`PassOpts` 等の typedef。キーは snake_case の wire format)
- opaque handle (`ShaderRef` / `BufferRef` / `TextureRef` = Dynamic) を受け渡す
- Lua multi-return を返す API が 10 箇所ある (`Io.load*` / `Input.mouse*` / `Gfx.size` / `Gfx.readTexture` / `Audio.decode` / `Host.msg` / `Ui.color` など)
- 定数は table フィールド (`Gfx.VERTEX` 等)

### lubx 層

- `lubx/*.hx` (SpriteBatch, Camera2d, Text, Boot, ...) は extern ではなく Haxe 実装
- tcs 置き換え時は lubx をそのまま使えない。PoC は core API 直呼び + 必要分を C# で書く
- 00_hello が使う `lubx.Boot.config` は「`LUB_BACKEND` env を読んで `Lub.config` に渡す」だけの定型で、自前実装できる

## tcs 側の実験結果

00_hello 相当の最小コード (`Gfx.begin_pass` + `new PassOpts { ... }` +
lowerCamel callback) を `--ref` stub 付きで transpile / check した。

### そのまま通るもの (確認済み)

- 識別子は無変換で emit される。C# stub を snake_case / lowerCamel で書けば `Gfx.begin_pass(...)` / `Hello.onInit` がそのまま出る。リネーム機構は不要
- `const` フィールド参照 (`Gfx.VERTEX`) は透過
- `Console.WriteLine` → `print`
- `new float[] { ... }` → Lua table (`clear_color` 等に使える)
- `--ref` 型は Lua 出力に含まれず host 注入 table を呼ぶ、という既存方針が lub の extern 構図とそのまま一致する

### ギャップ (確認済み)

| # | ギャップ | 内容 |
|---|---------|------|
| G1 | object initializer 黙殺 | `new PassOpts { target = ..., clear_color = ... }` が診断なしで `PassOpts.new()` に落ち、初期化子が消える。T97 (黙殺しない) 方針違反の既存バグ。さらに `--ref` 型は Lua 側に `.new` が存在せず実行時エラーになる |
| G2 | module return なし | tcs 出力は global 定義のみで `return <table>` を出さないため、lub の entry module 契約に合わない |
| G3 | naming warning | lowerCamel メソッド (`onInit` 等) に `naming:` warning が出て `tcs check` が exit 1 になる。lub の wire format は lowerCamel / snake_case 必須なので、そのままでは check ゲートに乗せられない |
| G4 | Lua multi-return | C# 側に multi-return の表現がない。00_hello では不要だが、breakout 級で `Io.loadText` (text/version/status/error) が必要になる |
| G5 | host API の namespace shim | lub の C 側は flat global を expose し、namespace table は Haxe 専用 prelude が作る。tcs 出力にも同等の shim を前置する仕組みが必要 (T126 で発見) |

### 未確認 (breakout 級で実測)

- `--ref` 型の instance method 呼び出しの emit 形。tcs は `obj:Method()` (colon call) を出すはずだが、lub の instance 風 API (`Readback.read_texture` 等) の呼び出し規約と合うか。00_hello / breakout では不要
- `onEvent(e)` の event table を typed stub class で読む際の field 名透過 (field access は透過のはずだが未実測)

T126 で実測済み: stub class `os` 経由の `os.getenv` 透過呼び、`Lub.config` の opts table、
`begin_pass` の PassOpts table literal、`--entry` module return、lub `--capture` による画面検証。

## 切り分け

### tcs に足す機能

- **G1: object initializer** — バグ修正として対応する。Lua 出力対象外の型 (`--ref` 型) の `new` + initializer は plain table literal `{ target = ..., clear_color = ... }` へ emit するのが lub 契約にも合う本命。Lua 出力対象の通常 class は `.new()` 後の field 代入で対応するか、当面 TCS1001 にする
- **G2: module return** — CLI に entry class 指定 (例: `--entry Hello`) を足し、出力末尾に `return Hello` を出す
- **G3: naming warning** — 抑制手段を足す (CLI flag など)。lub 契約コードを `tcs check` ゲートに乗せるために必要
- **G4: multi-return** — `out` 引数への割り当てなどで表現する。breakout 級まで先送り可
- **G5: namespace shim** — `--prelude <shim.lua>` で任意のユーザー Lua を出力へ前置できるようにした (T126 で実装済み)。lub 用 shim 本体は `samples/lub/lub_shim.lua`

### stub / shim で吸収

- lub core API の C# stub は tcs 側リポジトリに置く。snake_case メソッド名 + opaque handle class + opts class で表現できる
- `lubx.Boot` 相当の起動定型 (env 読み + `Lub.config`) は C# で書く

### lub への feature request

- 現時点でなし。multi-return API の table 返し版が欲しくなる可能性はあるが、G4 を tcs 側で解けば不要

## PoC (T126/T127) の進め方

1. G1 (`--ref` 型の table literal 化) / G2 / G3 を tcs に実装する
2. tcs 側に `samples/lub/` として 00_hello 相当の C# + lub core stub を置く
3. `lub <tcs>/samples/lub/.../hello.lua` で起動し clear 画面を確認する (T126)
4. `tcs --watch` + lub 側の mtime poll で hot reload を確認する (T127)
5. breakout 級のサンプル移植は G4 (multi-return) 実装後に判断する
