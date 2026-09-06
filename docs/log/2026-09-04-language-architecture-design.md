# 言語構成の設計 — Haxe 撤去、C# の 3 つの実行形、C API 層

> 記録: 2026-07-23 から 2026-09-05 にかけて検討した設計。パッケージング設計
> (`2026-07-23-packaging-design.md`)の議論から派生した言語まわりの案を、
> Haxe の撤去、C# の 3 つの実行形の決定、C API 層の定義まで 1 本にまとめたもの。
> 現状の構成の正は `docs/api-glue.md`。この文書単体で読めるように、根拠に
> した当時のコードの状態も書く。

## 要約

- Haxe は deprecate する。authoring は raw Lua と C# の 2 系統。撤去は全
  サンプルと ngs の C# 版が揃ってから。
- C# の実行形は 3 つ。tcs→Lua(dev、今あるもの)、tcs→C(release AOT、tcs
  側で計画中)、.NET 実行(実 .NET で動かす)。tcs→Lua と .NET 実行を dev の
  経路としてユーザに選ばせて両方維持し、tcs→C は release の経路として tcs の
  IL 計画に追従する。
- 同じゲームの C# ソースが全部の実行形で通ることが契約。lubx の正は C#
  ソースで、tcs が生成した Lua をリポジトリに置いて raw Lua からも届かせる。
- runtime に C API 層を定義する。線は immediate mode 層の上に引き、Lua
  binding、.NET 実行の P/Invoke facade、tcs→C の生成コードはどれも C API への
  薄い詰め替えにする。ゲームは runtime の memory を所有せず、runtime は
  ゲームの memory を保持しない。
- API の面は C# stub を記述として、header・Lua binding・facade・API docs を
  生成する。
- 名前は lowerCamel を中立表記とする規則 1 つで Lua / C# / C の 3 面を結ぶ。
  例外表は持たない。Lua 面は `lub` 1 つの global の下に小文字の namespace。

## 出発点: lub の正体は Lua contract

core API、`samples/lub_prelude.lua` が注入する namespace table、hotswap の
プロトコルが製品境界で、コンパイラ(Haxe / tcs)は周辺機器という整理。
実態は既にそうなっている(native の Haxe はコンパイルサーバ、web の Haxe と
tcs は wasm モジュール)。この見方を採るなら、いま実装の中に暗黙にある
contract を文書化された契約に昇格させる必要があり、以下の C API 層と記述の
話はその昇格の具体化になる。

## Haxe を撤去する

2026-07-23 時点では freeze(新機能投資の停止、動態保存)が本命に見えていて
未決だった。維持費の実体は、lubx の Haxe / C# 二重実装、全サンプルの両言語
対応、haxe-wasm(コンパイラの client-WASM 化)の維持と std-bundle 再生成の
運用。これを払い続けない判断として deprecate に決めた。

撤去の条件は、全サンプルと ngs の C# 版が揃い、tcs→Lua と .NET 実行の両方で
golden が通ること。ngs は Haxe 版しか無く(`samples/ngs/` に .cs は無い)、
roadmap の Phase 2 の出荷対象なので、Phase 2 は Haxe 版で出荷し、その後の
C# 移植が撤去条件の最後の 1 つになる。

deprecate の間の扱い。新機能と新サンプルは C# のみ、既存の Haxe サンプルは
凍結、CI の Haxe workflow は撤去まで維持、`templates/game/`(今は
`Game.hx` + `game.hxml`)は .NET 実行の host ができた時点で csproj の
テンプレートに置き換え、playground の言語トグル(`web/playground/samples.ts`
の `SampleLanguage`)は撤去時に C# のみにする。raw Lua のトグルは要望が
出てから。

撤去で宙に浮くのは API reference の生成元。`web/scripts/gen-api-docs.mjs:5`
が「single source of truth は `haxe-lib/lub/**/*.hx` の doc comment」と書いて
いて、`haxe --xml` を parse している。C# stub の XML doc comment から生成
する経路へ移すことが撤去の前提条件になる。

## C# の 3 つの実行形

### 定義

| 実行形 | 何をする | 状態 |
| --- | --- | --- |
| tcs→Lua | tcs が C# を Lua に transpile し、lub の player が hotswap で動かす | 今あるもの |
| tcs→C | 同じ IL から IL→C backend が C を吐き、静的 link / dll / wasm にする release AOT | tcs 側で計画(`third_party/tcs/doc/il-design.md` の M3、tasks.md の T218)。M1 の内部再編は完了、IL→C は未着手 |
| .NET 実行 | 実 .NET(CoreCLR か NativeAOT)で C# をそのまま動かし、P/Invoke で lub の C API を叩く | ゲームの経路としては無い。tcs の仕様適合テストが「同一ソースを実 .NET で実行した出力との differential 比較」のオラクルとして持つ(`doc/spec-conformance-design.md`) |

### 比較

| | tcs→Lua | tcs→C | .NET 実行 |
| --- | --- | --- | --- |
| 用途 | dev | release | dev(.NET の道具で開発したい人) |
| hot reload | lub の hotswap | mixed-mode。編集した module だけ interpreter に落ち、AOT 済み module は native のまま | .NET Hot Reload |
| デバッガ | 無い(Lua から C# の行に戻す仕組みが無い) | 同左 | 本物 |
| 意味論 | IL が正本 | IL が正本。digest 比較で tcs→Lua と一致を担保 | .NET そのもの。IL のオラクル |
| 数値 | tcs の M4 後は i32 + f32(`LUA_32BITS`) | i32 + f32 | .NET の型どおり |
| export に要るもの | 無し(snapshot + player、R3) | C コンパイラ(web は emscripten) | .NET ツールチェーン |
| web | 今あるもの | emscripten で最初から対象 | やらない |
| lub 側の依存 | 無し | C API と 32 bit ビルド。tcs の M2 / M3 待ち | C API、facade、C# の host |

### 決定

- tcs→Lua と .NET 実行を dev の経路として並べ、ユーザに選ばせて両方維持
  する。lub の開発体験(tcs + hotswap)は .NET Hot Reload と既存 IDE に対して
  ユーザの選択で競う。.NET 実行は計測台でも R5 の逃げ道でもない。
- tcs→C は release の経路として tcs の IL 計画に追従する。lub 側は C API と
  `LUA_32BITS` ビルドを用意して待つ。lub の既定は prebuilt の player +
  ゲームごとの dll / bundle で、player は変えない。静的 link は R5 の自前
  main() 向けに残す。可否は tcs 側の設計で確認する。
- 同じゲームの C# ソースが 3 つの実行形で通ることが契約。tcs が受ける C# の
  部分集合が共通の言語仕様で、サンプルは全部が tcs→Lua と .NET 実行で動く
  (Haxe / C# の両言語対応の置き換え)。tcs→C は同じ IL を通るので構造的に
  同じ。
- .NET 実行の web はやらない。web は tcs→Lua と tcs→C だけ。

### 入口の規約

今の規約を固定する。entry class は csproj の basename、`static` の
`OnInit()` / `OnEvent(EventData)` / `OnFrame(float dt)` / `OnQuit()`。
tcs→Lua は今どおり lub の player が呼び、.NET 実行はテンプレートの
`Lub.Run<Game>()` が pull 型の C API の上で loop を回して呼ぶ。tcs→C は lub の
player が host。ゲーム側のソースは実行形で変わらず、自前 main() を書く形は
その上の R5 として残る。event は C API の `poll_event` が typed struct を返し、
host が 1 件ずつ `OnEvent` に渡す。今の event table は `type` しか入って
いない(`push_event_table`、詳細は後段と註あり)ので、struct の中身は C API
定義のときに最小で決める。

### 帰結

- 意味論の一致は tcs の構造で担保する。tcs→Lua と tcs→C は IL の digest
  比較、.NET 実行はそのオラクル。lub の API を含む full program の差分比較は
  tcs の適合 harness に置き、lub の CI では各サンプルを headless で一定
  frame 回して stdout の digest を tcs→Lua と .NET 実行で突き合わせる。
  golden 画像は実行形ごとに別ファイル(`tests/golden/<name>_web.png` と同じ
  流儀)で共有しない。float の差で割れる前提を最初から織り込む。
- 配布は実行形ごと。tcs→Lua は R3 のまま(snapshot + player)。tcs→C は
  export に C コンパイラが要るので CI で回す形が自然。.NET 実行は export に
  .NET が要り、lub はライブラリ(実行物 + facade + host + lubx)と export の
  手順を製品として持つ。配布物はリポジトリ内のテンプレート
  (`templates/game-dotnet/`)で、csproj が lub の release build に含める
  native ライブラリを参照する。NuGet は今はやらない。パッケージング設計の
  「R5 層のパッケージングは各プロジェクトの管轄」は、.NET 実行については
  lub の管轄に変わる。dev 時に .NET SDK が要る点は tcs 経路も同じなので、
  差は export 時に限られる。
- ユーザが .NET 実行を選ぶ理由がデバッガなら、tcs 経路への source map 付き
  デバッガが次の投資候補。
- CI は tcs→Lua と .NET 実行の両方を回す。linux / windows の gate に .NET
  実行の build と golden が増える。

## 名前の面と写像規則

### 2026-08-31 時点の面

| 面 | 例 | 決めている場所 |
| --- | --- | --- |
| C の flat global | `begin_pass`, `phys2d_world`, `gfx_size`, `profile_begin` | `src/lua_api.c` |
| Lua の namespace | `Gfx.begin_pass`, `Phys2d.phys2d_world`, `Profiler.begin_scope` | `samples/lub_prelude.lua` |
| C# | `Gfx.begin_pass`(wire 名の直写し) | `cs-lib/lub_stub.cs` |

コードを読んで確認した状態。

- C# 面は全域が C# の慣習から外れている。core API は wire 名そのままで、
  そのため `--no-naming-check` が 4 箇所で立っている
  (`src/tcs_build.c:200`、`scripts/run-cs-sample.sh:56,62`、
  `web/scripts/gen-tcs-prebuilt.mjs:114`)。cs-lib と C# サンプルも
  Haxe 由来の camelCase メソッド(`sb.begin()`、`Assets.floats()`)。
- 写像を持っているのは Haxe extern の `@:native` だけ。Haxe を外すと
  camelCase と snake_case の対応知識がリポジトリから消える。
- Lua の namespace 面には 2 系統が混在する。`Gfx` / `Input` / `Profiler` /
  `Sys` は短名に正規化済みで、`Phys2d` / `Phys3d` / `Ui` / `Audio` /
  `Font` / `Host` は flat global 名のまま(`Phys2d.phys2d_world`、
  `Ui.ui_text`、`Audio.audio_play`)。
- entry callback だけ Lua 面で camelCase。`src/lua_api.c:2436,2444` が
  `onInit` / `onFrame` を引く。

### 規則: 中立表記を lowerCamel にする

写像の向きで決まる。snake_case を正にすると C# 名を作るときに `RGBA8` や
`fnv1a64` の大文字化が復元できない。lowerCamel を正にすれば、Lua へは
「大文字の前に `_` を入れて小文字化」、C# へは「先頭を大文字化」で
どちらも決定的に落ちる。lowerCamel はどの面にも現れない内部表記になる。

定数(enum のメンバ)は Lua と C では snake_case にした上で全大文字
(`lub.gfx.DEPTH24_STENCIL8`、`LUB_GFX_DEPTH24_STENCIL8`)、C# では先頭
大文字の PascalCase(`PixelFormat.Depth24Stencil8`)。C と C# は型つきの enum、
Lua は namespace 直下の平らな定数。

既存の全 wire 名でこの規則を突き合わせた結果、破れるのは `DONTCARE`
1 個だけだった(`dontCare` からは `DONT_CARE` になる)。
`DEPTH24_STENCIL8` / `RGBA32F` / `TRIANGLE_STRIP` / `ui_color_edit3` /
`fnv1a64` / `overlap_aabb` / `interleave_pncmw` はいずれも規則どおりに
落ちる。`DONTCARE` を wire 側で直せば、例外表は空になる。

C 名は `lub_<namespace>_<member>` に規則化する(`lub_gfx_size`、
`lub_profiler_begin_scope`、`lub_input_key_down`)。今の C 名は `gfx_size` と
`profile_begin` と `key_down` のように prefix が不揃いで、.NET 実行の facade
が表を持たないと結べなかったが、C API 層を切り出す際に規則に揃えることで
3 面が同じ規則 1 つで結ばれる。

### 規則の適用は tcs の emit

例外が無いなら写像は表ではなく関数で表せる。ゲームコードの呼び出し側の
名前(`Gfx.BeginPass` を `begin_pass` に落とす)は tcs の emit が規則で写す。
後述の記述からの生成は各面の宣言を作るだけで、この役割は変わらない。
今 `EmitRefTypeTable`(`third_party/tcs/Transpiler/LuaEmitter.Objects.cs`)が
メンバ名をそのまま出しているところが、写像を通す 1 箇所になる。

全メンバに一律で効かせる。参照専用型の判定が不要になり、帰結 3 つはどれも
機械的に扱える。

- entry callback は `on_init` / `on_frame` へ寄せる。C# の `OnFrame` が
  `on_frame` に落ちるため。
- 同じ型に `Foo` と `foo` があると同じキーに潰れる。コンパイル時に
  検出できるので tcs の診断で扱う。
- hotswap の registry キーが名前ベースなら、そちらも写像後の名前で揃う。

tcs が吐く Lua が snake_case になるということは、tcs の出力を Lua
ライブラリとして配れるということでもある。lubx の置き場所に直結する。

### Lua 面の形

今は `Gfx` / `Phys2d` / `Io` と PascalCase の global で、Lua の慣習は小文字。
小文字にすると、global 注入のままでは `io` と `math` が Lua 標準ライブラリと
衝突する。

`lub` 1 つだけを global に注入し、その下に小文字の namespace を置く
(`lub.gfx.begin_pass`)。require 不要は保ちつつ、Lua だけで使う人に読める形に
なり、標準ライブラリと衝突せず、C 名 `lub_gfx_*` とも揃う。namespace の
写像は「Lua は全小文字、C# は先頭大文字」の 1 行で例外なし。`lub` テーブル
自体は既にあるが、今は Haxe の emit 形のための別名。wire の変更は C API の
定義と同じ 1 回にまとめる。

### 完了の判定

名前の統一については、`--no-naming-check` を 4 箇所から消せた時点。

## raw Lua と lubx

### raw Lua の現状

`src/main.c:200` で `.lua` 直エントリは通る。ただし raw Lua のサンプルは
無く、golden にも載っていない。そして lubx(SpriteBatch / Atlas / Text /
Camera2d など)は Haxe と C# の実装しかなく、サンプルに同梱コンパイル
される形なので raw Lua からは届かない。唯一の例外が
`samples/lubx_png.lua` で、手書きの Lua がリポジトリに入っていて
prelude が `Png` として置いている。Lua だけで書けるかを決めているのは
名前ではなく、lubx の置き場所。

### lubx の正は C# ソース

2026-07-23 時点では「lubx を Lua 実装にする」案があったが、.NET 実行を
支えるならそのままでは穴が空く。.NET 実行は Lua を経由しないので lubx が
届かず、実質「core API 直叩きだけの経路」になる。tcs→C も同じで、Lua で
書いた lubx は AOT の対象にならない。lubx はゲームを書くときに触る面の
ほとんどなので、これは大きい。

3 通りのうち、C# で書き、tcs で transpile した Lua を生成物としてリポジトリに
置く形に決めた(`samples/lubx_png.lua` と同じ棚)。実行形ごとの供給はこう
なる。

- tcs→Lua: 今どおりゲームごとに C# ソースから同梱コンパイルする。lubx の
  hot reload と fork が今のまま残る。
- tcs→C: 同じ IL で AOT される。
- .NET 実行: C# ソースを直接コンパイルする。
- raw Lua: checkin した生成 Lua を require する。

二重実装は消え、型検査は残る。写像規則が入っていれば生成 Lua は snake_case
で出るので、Lua ライブラリとして読める。raw Lua と 3 つの実行形すべてに
1 つのソースで届く唯一の形で、パッケージング設計が配布物を Lua とデータ
だけと決めている点とも整合し、生成 lubx はそのまま snapshot の閉包に入る。

払うものは 2 つ。raw Lua の人が lubx を書き換える体験が「生成物を書き換える」
になること。もう 1 つは生成 Lua の再生成忘れで、これは `web/tcs-prebuilt/` や
`std-bundle.json` で既に踏んでいる形なので、再生成して差分が出たら落とす
検査を CI に置く。

未知として一番大きいのは、tcs が今「registry を適用する単一 entry Lua」を
吐く形で、require できるモジュール Lua を吐くモードが要ること。重さは
実装を見ないと読めない。

## C API 層

### 現状

C API 層は無い。`src/lua_api.c` の flat global(`lua_setglobal` が 47 箇所)は
lua_CFunction で、option table の読み取り、TextureRef の sentinel 検査、
string key での resource 解決(`res_table_get`、hash + strcmp)、宣言と合って
いるかの検証をすべて binding の中で行ってから内部関数を呼ぶ。
`l_begin_pass`(538 行目から)だけで 150 行ほどある。その下の `pass.h` /
`resources.h` / `backend.h` は `struct App*` を取る内部モジュールで、
外に出せる ABI ではない。このままでは .NET 実行の P/Invoke facade も tcs→C の
生成コードも検証を再利用できず、検証なしで内部を叩くか、呼び出し側で検証を
書き直すかになる。

### 原則: 線は immediate mode 層の上に引く

前身の lub3d(`neguse/lub3d`)は Lua が生成 binding を直接 require し、
binding は sokol / box2d / miniaudio と 1 対 1 だった。境界が retained mode の
API の上にあったため、寿命の事情が境界の上に散った。資源の寿命は
`lib/gpu.lua` の `__gc` wrapper、親子の寿命は generator の `DependencyBinding`
(`doc/ownership-design.md`)、callback は `idl/box2d.idl` の `[Persistent]` と
Immediate の 2 種類の橋(Persistent は関数ごとの static context に registry ref、
`lua_call` で保護なし、hot reload 後も古い closure を握る)、app の callback は
Emscripten で `sapp_run` が即 return して context が GC され落ちたので registry
の self_ref で自分を握る(`gen/sokol_app.c` 冒頭)。hot reload の整合は
`lib/boot.lua` の colon 呼びで取り、thread は Jolt の worker を 0 にして回避
した。IDL に `[Persistent]`、`event`、`HandleType`、`DependencyBinding` が
増えたのはこの分散の写し。

lub は同じ仕事を C の中に集めている。string key と `get_or_create`、world の
generation と prune、callback の宣言型寿命、step 後の event 配列、readback の
ring。Lua 側に `__gc` wrapper も self_ref も無い。C API はこの集まりを ABI と
して切り出すもので、線の下は retained mode の依存を宣言型に変換する層、
線の上は table や struct への詰め替えだけにする。今その線が滲んでいるのは
`lua_api.c` の検証と sentinel 検査、`g_app_for_lua` の global で、C API の
定義はこれを線の下に押し戻す作業と同じ。

### Lua VM に依存している箇所

Lua VM を仮定できない実行形(.NET 実行、tcs→C)に向けて、Lua に依存している
箇所を数えた。coroutine は tcs の emit にもサンプルにも lubx にも使用箇所が
無い。依存は 4 種類。

- Lua で実装された API。`samples/lub_io.lua`(351 行。`Io.load_*` と
  `interleave_*`、gltf の table 形式、mtime と hash による cache 更新)と
  `samples/lubx_png.lua`(84 行。Lua 標準の `io.open` を直接使う)。C API に
  移す。gltf は typed な MeshData を返し、interleave は C で持ち、cache は
  runtime の keyed cache にする。
- Lua の table をそのまま受け渡す API。stub に `object` 引数が 92 箇所。
  中身は gltf の mesh table、`Sdf` の木(`Dictionary<string, object>` の入れ子を
  C の `sdf_mesh` が読む)、`audio_pcm` の table か Bytes、joint の desc、Ui の
  bindings。全部 typed な struct に書き切る。Sdf の木は C API では node の
  配列 + index の平坦な形にし、C# 側の builder はそのまま残す。
- GC 依存。Bytes と Readback は `__gc` 付きの userdata で、runtime 所有の
  handle を Lua の GC が返す形。下の所有権の規則で消す。
- 参照の sentinel(`{__lub_kind, key, version}` の table)。int32 の handle に
  置き換える。

### 形

- context handle を取り、typed な desc 構造体を受け、戻り値は status コード。
  検証はこの層に置き、Lua binding は table から desc への詰め替えとエラーの
  raise だけ、facade も詰め替えだけにする。
- 型は int32 / float / bool / UTF-8 の byte 列だけ。double と 64 bit 整数は
  面に出さない(数値型の節)。
- 文字列は常に UTF-8 の `(const char*, size_t len)` で、NUL 終端を要求しない。
  Lua は interned bytes をそのまま渡し、.NET の facade が UTF-16 から変換する
  (hot path は `u8` の span、便利用に string の overload を重ねる)。tcs→C は
  IL の文字列表現がどうであれ bytes + length で渡せる。C API 側に UTF-16 は
  置かない。
- resource の同一性は string key を保ちつつ、整数 handle の経路も持つ。宣言
  (`use_*`)が handle を返し、key から引く lookup も置く。handle は key +
  version に結び、同じ key が同じ version で宣言され続ける限り hot reload を
  跨いでも有効。version が変わるか sweep されたら stale で、stale handle の
  使用は誤用としてエラーハンドラに回り、facade はそれを受けて再解決する。
  今の sentinel が持つ `key` / `version` と同じ意味論。Lua は interned 文字列
  のまま、facade は key ごとに handle を cache する。
- 連続メモリを生 pointer で受ける。.NET 実行の C# と tcs→C の生成コードが
  `float[]` や struct 配列を直接流し込めるようにする(Lua の配列は boxed
  TValue の列で、ここが AOT と .NET の性能上の利点になる)。
- tcs→C の生成コードは Lua の table を経由せず、desc を C で組んで C API を
  直接呼ぶ。呼び方は tcs 側の IL→C 設計で決まるので、lub は header を契約
  として用意する。
- C API は main thread 限定。audio の mixer 等は内部にとどめ、外に出さない。

### 所有権と GC

runtime を読むと、答えの半分は既にある。resource は key で宣言し
`last_seen_frame`(`src/resources.h`)と `resource_sweep_after_frames` で
sweep、`use_texture` は version が同じなら data を省略でき、audio の snd は
内容で dedupe、voice は key で毎フレーム宣言(`src/audio.h`)。残っている
GC 依存は Bytes と Readback の `__gc` userdata だけ。これを 3 つの規則に
揃える。

- 規則 1。ゲームの memory を runtime に渡すときは呼び出しの間だけ借用。
  runtime は必要なら copy し、呼び出し後に pointer を持たない。
- 規則 2。runtime の memory をゲームに返すときは、その frame の終わりまで
  有効な view(pointer + length + frame 番号)。ゲームは frame を跨いで
  持たない。持ちたければ自分の memory に copy する。
- 規則 3。frame を跨いで生きるものは全部 runtime 所有の keyed resource にし、
  subsystem ごとの sweep 規則で回収する。ゲームが持つのは key と int32 の
  handle だけで、所有 pointer は持たない。

これで `__gc`、finalizer、`IDisposable`、retain / release が契約から消え、
tcs→C の object model(tcs の T212、未定)にも依存しない。今あるものの
行き先はこうなる。

| 今 | 新しい形 |
| --- | --- |
| Bytes(readback / audio_decode / png の結果、`__gc` userdata) | frame 有効の view。Lua binding は frame 番号つき userdata(zero copy)、.NET は `ReadOnlySpan<byte>`、tcs→C は pointer + length |
| Readback(`Gfx.readback()` が返す ring object) | key で宣言する resource(`lub.gfx.readback("key")`)。宣言が切れたら sweep。id token は int32 |
| load_gltf / surface_nets / sdf_mesh の MeshData(Lua table) | runtime 所有の mesh を key で cache し、frame 有効の view で返す。lubx の Mesh3d が保持したければ自分の `float[]` に copy(今も `data` field に持っている) |
| Png.load の decoded 画像(`lubx_png.lua` の Lua cache) | runtime の keyed cache(path が key)。毎フレーム view を返し、`use_texture` は version が同じなら data を読まない |
| audio snd(int handle + `audio_free`) | `audio_snd("key", pcm, version)` で宣言し、他の resource と同じ sweep。内容 dedupe はそのまま、`audio_free` は消える |
| TextureRef 等の sentinel table | int32 handle(key + version に結ぶ) |
| context | host が create / destroy。中身は全部これにぶら下がる |

view の安全は runtime が dev 機能として検査する。view に frame 番号を刻み、
古い view を渡された API は stale としてエラーハンドラに回す。.NET は
`ReadOnlySpan<byte>` が ref struct なので field に入れられず、frame を跨ぐ
保持がコンパイル時に弾かれる。Lua は userdata を table に仕舞えてしまうので、
この runtime 検査が守りになる。

非同期の結果は poll した frame に view で届き、その frame だけ有効(今の
readback と同じ)。大きい view を毎フレーム再取得する費用は key の hash 1 回で
copy は無い。ゲームが結果を加工したい場合だけ自分の memory に copy する。

sweep は 2 種類あり、今の挙動のまま。物理は `begin` で即 prune、resource と
cache は `resource_sweep_after_frames` の猶予つき。

### callback の規約

境界を越える callback は 1 種類だけにし、規約を固定する。記述には callback
型が 1 つあるだけで、生存期間の注釈は要らない。

- 生存期間は借用。API 呼び出しの間だけ有効か、宣言型の object(world 等)に
  付く場合は次の宣言か begin まで。runtime は再宣言なしにフレームを跨いで
  保持しない。Lua は registry ref がフレームを越えず、C# は facade が slot を
  毎フレーム上書きするだけで GC 対策が閉じる。hot reload 後は次の宣言で
  新しい closure に入れ替わる。
- 呼ぶ thread は呼び出し元と同じ。別 thread からは呼ばない。thread を跨ぐ
  生産は ring buffer と poll に置き換える(audio は今もこの形)。
- 再入は API ごとに宣言する。callback 内で許す呼び出しと拒む呼び出しを記述に
  持つ。今の `phys_in_callback` がその実装。
- 渡す data は呼び出しの間だけ借用で、struct pointer の in/out。callback は
  その場で編集し、戻ったあと runtime が読む。保持したければ game が copy する。
  大きい buffer は Lua 側に table でなく view を渡して copy を避ける。
- 戻り値は判定や status。エラーは境界で止め、1 回 log して記述にある既定値で
  続行する。C API から longjmp も例外も外に出さない。
- 非同期の完了は callback にしない。poll + user token(readback の形)か、
  step 後の event 配列にする。

除外されるのは、登録しっぱなしで永続する callback、別 thread からの callback、
非同期完了の callback の 3 つで、それぞれ毎フレーム宣言、ring buffer、poll に
置き換わる。phys2d / phys3d の filter / pre_solve / friction / restitution は
この規約の実装例そのもの(宣言型の寿命、main thread、再入拒否、`lua_pcall` と
既定値への fallback。`src/physics_box2d.c` の `callbacks_replace_from_opts` と
`l_phys2d_begin`)で、そのまま残す。

runtime からゲームへの hook(init / event / frame)は C API では pull にする。
frame の開始と終了、event の poll を関数として出し、.NET 実行は C# の host
が、tcs→C は lub の player が loop を回す。Lua 経路では player が今の
on_frame を pull API の上で実装する。readback の id は C API では整数の
user token にし、値との対応は Lua binding が持つ。C API が Lua の値を預かる
箇所はゼロになる。

### エラーの位置づけ

ランタイムエラーは「直せば動く」保証ではなく、開発中に runtime の
エラーハンドラが出す診断機能。想定内の結果(readback の pending / ready、
file request の status、shader compile の失敗)は値で返し、それ以外の誤用は
エラーハンドラの事象にする。ハンドラは message と API 名と frame を記録して
当該 frame のゲーム呼び出しを中断し、player は生かして次の編集を待つ。
出荷ビルドでも同じハンドラで log だけ。ゲームコードは捕まえない。実行形
ごとの配線は、tcs→Lua が今どおり Lua error、.NET 実行が facade の例外を
host が frame 境界で捕まえる、tcs→C が生成コードで status を見て player の
frame 中断に飛ぶ。C API の status コードと `last_error` の文字列は、binding と
host がハンドラへ回すための配線であって、ゲームに見える契約ではない。

C# の型検査で消える分を数えた。`luaL_check*` が 209 箇所、`luaL_error` が
308 箇所、合計 517 箇所(`lua_api.c`、`physics_box2d.c`、`physics_box3d.c`、
`ui.cpp`、`sdf.c`、`font.c`、`host.c`、`gltf.c`)。

| 種類 | 箇所 | C# 化後 |
| --- | --- | --- |
| 型と形(`luaL_check*`、must be a TextureRef、required、unknown kind) | 約 280 | コンパイルエラーになり消える |
| 値の制約(範囲、endpoints must be distinct、convex hull、非ゼロ) | 約 100 | 残る |
| backend と環境(shader compile error、out of memory、format not supported) | 約 80 | 残る |
| resource の同一性と生存(not found、already used、is not live) | 約 30 | 残る |
| 状態(pass の中、begin の前、onInit の中) | 約 10 | 残る |

半分強が型エラーとして消え、残り約 220 が本当の実行時条件で、ハンドラの
対象はこれと stale な view / handle になる。

### 性能

Lua 経路の費用は変わらない見込み。1 回の呼び出しで支配的なのは VM から C
への遷移、`lua_getfield` による table の読み取り、string key の hash、GPU
バックエンドの仕事で、C API 層が足すのは stack 上の desc への詰め替えと
直接呼び出し 1 回(数 ns 級、未計測)。検証は移動するだけで二重にならない。
落ちるとすれば入口で copy する設計にしたときで、上の借用の契約で防ぐ。
数字が要るなら、一番重いサンプルの frame あたり呼び出し回数を数えて掛ける
spike で足りる。

### 完了の判定

`lua_api.c` の binding に検証が残らないこと、facade が C API だけでビルド
できること、tcs→Lua と .NET 実行の両方で golden が通ること。

## 記述と生成

### 経緯

2026-07-23 時点では contract を WebIDL で書いて各言語の面を生成する案が
あった。動機は Haxe extern / C# stub / API docs の 3 重の手書き。Haxe が
消えると手書きの面は prelude と stub の 2 つに減り、名前の対応も規則で表せる
ので、2026-08-31 時点では IDL は棚上げ、復活の引き金は .NET 実行の P/Invoke
facade と見ていた。

その引き金は C API 層で引かれたが、仕事は小さくなっている。C 名を規則化
したので名前の表は要らず、残るのは「1 つの記述から header・Lua binding の
詰め替え・P/Invoke facade・API docs を生成する」ことだけ。header は
tcs→C の生成コードが include する契約でもある。header は生成物で手で
触らない。header を正にして annotate する案は C の parser が
要り、Lua 固有の注釈(option table、多値返し、key 参照)を C のコメントや
macro に押し込むことになるので採らない。

### 記述に要る構文

`src/lua_api.c` と `src/enums.h` にある構文がそのまま要件になる。

- namespace と、その下の関数・定数
- 位置引数と省略可能引数(`luaL_opt*` / `lua_isnoneornil` が 20 箇所)
- option table。省略可能 field と既定値、入れ子、上限つき配列(`targets` は
  `SGL_MAX_COLOR_TARGETS` まで)
- 整数値つきの enum(`enums.h` に 10 個ほど。`enums_lua.c` が手書きで Lua に
  写している 2 つ目の写しで、記述があれば生成物になる)
- resource 参照(handle)
- byte 列(借用 pointer + length、frame 有効の view)
- 多値返し(7 箇所)。C では out 引数
- status と `last_error`。Lua では raise、C ではコード
- 上の規約に従う callback 1 種類
- doc comment

### 記述形式に求める特性

1. 上の構文を全部、素直に書ける。特に「省略可能 field + 既定値」「整数
   enum」「out 引数」「固定長配列」が無理なく書けること。
2. member ごとに中立名が 1 つで、3 面の名前は規則で導く。面ごとの上書きは
   例外的な逃げ道として grep できる形で持つ。
3. 面ごとの注釈を型つきで書ける。Lua 向け(table か位置引数か、多値返し)、
   C 向け(配列長、借用)、C# 向け(Span、out)。文字列の慣習でなく、
   間違えると検査で落ちること。
4. generator の host が CI に既にあるもので済む。この repo では Node
   (`web/scripts/*.mjs`、typescript は依存に入っている)、.NET(tcs、
   Roslyn)、Lua(runtime)の 3 つ。生成物は checkin して、C のビルドは cmake
   だけで通す。再生成の diff 検査は format --check と同じ形で CI に置く。
5. 記述自体が機械検査できる。壊れた記述は CI で落ち、Lua binding が記述の
   member を過不足なく登録しているかも検査できる。
6. 人が手で書く量として現実的。関数 50 弱、enum 10、構造体 10 程度。

### 決定: C# stub を記述にする

比べた 4 つ。

- WebIDL。dictionary(省略可能 member + 既定値)が option table にそのまま
  対応し、namespace、sequence、nullable、拡張属性による注釈があり、parser は
  webidl2 が npm にある。弱いのは enum が文字列 enum で整数値を持てないこと、
  out 引数や多値返しが無く拡張属性で表す必要があること、固定長配列や借用と
  いった C ABI の概念が無いこと、doc comment の標準が無いこと。
- TypeScript の宣言ファイルの部分集合。`?` による省略可能 field、数値 enum、
  namespace、tuple 型による多値返しと固定長配列、JSDoc による doc comment が
  そのまま使え、parser は既に依存にある typescript で、editor 支援も付く。
  弱いのは表現力が大きすぎて部分集合を lint で縛る必要があること、面ごとの
  注釈が JSDoc タグという文字列の慣習になり、型つきで検査されないこと。
- C# stub を記述にする。`cs-lib/lub_stub.cs` が既に 2199 行あり、tcs の面は
  生成が不要になる。struct、整数 enum、out、`ReadOnlySpan<byte>`、delegate が
  C ABI にほぼ 1 対 1 で、面ごとの注釈は attribute として compiler が検査し、
  doc comment は XML doc で API docs の生成元になる。記述が compile される
  こと自体が検査になる。弱いのは中立表記が C# の形になること(field と
  property、nullable 参照型、class と struct の区別が記述に混じる)で、
  宣言的な部分集合に縛る analyzer が要る。再生成には dotnet が要るが、CI と
  tcs 経路で既に前提。
- 自前のデータ形式(JSON か Lua table)。parser が不要で、必要な構文だけを
  持てる。JSON は comment が書けないので doc が文字列 field になり冗長。Lua
  table は読みやすく generator を Lua で書けば runtime と同じ言語になるが、CI
  に Lua interpreter を用意する手間と、記述の型検査を自分で書く手間がある。

C# stub を記述にする。既に存在すること、記述の検査を compiler と attribute に
任せられること、.NET 実行を維持する経路と決めた以上 C# の型が C ABI に
近いのは利点になること、tcs が既に Roslyn でこの stub を読んでいるので
generator の足場があること。generator は lub 側の `tools/` に dotnet project
として置き、Roslyn で stub を読む。stub は手書きで、facade と header と Lua
binding が生成物。API docs は stub の XML doc から生成し、実行形で共有する。

## 数値型

lub の Lua の number は double、integer は 64 bit。`third_party/lua/luaconf.h`
で `LUA_32BITS` はコメントアウトのままで、CMakeLists.txt にも数値型を変える
define は無い。「float にした」は tcs の IL 設計のもの(数値は i32 + f32 のみ、
Lua 実行は `LUA_32BITS` ビルド、移行は M4 = T216、tcs 側の 32 bit ビルド整備は
T213)で、lub の Lua ビルドには入っていない。今の tcs の Lua 出力は float も
double も number に落とし、整数除算だけ `__tcs_idiv` で C# 規則に補正している。

今の面で 64 bit と double が出る場所を数えた。

- `double` は stub の全域(`OnFrame(double dt)`、`mouse_pos`、`clear_color`、
  `List<double>` の頂点データ、`interleave_*` の戻り値)。Haxe の Float の
  写しで、M4 の部分集合(double と long はサブセット外)ではコンパイルでき
  ない。C API を定義する時点で面を float と `float[]` に切り替える。
- 64 bit 整数は `file_mtime`(ns を int64 で返す)と `fnv1a64` の 2 つだけ。
  使っているのは `samples/lub_io.lua` と `samples/lubx_png.lua` の cache 更新
  判定と `Sys.hx` の宣言だけで、この 2 つの Lua は C に移すので、64 bit の
  値がゲームの面に出る箇所はゼロになる。
- Bytes の長さは size_t で、int32 の 2 GB で足りる。
- 絶対時刻の API は無い(dt だけ)。float 秒の累積は精度が落ちるので、必要に
  なったら int32 の frame 番号か ms を足し、double は足さない。

`LUA_32BITS` への追従は tcs→C のためだけでなく正しさのために要る。今の
64 bit 整数の Lua では C# の `int` の wrap が再現されず、tcs→Lua と .NET 実行は
既に overflow で食い違う。なので追従は順序の末尾ではなく、C API 定義の直後、
differential 比較を始める前に置く。stock の luaconf は `LUA_32BITS` で int32 と
float を束で切り替える作りで、上のとおり面から 64 bit を消せばそのまま使える。

追従時に golden は全部撮り直す。risk が 1 つあり、Lua の `math.sin` は float
ビルドで `sinf` になり、glibc と MSVC の実装差が digest に出る可能性がある。
今の golden は 86 枚を linux と windows で同じファイルと byte 比較して通って
いるので double では揃っているが、float で揃う保証は無く、再生成時に両 CI で
確認する。

## 順序

上ほど先で、後の項目を縛る。6 までは lub 側だけで進み、9 は tcs の IL 計画と
同期する。

1. 記述形式は決まった(C# stub)。generator の骨組みと、handle・status・view の
   語彙を stub の attribute として決める。
2. 写像規則を tcs の emit に入れ、`--no-naming-check` を消す。C API と独立に
   進められる。
3. C API を定義する。wire を触る 1 回はこれで、prefix 重複、`DONTCARE`、
   `on_init`、`lub` 単一テーブル化、面の float / int32 化、Io と Png の C への
   移動、`object` 引数 92 箇所の型付け、所有権の 3 規則をここで入れる。header
   は tcs→C が include する契約になるので、tcs の M2(IL 仕様と metadata)と
   突き合わせる。
4. generator を書き、header・Lua binding・API docs を生成物にする。
5. `LUA_32BITS` に追従し、golden を全部再生成して両 CI で確認する。
6. facade と C# の host とテンプレートを作り、.NET 実行の golden と digest
   比較を CI に足す。`templates/game/` を csproj に置き換える。
7. tcs にモジュール Lua を吐くモードを足し、lubx の生成 Lua と raw Lua の
   サンプルを置く。
8. 全サンプルと ngs を C# に移植し、API docs の生成元を移し、Haxe を撤去する
   (playground の言語トグルもここで落とす)。
9. tcs→C を受ける。tcs の M3 待ち。

## 捨てた案

- lubx を Lua で書く。raw Lua と tcs→Lua は幸せだが .NET 実行と tcs→C に
  穴が空き、lubx 内部の型検査も失う。
- lubx を C で書いて runtime に取り込む。全言語に等しく速く届くが、lubx が
  ホットリロードの外に出る。`docs/api-glue.md` が二重実装を許容している
  根拠(ユーザーが読み、書き換えられること)を正面から捨てることになり、
  Rect / Color / Rand のような小物なら安いが、SpriteBatch / Text /
  Renderer3d を落とすとエンジンの性格が変わる。
- .NET 実行を計測台か R5 の逃げ道に留める。ユーザに選ばせて競う形にしたので
  維持する経路になった。
- C API を callback なしにする。判定を返す callback と buffer を編集して返す
  callback が書けなくなる。規約を 1 つに固定すれば持ち込めるので、規約の側で
  解いた。
- runtime 所有の object を retain / release や finalizer で管理する。frame 有効
  の view と keyed resource の 2 つで足り、tcs→C の object model に依存しない。
- header を記述にして annotate する。C の parser が要り、Lua 固有の注釈を C に
  押し込むことになる。
- 名前の対応を IDL に運ばせる。規則で表せるので表が要らない。
- PascalCase の global namespace を維持する。Lua だけで使う出口として弱く、
  小文字にするなら単一テーブルしか衝突を避ける形が無い。

## 残る未決

- tcs 側の時期と形。M2 / M3 / M4 の時期、IL→C の生成コードが C API を呼ぶ
  最終形、prebuilt player + dll / bundle の可否。
- event struct の中身と `Sdf` の木の平坦化の具体形。C API 定義(順序 3)で決める。
- float ビルドでの `sinf` 等の実装差が golden に出るか。順序 5 で実測する。
- tcs のモジュール Lua 出力モードの重さ。順序 7 で実装を見て測る。
