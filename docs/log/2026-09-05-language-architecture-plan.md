# 言語構成の実装計画 — 命名規則から C API まで

> 記録: 2026-09-05 に `2026-09-04-language-architecture-design.md` の「順序」を
> 実装に落とすときの作業計画と、実装中に決めたこと。設計の正は設計記録、
> 現状の構成の正は `docs/api-glue.md`。

## 段階

設計記録の順序 1〜9 をそのまま段階にする。各段階は CI(linux / windows /
web)が通る状態で区切り、1 段階 = 1 PR を基本にする。

| 段階 | 内容 | 状態 |
| --- | --- | --- |
| 1 | 記述形式(C# stub)の generator 骨組みと、handle / status / view の attribute 語彙 | `tools/lub-gen`(check / model / surface-test)と `[LubHandle]` / `[LubView]` / `[LubLuaName]` |
| 2 | 写像規則を tcs の emit に入れ、`--no-naming-check` を消す | tcs T231 / T232、stub と全サンプルの PascalCase 化まで |
| 3 | C API を定義する(wire の変更はここに集める) | 3a: `include/lub/lub_api.h` と gfx(`src/api_gfx.c`)。3b: config / quit / input / sys / profiler / host / audio(`src/api_sys.c`, `src/api_audio.c`, `src/host.c`)。3c: io / png を C に移す(`src/api_io.c`、`samples/lub_io.lua` と `lubx_png.lua` は Haxe 向けの alias だけ)。3d: font / ui / mesh(surface_nets / sdf)を C に移す(`src/api_font.c`, `src/api_mesh.c`、`src/ui.cpp` は C API を直接出す)。3e: phys2d / phys3d を C に移す(`src/physics_box2d.c` / `src/physics_box3d.c` は Lua を含まない core + C API、Lua 面は `src/lua_phys2d.c` / `src/lua_phys3d.c`)。Lua binding は詰め替えだけに。残り: `object` 引数の型付け、所有権の規則 (Bytes / Readback / view)、wire の整理 |
| 4 | generator で header・Lua binding・API docs を生成物にする | 未 |
| 5 | `LUA_32BITS` に追従し golden を再生成 | 未 |
| 6 | facade・C# host・テンプレート、.NET 実行の golden と digest 比較 | 未 |
| 7 | tcs のモジュール Lua 出力、lubx の生成 Lua、raw Lua サンプル | 未 |
| 8 | 全サンプルと ngs の C# 移植、API docs の生成元移行、Haxe 撤去 | 未 |
| 9 | tcs→C(tcs の M3 待ち) | 未 |

## 段階 2 で決めたこと

設計記録に無かった細部を、実装時に次のように決めた。

- 写像は tcs の emit に無条件で入れる(flag にしない)。tcs のテストの Lua 式は
  写像後の名前に書き換える。
- 写像の関数: C# 名の先頭を小文字にして lowerCamel にし、以降の大文字の前に
  `_` を入れて小文字化する。enum のメンバはその結果を全大文字にする。結果が
  Lua の予約語(`end` 等)になったら `_` を後置する(`end_`)。これは表では
  なく規則。
- 参照専用型(`--ref` の stub)の static アクセスは、入れ子の型名を全小文字に
  して `.` で結ぶ(`Lub.Gfx` → `lub.gfx`)。参照専用型に入れ子の enum は
  enum 名を省いて親の下に平らに置く(`Lub.Gfx.PixelFormat.Rgba8` →
  `lub.gfx.RGBA8`)。ユーザ型の型名は写像しない。
- C# の `const` は値を inline する(C# の意味論どおり)。stub の文字列定数
  (`Io.Pending` 等)はこれで Lua 側の名前に依存しない。
- stub は root の `Lub` static class に nested static class(`Gfx`, `Input`,
  ...)と nested enum を置く形にする。サンプルは `using static Lub;` で
  `Gfx.BeginPass(...)` と書く。
- Lua 面は段階 3 まで今の PascalCase global を残し、prelude に `lub.gfx` 等の
  小文字 alias と、`Phys2d` / `Phys3d` / `Ui` / `Audio` / `Font` / `Host` の
  短名 alias、`Gfx.DONT_CARE` alias を足す(追加のみ。旧名の撤去は段階 3)。
- entry callback は `on_init` / `on_event` / `on_frame` / `on_quit` を先に
  引き、無ければ従来の `onInit` 系に fallback する(Haxe 撤去まで)。
- Lua 標準ライブラリの stub(`os` / `utf8` / `string`)は消す。同じ C# が
  .NET でも通るように、tcs に `Environment.GetEnvironmentVariable`、
  `int.Parse` / `double.Parse`、`string.EnumerateRunes()` + `Rune.Value`、
  `(int)s[i]` を足して置き換える。
- `Ui.ui_begin` / `ui_end` は C# で `BeginWindow` / `EndWindow` にする
  (`end` は Lua の予約語)。
- 物理の option key は C 側が snake_case と camelCase を両方読む形が既に
  あるので、C# は snake_case 側に落ちる。
- 段階 1 の attribute は `[LubHandle]`(runtime 所有の不透明参照)、`[LubView]`
  (frame 有効の view)、`[LubLuaName]`(面ごとの上書き。今は未使用)の 3 つ。
  status は段階 3 の C API 定義で戻り値の型として入れるので attribute にしない。
- generator は tcs の `Transpiler.csproj` を参照して `LuaNaming` を共有する
  (写像の実装を 2 つ持たない)。生成物はいまのところ
  `tests/lua/test_api_surface.lua` だけで、native gate が再生成との差分を検査する。
- 物理の option key `MaterialId` は C が `material` / `materialId` しか読んで
  いなかったので `material_id` も読む(段階 3 で typed struct に置き換わるまで)。
- lavapipe の golden はこの機械の mesa が golden 生成時と違うため、Haxe 版も
  C# 版も同じサンプル(11_shadow / 12_sfb 等)で LSB 差が出る。C# 版の golden の
  正否は CI(windows WARP)で見る。

## 段階 3 で決めたこと

- C API の header は `include/lub/lub_api.h`。段階 4 で生成物にするまでは
  手書きで、subsystem ごとに順に移す(3a = gfx、以降 input / audio /
  physics / ...)。実装は `src/api_<subsystem>.c`、共有 helper は
  `src/api_internal.h`。`LubContext` は `App` そのもの。
- C の enum メンバ名は `LUB_<NS>_<ENUM>_<MEMBER>`(`LUB_GFX_BLEND_NONE`)。
  Lua の平らな定数と違い C は enum を跨いで同じ名前を置けないので、
  enum 名を挟む。関数は `lub_<ns>_<member>`、構造体は `Lub<Ns><Name>`。
- handle は entry(key)の寿命に結ぶ(sweep されるまで同じ値。version が
  変わっても handle は変わらない)。設計記録の「key + version に結ぶ」より
  緩いが、facade が version 変化のたびに再解決する必要が無くなる。stale
  handle は `lub_last_error` に回る。Lua 面の sentinel table は `handle`
  field を持ち、stale なら key から引き直す。
- readback は runtime が key で持つ queue になり、結果は frame 有効の view で
  返す。Lua binding は従来の `Bytes`(所有)に copy して返し、frame を跨いで
  持てる従来の契約を保つ(zero copy 化は所有権の規則を Lua 面に入れるとき)。
- version の面は int32(runtime 内部は int64 のまま)。
- io の file cache は runtime が path で持つ(`src/api_io.c`)。mtime の fast
  path と内容 hash の version は `lub_io.lua` と同じ意味論で、version は
  hash を int32 に畳んだ値。`load_floats` は `return { 数値, ... }` だけを
  受ける小さな parser で読む(Lua の `load` は使わない)。gltf は typed な
  `LubGltfView`(primitive ごとの平らな配列 + material)で返し、Lua 面の
  table は binding が組み立てる。interleave は C の `lub_mesh_interleave`
  で、Lua 面は table を配列に写してから呼ぶ。
- font / mesh の結果(glyph bitmap、glyph mesh、surface_nets / sdf の
  mesh)は runtime が同じ subsystem の次の呼び出しまで持つ view で返す
  (`LubView` / `LubMeshData`)。Lua binding は従来どおり table や `Bytes` に
  写して返す。`src/font.c` / `src/sdf.c` / `src/surfacenets.c` は Lua を
  含まない純関数にし、`tests/c/*_smoke.c` は C の関数を直接叩く。
- sdf の木は C では平らな `LubSdfNode` 配列(op、子の index、params、bone
  名)で受ける。Lua 面の入れ子 table は binding が平らにしてから渡す。C# の
  木も同じ配列に落とす(段階 3 の残りで `object` 引数を typed にするとき)。
- ui は `lub_ui_*` を C API そのものにする(`src/ui.cpp` の `extern "C"`)。
  Lua binding の `Ui` table は C API を 1 対 1 で包む。
- 物理の handle は world / body / shape / chain / joint の entry ごとに
  `PhysHandles`(`src/phys_common.h`)で振り、entry が prune されると stale。
  Lua 面の sentinel は従来どおり key(world / body / key)を持ち、呼び出しの
  たびに key で引き直す(handle は情報)。手で作った sentinel table も動く。
- callback(filter / pre_solve / friction / restitution)は C の関数ポインタ
  + user で world desc に渡す。Lua 面は world ごとの closure を registry に
  持ち、trampoline が pcall して error は 1 回 log して既定値で続ける
  (旧実装と同じ)。再入の拒否は core の `callback_depth` で、callback 内の
  変更 API は LUB_ERROR(Lua 面は "physics mutation is not allowed" の
  error)。query の visitor も同じ trampoline の形。
- 物理の filter bit は Box2D / Box3D の 64 bit mask をそのまま uint64 で持つ
  (面は int32 / float の原則の例外。Lua 面は hex 文字列、C# は ulong)。
- 3d の四元数は正規化済みが C API の契約。Lua 面の正規化と euler の合成は
  Box3D の inline 関数を使い、core で正規化し直さない(二重に正規化すると
  丸めが変わり、18_coin_pusher の golden がずれた)。
- 問い合わせの対象が無い/live でないときは `LUB_NOT_FOUND`(last_error は
  書かない)。Lua 面は従来どおり `nil, "not found"`。
