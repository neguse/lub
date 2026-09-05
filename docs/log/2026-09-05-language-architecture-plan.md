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
| 3 | C API を定義する(wire の変更はここに集める) | 3a: `include/lub/lub_api.h` と gfx(`src/api_gfx.c`)。3b: config / quit / input / sys / profiler / host / audio(`src/api_sys.c`, `src/api_audio.c`, `src/host.c`)。3c: io / png を C に移す(`src/api_io.c`、`samples/lub_io.lua` と `lubx_png.lua` は Haxe 向けの alias だけ)。3d: font / ui / mesh(surface_nets / sdf)を C に移す(`src/api_font.c`, `src/api_mesh.c`、`src/ui.cpp` は C API を直接出す)。3e: phys2d / phys3d を C に移す(`src/physics_box2d.c` / `src/physics_box3d.c` は Lua を含まない core + C API、Lua 面は `src/lua_phys2d.c` / `src/lua_phys3d.c`)。3f: stub の `object` 引数と戻り値を typed な class にする(残る `object` は draw / dispatch の bindings の `Dictionary<string, object>` だけ)。3g: 所有権の規則(Bytes は frame 有効の view、readback と snd は key で宣言する resource)。3h: wire の整理(`DONT_CARE`、desc の別名 field の統一、文字列 enum の enum 化)。Lua binding は詰め替えだけに。`lub` 単一 table 化と `onInit` fallback の撤去は Haxe 撤去(段階 8)まで残す |
| 4 | generator で header・Lua binding・API docs を生成物にする | 4a: `include/lub/lub_api.h` を stub から生成(`tools/lub-gen -- header`)。4b: Lua binding を生成(`src/gen/lua_api_gen.c`、土台は `src/lua_gen_support.c`)。物理の core は内部形との詰め替え(`src/physics_box2d_api.inc` / `physics_box3d_api.inc`)。4c: API docs のデータを生成(`web/gen/lub-api-docs.json`、`tools/lub-gen -- docs`)。`web/scripts/gen-api-docs.mjs --source stub` が読み、docs.ts の形に変換する。既定の source は Haxe のままで、段階 8 で切り替える。再生成は `scripts/gen-api.sh`、差分検査は native gate |
| 5 | `LUA_32BITS` に追従し golden を再生成 | CMake で `lua_static` に `LUA_32BITS` を PUBLIC 定義(整数 32 bit、実数 float)。64 bit を返す唯一の Lua 面だった `file_mtime` global(Haxe の `Sys.fileMtime`)は使い手が無いので消す。linux の golden(sdlgpu / C# / web)は手元で再生成。windows の `_native.png` と、手元の lavapipe で CI と一致しない 5 枚(11_shadow、12_sfb、18_coin_pusher、19_sdf、26_renderer3d)は CI の artifact から更新する |
| 6 | facade・C# host・テンプレート、.NET 実行の golden と digest 比較 | host API(`include/lub/lub_host.h`、手書き)と共有 library(CMake `lub_shared`、object library を player と共有)。facade は生成物(`dotnet/Lub/Lub.g.cs`、`tools/lub-gen -- facade`)、土台と host は手書き(`dotnet/Lub/LubRuntime.cs`、`LubHost.cs`、`Lub.Run(typeof(Game), args)`)。サンプルは `dotnet/SampleRunner`。`--digest` で frame ごとの C API 呼び出しの構造を hash し、native gate が tcs→Lua と .NET で比較する。`templates/game/` は csproj(tcs→Lua と .NET の両方)。golden は `_dotnet_sdlgpu.png`。windows CI の .NET step(lub.dll、digest 比較)は手元で確かめられないので CI で見る |
| 7 | tcs のモジュール Lua 出力、lubx の生成 Lua、raw Lua サンプル | tcs に `--module`(定義した型の table を返す)を足した。`samples/lubx.lua` は cs-lib から `scripts/gen-lubx-lua.sh` が生成する checkin 済みの Lua で、native gate が `--check` で差分を検査する。raw Lua サンプルは `samples/27_lua_triangle`(lub table だけ)と `samples/28_lua_sprites`(`require("lubx")` で SpriteBatch)。golden は `<name>_lua_sdlgpu.png` |
| 8 | 全サンプルと ngs の C# 移植、API docs の生成元移行、Haxe 撤去 | ngs を C# に移植(5 scenario の capture が Haxe 版と byte 一致)。API docs の生成元を stub の XML doc に移した(Haxe の doc comment 95 件のうち名前で対応が取れた 54 件を stub に写した。`gen-api-docs.mjs` は stub の JSON だけを読む)。Haxe を撤去した: haxe-lib / haxe-wasm / 全 .hx と .hxml / native の haxe pipeline(`--serve` は csproj を受ける)/ prelude と PascalCase alias / playground の Haxe コンパイラと言語トグル。Lua の面は生成 binding の `lub` table だけになり、tests/lua と golden の Lua テストはその名前に書き直した。golden(`_sdlgpu.png` / `_web.png`)は C# 版で撮り直し、`_cs_sdlgpu.png` は役目を終えて削除 |
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
  返す。Lua binding は 3g までは従来の `Bytes`(所有)に copy して返した。
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
- stub の `object` は typed な class に置き換えた。desc は Lua 面の table の
  形をそのまま class にし(field 名は snake_case に写像される)、戻り値も
  class にする(`WorldInfo`, `ShapeView`, `RayHit`, `JointInfo` 等、3D は
  `...3d`)。Lua 面が値の型で分岐していたところは C# の名前を分ける:
  `Pose(body)` と `PoseByKey(world, key)`、`UseTexture(List<int>)` と
  `UseTextureBytes(Bytes)`、`Pcm(List<double>)` と `PcmBytes(Bytes)`、
  `Raycast` / `ShapeCast`(最寄り)と `RaycastAll` / `ShapeCastAll`(visitor
  付き)、3D の `JointMotorTorque`(数値)と `JointMotorTorqueVector`
  (spherical)。Lua 面は prelude の alias(`pose_by_key = pose` 等)で同じ
  関数を指す。
- 2 値の material(名前か整数)は Lua 面に `material_name` を足して
  `MaterialName` / `UserMaterialId` に分け、`material` は Haxe 向けに残す。
- visitor は C API の規約で型を付ける: raycast / shape_cast は
  `Func<RayHit, double>`(-1 = 無視、0 = 打ち切り、fraction、1 = 続行)、
  overlap / collide_mover は `Func<ShapeView, bool>`。
- sdf の木は C# 側で typed な `SdfNode`(op / params / 子参照)にし、
  `Sdf.Mesh` が C API の平らな配列(`SdfNodeDesc`: op, a, b, params, name)に
  落として `Lub.Mesh.SdfMesh(nodes, root, n, skinK)` を呼ぶ。Lua binding は
  平らな配列と入れ子の table(Haxe の Sdf)の両方を受ける。op の enum は
  `Lub.Mesh.SdfOp`(Lua は `lub.mesh.SPHERE` 等の定数)。
- texture の px は byte 値の `List<int>`(Lua binding は 0..255 に丸める)。
- glTF は `GltfMesh` / `GltfPrimitive` / `GltfMaterial`(MeshData 派生)で
  受け、`Interleave*` は `MeshData` を取る。
- 所有権の規則(設計記録の 3 規則)を Lua 面まで入れた(3g)。`Bytes` は
  frame 番号つきの view userdata で `__gc` を持たず、png_load / readback /
  audio_decode の結果を zero copy で指す。古い frame の view を渡された API
  (use_texture / png_write / audio_snd / font)と `.length` は error にする。
  view の実体は `App.frame_garbage` に預けて `app_frame_end` で free する
  (readback の pixel、decode の PCM)。png の pixel は io cache の entry が
  持つ。
- readback は `readback(key)` の sentinel table(`__lub_kind = "readback"`)
  で、id は int32 の user token をそのまま runtime の token にする(Lua 値の
  対応表は消えた)。queue は最後に poll された frame を持ち、
  `resource_sweep_after_frames` の間 poll が途切れると sweep。
- audio の snd は `audio_snd(key, data, channels, rate, version)` で宣言する
  resource になり、`audio_pcm` / `audio_free` は消えた。version の規約は
  use_buffer と同じで、同じ version なら data を読まない(Lua binding は
  table の変換もしない)。宣言が途切れた key は sweep で snd を退役させ
  (`audio_snd_retire`)、鳴っている voice が離れてから frame_end で PCM を
  回収する。内容 dedupe はそのままなので複数の key が同じ snd を指しうる。
  退役は他の key が指していないときだけ。同じ内容で宣言し直せば退役が
  解ける。lubx の `Sfx` は波形を cache して呼ぶたびに version 1 で宣言し直す。
  20_audio は key "lab" を毎フレーム宣言し、パラメータが変わったら version
  を進める(遅延 free の仕組みは消えた)。
- wire の整理(3h)。`DONTCARE` は `DONT_CARE` に直した(Haxe extern は
  `@:native("DONT_CARE")`)。C 名は `lub_<ns>_<member>` で揃っていて、root の
  `Lub.Config` / `Lub.Quit` だけ namespace 無しの `lub_config` / `lub_quit`。
  Lua の PascalCase global と `onInit` fallback は Haxe が使うので段階 8 まで
  残す。desc の別名 field(`A` / `BodyA`、`X, Y, Dx, Dy` / `Origin` /
  `Translation` / `Delta` / `To`、`Px, Py` / `Point`、`Material` /
  `UserMaterialId` / `MaterialId` 等)は段階 4 の header 生成の前提として
  1 概念 1 field に統一し、文字列で持っていた enum(joint の type、shape の
  kind、event の kind、proxy の kind、io / readback の status)は C# の enum に
  する。Lua 面は従来どおり文字列のまま(`[LubLuaString]`)。

## 段階 4 で決めたこと

- header は stub から規則で導く。stub は Lua の wire の形(C# のゲームコード
  が呼ぶ形)を記述したままにし、C の形は規則と少数の attribute で決める。
  desc を束ねる手書きの構造体(3 段階までの `LubGfxTextureDesc` 等)は
  やめ、C の関数は stub の関数と 1 対 1 にする。これで Lua binding、.NET の
  facade、tcs→C の生成がどれも同じ規則の詰め替えになる。
- 型の写像: `int` → `int32_t`、`double` → `float`、`bool` → `bool`、`string` →
  `LubStr`、enum → `int32_t`(C の typed enum は定数の宣言だけ)、
  `[LubHandle]` → `LubHandle`、`Bytes` → 入力は `const uint8_t *, int32_t len`、
  出力は `LubView`、`List<T>` → `const T *, int32_t count`(入力は借用、出力は
  runtime 所有の view)、`[LubArray(n)]` の `List<T>` / `T[]` → 固定長配列
  `T x[n]`(List は `x_count` 付き)、class → `struct`、継承は先頭に `base` と
  して埋め込む、`Func<...>` の field → `void *user` + 関数 pointer、
  `Dictionary<string, object>`(draw / dispatch の bindings)→ `LubBinding`
  (name + handle か float 列)の配列。
- 省略可能(nullable)の写像: 値・enum・入れ子 class の field は `bool has_x` +
  `x`、handle の field は 0、string の field は len 0、引数は pointer
  (NULL = 無し)。既定値は C の実装が `has_x` を見て入れる(`*_desc_init`
  は消える)。
- 関数の写像: `LubContext *ctx` を先頭に取り、戻り値は原則 `LubStatus`。
  C# の戻り値と `out` は C の out pointer。`[LubNoFail]` を付けた関数だけ
  bool / float / int32_t を直接返す(input / ui / profiler 等)。nullable の
  戻り値は class なら `LUB_NOT_FOUND`(対象が無い)、scalar なら `bool *has`
  (値が無い)。class の戻り値で「無い」が通常の結果(raycast の hit 等)は
  `[LubMaybe]` で `bool *has` にする。`List<T>` の戻り値は runtime 所有の
  view(`const T **items, int32_t *count`)。
- 面ごとの attribute は `[LubArray(n)]`、`[LubNoFail]`、`[LubMaybe]`、
  `[LubLuaString]`(enum を Lua 面では小文字の文字列で持つ)、`[LubBits]`
  (string field を Lua 面は hex 文字列、C は `uint64_t` で持つ。LUA_32BITS
  で 64 bit 整数を面に出さないため)、`[LubNoC]`(Lua / C# 面だけの関数。
  `readback(key)` の sentinel 作成)。
- C の名前: 型は `Lub` + C# の型名(`LubJointDesc`、`LubJointDesc3d`)、
  enum のメンバは `LUB_<NS>_<ENUM>_<MEMBER>`、関数は `lub_<ns>_<member>`。
  段階 3 の手書き header にあった `LubPhys2dXxx` の接頭辞や `LubVec2` は
  規則の出力(`LubVec2d`)に合わせて実装側を直す。
- overload は禁止(C 名が衝突する)。`UseBuffer` の空確保は `UseBufferEmpty`
  にし、Lua 面は prelude の alias で同じ関数を指す。`Readback.ReadTexture`
  は `Gfx.ReadTexture(rb, ...)` の関数にする(Lua 面の `rb:read_texture` は
  sentinel の metatable が同じ関数を指す)。
- 生成物: `include/lub/lub_api.h`(4a)、Lua binding の詰め替え(4b、
  `src/gen/lua_api_gen.c`。Lua の値の読み書き、sentinel、view userdata、
  callback の土台は手書きの `src/lua_gen_support.c`)、API docs の JSON
  (4c、stub の XML doc から)。再生成は `scripts/gen-api.sh`、差分検査は
  `scripts/gen-api.sh --check` を native gate に置く。Haxe が使う別名や
  多値の互換は `samples/lub_prelude.lua`(生成物の外)に寄せ、段階 8 で消す。
- 生成した Lua binding の形。`lub` table は C が作り(namespace table、
  関数、定数)、prelude は Haxe 向けの PascalCase table と flat global、
  2 形の吸収(`phys2d_pose(world, key)`、visitor 付き `raycast`、空確保の
  `use_buffer`、Bytes の `audio_snd`、入れ子の `sdf_mesh`)だけを持つ。
  record は table、runtime 所有の数値配列(mesh の positions、debug の
  segments 等)は frame 有効の view userdata(1 始まりの添字と `#` で
  読める。table に写さないので大きい mesh を毎フレーム返せる)。desc の
  数値配列は table でも view でも受ける(view なら zero copy)。
- 物理の core(`src/physics_box2d.c` / `physics_box3d.c`)は段階 3 の手書き
  header の形を内部の型(`src/phys2d_internal.h` / `phys3d_internal.h`、
  `P2` / `P3` 接頭辞)としてそのまま持ち、公開 API との詰め替えを
  `physics_box2d_api.inc` / `physics_box3d_api.inc` で行う。世界 anchor や
  euler の変換、query の結果配列(runtime 所有、同じ関数の次の呼び出しまで
  有効)、visitor の trampoline はここにある。callback の holder は
  `user_release` で core が手放すときに解放する。
- 2D の joint の `AnchorA` は `LocalAnchorA` の別名(旧 Lua 面の名前)。3D の
  `AnchorA` は世界座標で core が local frame に変換する。
- `Sys.FileMtime` は消した(絶対時刻は面に出さない。Haxe の `lub_io.lua` が
  使う `file_mtime` / `request_file` は手書きの global として残す)。
- gfx の `Lookup*` / `ResourceInfo` を stub に足した(sentinel の再解決と
  `TextureRef.Version` に要る)。
- 生成した binding は handle の sentinel に method table(`world:step(...)`
  の形。第 1 引数がその handle の関数を、kind の接頭辞を落とした名前で持つ)
  を付ける。`lub.__refs.<kind>` から引けるので、prelude が Haxe 時代の
  2 形の method(`world:pose(key)` 等)を重ねる。
- Haxe 向けの互換は `samples/lub_prelude.lua` に集めた: PascalCase の
  namespace table は生成物の table を `__index` に持つ写しで、旧 flat 名
  (`phys2d_world` / `ui_begin` 等)、camelCase の key の snake_case 化、
  別名 field(`a` / `b`、`x` / `y` の速度、`center`、`type` / `r` の proxy、
  `category` / `mask` の bit 番号、`dt`)、`points` の `{x, y}` 列、入れ子の
  sdf 木を吸収する。C# (tcs) の面は生成物の table をそのまま使う。
- query の visitor が error を投げたら `nil, "<関数名> visitor: <message>"`
  で返す(従来の契約)。対象が無い問い合わせは `nil, "not found"`。
- Lua のテスト(`tests/lua/test_physics_*.lua`)は正の名前(`material_name` /
  `material_id`、`category_bits` / `mask_bits`、`body_a` 等)に書き換えた。
