# tcs2c — TinyC# IL→C release backend

`tcs2c` は TinyC# source を `IlExport.Export` へ渡し、release 用の GNU C source
を生成する .NET 10 console application。class は type id 付き `calloc` struct、
array と `List<int>` / `List<float>` は型付き連続 buffer へ lower する。

## 使い方

```sh
dotnet run --project tcs2c -- ../tcs/samples/collision.cs -o collision.c
gcc -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  collision.c -o collision -lm
./collision
```

`-lm` は Math (sqrtf / floorf 等) を使う入力に必要。

結果が IEEE-754 で決まらない Math (Sin / Cos / Tan / Exp / Atan2 / Pow / Log) は、生成 C に埋め込んだ `runtime/math` の実装 (`tcs_sinf` など、static) を呼ぶ。Lua の `tcs_math` module と .NET の `TinySystem.Math` も同じ実装なので、どの backend とプラットフォームでも bit 単位で同じ結果になる。この実装は上の strict f32 の build flag (積和を融合しない) を前提にする。Pow の指数 2 は Lua の `^` と同じく乗算にする。

単一の `static void Main()` があれば生成 executable の entry にする。複数ある
場合は `--entry CLASS` で選ぶ。class library sample のように `Main` が無い入力も
全 method を C へ変換し、static initializer だけを実行する no-op entry を付ける。

## 対応面

IL 契約 (`IlExport`) に載る TinyC# は原則すべて C へ落とす。意味論の正本は
Lua backend (dev) で、`tcs2c.Tests` が同じ source の stdout 一致を要求する。

- 型: `int` / `float` / `bool` / `string` (immutable byte 列、literal は static
  object) / `char` (整数 code unit。`string.char` / `Char.*` intrinsic) / enum (整数定数、`ToString` も
  整数表記) / class 参照 / データ struct (C 値型) / `T[]` (inline 要素の固定長
  配列) / `List<T>` (growable buffer) / `Dictionary<int|string, V>` (chained
  hash) / `Action` `Func` (closure = lifted 関数 + 捕捉 cell)
- class: 継承 (prefix layout)、virtual dispatch (type id switch)、`base.M`、
  ctor 連鎖、`is T` / is-pattern、static field / property、auto property
  initializer、upcast、明示 downcast (`IlCast`: 実行時型が合わなければ fault)
- user-defined operator (`+ - * / %`、単項 `-`): IL の時点で Roslyn が選んだ
  overload の static method への `IlCall` (同じ operator の overload は Lua 出力と
  同じ `__mul_1` `__mul_2` … の別関数)。virtual と
  同名で別シグネチャの method は override ではなく別 method として扱う
- struct / record struct: 素の C 値型 (配列 / List / field に inline、代入 =
  値 copy)。instance method / property accessor は `Tcs_S *self` で格納場所を
  直接指す (変数 receiver はコピーゼロ、rvalue receiver は C# と同じく一時値)、
  explicit / positional ctor は zero 値 → initializer → 本文、record struct の
  `==` / with、List.Contains / IndexOf / Remove の memberwise 等価、
  `new S[n]` / `default(S)` の zero 値
- record class: positional ctor、`==` / `!=` の構造等価 (型ごとの比較関数、
  継承は実行時型へ dispatch、string は内容、record は再帰、struct は
  memberwise)、`with` (実行時型の layout で shallow copy)、分解代入、
  List.Contains / IndexOf / Remove も構造等価
- 式・文: 数値演算 (i32 wrap、f32 strict、`/` は Lua と同じ float 除算、
  `(int)f` は 0 方向 truncation)、文字列連結・補間 (`string.format`:
  `%d %s %f %e %g %x %c`、幅・精度・`-`/`0` flag)、三項・`??`・switch 式、
  while / do-while / for / foreach (List / array / Dictionary /
  `EnumerateRunes`)、break / continue、IIFE (GNU statement expression)
- runtime 表面 (`runtime/tinysystem.lua` と同じ意味論):
  - List: Add / Count / index / Remove / RemoveAt / Clear / Contains / IndexOf /
    Sort (自然順・comparison) / Where / Select / Any / All / First / Last /
    FirstOrDefault / LastOrDefault / Count(pred) / Sum / Min / Max / OrderBy /
    OrderByDescending / Take / Skip / ToList / ToDictionary (要素型ごとの
    inline loop。Sort は .NET と同じ introsort、OrderBy は安定 merge sort)
  - Dictionary: index get / set (indexer initializer 含む)、Add、ContainsKey、
    TryGetValue、Remove、Count、Keys / Values、foreach (KeyValuePair)
  - String: Length / index / Contains / IndexOf / Replace / StartsWith /
    EndsWith / Trim / Substring / Split / Join / IsNullOrEmpty / ToUpper /
    ToLower、`int.Parse` / `float.Parse`
  - Math (`Math` / `MathF`): Min / Max / Abs / Sign / Clamp / Floor / Ceiling /
    Round / Sqrt / Sin / Cos / Tan / Atan2 / Pow / Exp / Log / PI
  - Random: `new Random()` / `new Random(seed)` (GC object) / `Random.Shared` /
    Seed、instance の Next / Next(max) / Next(min, max) / NextFloat / NextSingle
    / Range
    (Lua 5.5 の xoshiro256** を LUA_32BITS 構成のまま移植。seed 固定時に
    Lua backend と bit 一致)
  - Console.WriteLine / Write、`Environment.GetEnvironmentVariable`
- fault: null / bounds / 0 除算 / key-not-found / 空列 First 等は
  `tcs_fault(kind)` で stderr へ出して exit 1 (il-spec §12)

明示エラー (未対応): `object` 型の local、
`StringBuilder`、int / float 以外の `CompareTo`、Lua 固有の `IlIsLuaType`。Lua backend 側の既知差異は
参照型要素の `new T[n]` (要素 nil / `.Length` 0) のみ (support-matrix 参照)。

通常の `print` は stdout へ値を出す。digest kernel 回帰用だけは
`--digest-f32` を付け、各 f32 の bit 列を FNV-1a へ直接投入する。

```sh
TCS_ROOT=../tcs bash tcs2c/verify-digests.sh
```

## GC

生成 C は自前のフレーム同期・世代別 GC を runtime prelude に含む (T252)。
保守的な C stack 走査は無く、root は全部精密。

- **heap は精密**: 全 heap object は `TcsGcHeader` (kind + `TcsLayout`) を
  持つ。生成側が class / struct ごとに pointer slot の byte offset 表
  (`tcs_layout_C_<Class>` / `tcs_layout_S_<Struct>`、struct-in-struct は
  `offsetof` 加算で平坦化) を出し、array / List / Dictionary / closure cell
  は確保時に要素 layout を受け取る。int / float field を pointer と誤認
  することはない
- **nursery (若い世代)**: 確保は bump pointer の chunk 列 (`TCS_GC_NURSERY_CHUNK`
  = 256 KiB)。フレーム中 (entry の呼び出し中) は一切 GC しない。足りなければ
  chunk を足すだけなので、C stack 上の pointer が無効になることは無い
- **フレーム境界** (`tcs_gc_frame`): 最外の entry から戻った点 (= C# の
  スタックが空) で自動的に踏む (`tcs_lib_gc()` は明示の境界)。static field / host の hold slot /
  dirty な旧 object から到達する若い object だけを旧世代へ copy して
  slot を昇格先に書き換え (Cheney 式、forwarding は header の `next`)、
  残りの nursery は先頭 chunk を zero に戻すだけで一括解放する
- **旧世代**: malloc 個別 + 連結 list の非移動 mark-sweep。root は static と
  hold slot だけ (境界なので若い object も stack 上の参照も無い)。直近
  mark-sweep 以降の昇格 bytes が threshold (直近 GC 後の生存 bytes、下限
  `TCS_GC_MIN_THRESHOLD` = 1 MiB) に達した境界で走る
- **ライトバリア** (`tcs_wb(owner)`): 旧 object の参照型 slot (class field /
  struct 連鎖 field / 配列・List 要素 / 捕捉 cell / Dict 値) への store で
  owner を dirty 登録する。生成側が store 文に挿し、runtime は List / Dict
  の内部 store (`tcs_list_add` / `tcs_dict_put` / 再確保) に挿す。struct
  method は `self` に加えて所有 heap object (`v_owner`、無ければ NULL) を
  受ける。static は毎境界で全走査するのでバリア不要
- **string literal は static object** (`TCS_GC_STATIC`): 評価ごとの確保を
  しない。昇格 / mark / sweep の対象外
- **実行形 (`main`)**: Main 全体が 1 フレームで GC は走らない (全確保が
  nursery に残る)。GC が意味を持つのは lib 出荷形
- **`--lib` の host 規約**: `tcs_lib_init()` の後、entry
  (`tcs_entry_<Class>_<Method>`) を呼ぶ。各 entry の最外呼び出しから戻る点が
  フレーム境界 (典型は毎フレームの update)。host が tcs の object pointer
  を境界を跨いで持つなら、その slot を `tcs_lib_hold(void **)` で登録する
  (境界で昇格先に書き換わる)。`tcs_lib_release` で外す。登録しない pointer
  は境界の後は無効。
  entry に外部データ class の record を渡すときは、`tcs_new_<型>()` で作って field を埋め、すぐに entry に渡す (作ってから渡すまでに `tcs_lib_gc` / `tcs_lib_collect` や別の entry を挟まない)。
  entry から戻った後は host の pointer は無効で、game が保持した record は境界で昇格する。
  `tcs_lib_collect()` は境界 + 旧世代 full GC、
  `tcs_lib_heap_bytes()` / `tcs_lib_heap_objects()` は managed heap の統計
- **検証**: `-DTCS_GC_STRESS=1` で毎境界に旧世代 full GC を回し、nursery
  chunk を 256 byte にする (若い object が chunk を跨いで散り、解放済み
  chunk の再利用で死んだ参照が速やかに壊れる)。root / バリア漏れは生きて
  いる object が回収 / 上書きされて出力が変わる形で現れるので、
  `tcs2c.Tests` は lib 形で Setup / Frame×N / Report を回し C (通常 +
  stress) と Lua の stdout 一致を要求する
- **有界全列挙** (`tcs2c.Tests/GcEnumeration.c`): 本物の runtime を arena
  (`-Dmalloc=` 等で差し替え) + snapshot / restore に載せ、mutator の op
  (new / static・hold root 代入 / field store / `List<Node>` Add・RemoveAt /
  `List<int>` Add / Dict put・Remove・rehash / 境界) を深さ制限 DFS で
  列挙し、境界ごとに shadow graph と実 heap を root から突き合わせる。
  性質は NoDangling (到達可能な slot は shadow と同じ object を指し
  nursery を指さない)、NoLostObject
  (到達可能な object は旧世代に 1 つずつ)、OldGenSound (旧世代の list =
  到達集合。DIRTY / FORWARD / MARK は境界後に残らない)。`tcs_dict_remove`
  がバリア無しで済む前提 (そこのコメント: put は若い node を chain の先頭に
  足し、rehash は全 node を dirty にする) は、rehash → Remove → 境界を含む
  列挙で成立を確かめた契約。バリア / root / 昇格 / mark を
  1 箇所ずつ落とした variant はすべて最短の op 列 (witness) 付きで赤になる
  (`GcEnumerationTests`)。列挙が示した効き方: `tcs_list_reserve` のバリアは
  要素 layout の無い `List<int>` でだけ効く (`List<Node>` は `tcs_list_add`
  のバリアが覆う)。`tcs_dict_grow` 末尾の `tcs_wb(dict)` は唯一の呼び出し元
  `tcs_dict_put` の末尾のバリアに内包されていて、落としても違反は出ない

runtime は単一 thread 前提。GC の統計 (`tcs_gc_frames` / `tcs_gc_collections`
/ `tcs_gc_promoted_bytes` / `tcs_gc_live_bytes` / `tcs_gc_nursery_bytes` /
`tcs_gc_nursery_chunks`) は translation unit 内の static 変数。

## テスト

```sh
dotnet test tcs2c.Tests        # C compiler (gcc / cc / clang) が無ければ skip
```

`tcs2c.Tests` は同じ TinyC# source を tcs2c→C→cc と tcs→Lua→lua32 で実行し
stdout を突き合わせる 2 backend differential (GC stress 込み) と、GC 固有の
テスト (lib 形でフレームを跨ぐ各 root / ライトバリア経路の保持、境界での
garbage 解放と heap の有界性、host の hold / release)、GC の有界全列挙
(`GcEnumerationTests`、上記) を持つ。

## IlExport 契約

型と初期化情報は T228 契約 (`IlMethodInfo.ReturnType` / `ParameterTypes`、
`IlFieldInfo.Init`、`IlNewArray.ElementType` / `Length`、`IlTable.ElementType`) だけ
から読む。第一 milestone の `SourceFacts` / Roslyn 再解析 bridge は廃止した。

現契約には constructor 本文と local の宣言型が無い。そのためこの slice は、
initializer 付き local の型を IL から推論し、引数付き `IlNewObj` は引数を宣言順の
instance field へ代入する positional constructor に限定する（0 引数 default
constructor も可）。initializer 無し local、positional で表せない constructor、
method overload、継承、List の int/float 以外は対象を含む明示 error で拒否する。

生成 C は GNU statement expression で operand / argument の左→右評価を固定する。
strict f32 build では `-ffp-contract=off`、`-fwrapv`、
`-fexcess-precision=standard` を必須とする。

## 配列 / 引数 / 数値 / generic (master 系列の取り込み)

- **generic class の単相化**: top-level の generic class は IL export の前に
  使用型ごとの閉じた class (`TcsClosed{n}_{Name}`) へ展開する
  (`IlSpecialization`。tcs2c の CLI は常時 on)。instance ごとに field / static
  を別に持ち、制約付き基底・配列・factory lambda は具体型のまま。abstract
  method は dispatcher に参加する (本体は `tcs_fault("abstract-method")`)。
  入れ子 / partial / open な generic は対象外、上限は 256 class / 128 回
- **interface**: `IlClassInfo.IsInterface`。interface 型の receiver への呼び
  出しは実装 class への dispatch (`tcs_dispatch_<Iface>_<m>`、基底 class の
  実装も対象)。cast は `tcs_is_<Iface>` (実装 class の type tag 集合) で検査
- **配列 literal** (`new T[] { ... }`、入れ子 / 空も) は固定長 `TcsArray`。
  ctor / method / foreign 呼び出しは末尾の省略可能引数を IL の
  `ParameterDefaults` で補う。float literal / const は `IlLit.Type` で F32 を
  保つ (`2f` が整数に化けない)
- **static field の初期化順**: 定数でない initializer を持つ class は C# と
  同じく最初のアクセスで初期化する (`tcs_sinit_<Class>`、accessor
  `tcs_sp_<Class>_<field>()` 経由。前方参照 / 循環は初期化中の値 = default
  を読む)。定数だけの class は `tcs_init_statics` の先頭で一括 (遅延 class
  の initializer が accessor なしで直接読むため、宣言順に関係なく先に埋める)。
  `tcs_init_statics` は遅延 class を宣言順に触る
- **object**: `CType.Object` = `void *`。参照型はそのまま、`int` / `float` /
  `bool` は `TcsBox` に box。cast / unbox は GC header の `type_id` で検査
  (class は `tcs_init_<Class>`、string は `tcs_string_new`、box は
  `tcs_box_*`、配列 / List / Dict は生成時 `tcs_typed` で構造型 id) し、
  不一致は `tcs_fault("invalid-cast")`。配列の要素型も区別する
  (`(float[])` に `int[]` は fault)。`o is List<float>` / `o is float[]` などの
  型 test も同じ構造型 id を比べる
- 検証: `bash tcs2c/verify-game-core.sh` (配列 / 省略引数 / 数値 / 文字列 /
  generic / static 初期化)、`bash tcs2c/verify-object-values.sh` (object の
  root 保持 / interface dispatch / 不正 cast)。期待出力は Lua backend と同じ
  (`4.2949673E+09` 等)

## host 境界 (`--ref` / `--lib`)

`--ref STUB.cs` は入力が使う static method / static field / enum 定数 /
データ class を stub から取り込む (本体はコンパイルしない)。生成 C は
`extern` の `tcs_host_<lua path を _ 連結>` を宣言し、host がそれを実装する
(`tests/foreign-host.c` のように生成 C を include して内部 ABI を直接使って
良い)。

- static field は引数なしの getter (`T tcs_host_api_current(void)`)、enum
  定数は整数 literal に畳む
- 外部データ class の instance method は `tcs_host_<class>_<method>(self, args...)`
  (受け手が null なら fault。基底 class の宣言も受け手の chain から引く)。
  user subclass が同じシグネチャで再宣言 (override / 隠蔽) していれば、
  実行時型で user 実装と host 関数を振り分ける (シグネチャの違う同名は飛ばして
  祖先の実装を探す)。`base.M(...)` は振り分けず host 関数を直接呼ぶ
- `out` parameter は pointer 渡し (`TcsString **`、`int32_t *`)。戻り値のある
  out 呼び出しと同名 overload は拒否。`out _` は呼び出し側の一時変数
- nullable スカラ (`int?`) は `TcsOptI32` 等の **値渡し** (box しない)
- 外部データ class は `Dictionary` の key にできる。同一性は `host_value`
  (同じ handle の別 wrapper で引ける。`host_value` が 0 の object は pointer
  同一性)。`==` は pointer 比較のまま
- 外部データ class (`IsExternal`) は `type_id` の直後に GC が見ない
  `uint64_t host_value` を持つ (host 側 handle)。`new Options { X = ... }` は
  `tcs_new_Options()` + field 代入
- `--lib` は構造型 id を `TCS_TYPE_ARRAY_F32` / `TCS_TYPE_ARRAY_I32` /
  `TCS_TYPE_LIST_F32` / `TCS_TYPE_LIST_I32` / `TCS_TYPE_DICT_STRING_OBJECT`
  として出す (値は入力ごとに変わる)。host は `object` で受けた値を
  `TCS_GC_HEADER(p)->type_id` で見分け、自分で作る値には `tcs_typed` で付ける
- host が entry の外で tcs の pointer を持つなら `tcs_lib_hold(void **)`
  した slot 経由 (境界で昇格先に書き換わる)。それ以外の借用 pointer は
  entry から戻った時点で無効 (string は `tcs_string_new` で managed に写す)
- `--lib` の entry (`tcs_entry_<Class>_<Method>`) は、引数が `int` / `float` / `bool` と外部データ class (`--ref` の stub が宣言した class 自身で、`Tcs_<型> *` で受ける) で、戻りが `void` の static method。
  最外の entry から戻った点で自動的にフレーム境界 (`tcs_gc_frame`) を踏む (入れ子の entry は深さで抑止)。
  `tcs_lib_gc()` は明示の境界、`tcs_lib_collect()` は境界 + 旧世代 full GC、
  `tcs_lib_heap_bytes()` / `tcs_lib_heap_objects()` は旧世代 + nursery の
  managed heap 統計 (process のメモリではない)
- 検証: `bash tcs2c/verify-host.sh` (typed options / enum / nullable 既定値 /
  out string / scalar entry 引数 / 条件アクセス / runtime services)、
  `bash tcs2c/verify-gc.sh` (host ループでの root 保持 / 循環 garbage の回収 /
  heap の有界性)
