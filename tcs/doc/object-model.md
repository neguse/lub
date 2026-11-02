# object model の契約 — VM と tcs2c が共有する型記述子・layout・reload の計画・heap の walker

> Status: 契約 (2026-11-01)。M-1、M-2、M-3 は実装済み、M-4 は 1 段目 (class の payload の形と型の置き場所) まで (§9)。
>
> Scope: tcs VM (`tcsvm/`、doc/vm-design.md) と tcs2c (`tcs2c/`) の heap object の形、
> 型記述子の data、hot reload の計画の構造、heap を辿る walker。

## 1. なぜ今決めるか

VM と tcs2c (release) の性能差は 11〜29 倍ある (vm-design §17.3)。手元の機械や実機で dev 実行するとき、
「編集した module だけ VM、残りは AOT」の mixed-mode (il-design §5) が効く。そのためには両方が
同じ heap の object を読み書きできなければならない。また hot reload の移行 (vm-design §12.2)、
tcs2c の reload (hot-reload-design §3 の L2 の copy migration)、snapshot / restore (同 §3 の S) は、
どれも「型記述子に従って heap を辿り、object を詰め直す」同じ仕事をする。

どちらの実装も若いうちに、型記述子・layout の規則・reload の計画・walker を 1 つに揃えて契約にする。
hot-reload-design §10 の規則 4 (runtime を片側だけに作らない) の通り、C の実装は `runtime/` に 1 つ置き、
VM と tcs2c が同じものを使う。

## 2. 共有するもの

| もの | 形 | 作る側 | 読む側 |
|---|---|---|---|
| 型記述子 (§4) | byte 列 (`.tcsb` の `types` section と同じ形式) | TinyC# の `TypeTable` (`SelfHost/CBackend/CEmitter.cs`。C backend と VM backend が共有) | `runtime/model` の parser (VM の loader、tcs2c の生成 C の初期化) |
| layout の規則 (§5) | C の struct の規則 | — | `runtime/model` の layout 計算。tcs2c の生成 struct は同じ offset になることを検査する |
| object の header (§3) | C の struct | — | VM と tcs2c の GC と walker |
| reload の計画 (§6) | byte 列 | TinyC# の `ReloadPlan` (`SelfHost/VmBackend/ReloadPlan.cs`) | VM の `tvm_apply`、tcs2c の migrator |
| heap の walker (§7) | C の関数 | — | VM の移行、tcs2c の copy migration、snapshot。sink だけが違う |

## 3. object の header

全 heap object は header の直後に payload を置く。header は tcs2c の `TcsGcHeader` を元にし、
layout の代わりに型記述子を指す。

```c
typedef struct TcsHeader {
  _Alignas(max_align_t) struct TcsHeader *next;
  const TcsType *type;   /* 型記述子 (§4)。string / buffer も型を持つ */
  size_t size;           /* payload の byte 数 */
  uint16_t kind;         /* TCS_KIND_* (raw / object / array / list / dict / closure / box) */
  uint16_t flags;        /* GC の印 (MARK / STATIC / OLD / DIRTY / FORWARD) */
  uint32_t type_id;      /* 実行時型の tag (cast と型 test) */
} TcsHeader;
```

- payload は `max_align_t` に揃う
- 実装の今 (M-4 の 1 段目): VM の header (`TvmGcHeader`) は `max_align_t` に揃い、型記述子 (`type`) を持つ。
  class の payload は tcs2c と同じ `{ uint32_t type_id; field... }` になり、class の field の offset は tcs2c の生成
  struct と同じ (`tcsvm/tests/mixed`: tcs2c の生成 C の関数が VM の object を読み書きする)。header の残りの形
  (`size`、`kind`、`flags`)、`type_id` の番号の付け方 (VM は型の表の添字、tcs2c は DFS の範囲)、GC は共有していない
- class の payload は `{ uint32_t type_id; [uint64_t host_value;] field... }`。`type_id` は header と同じ値で、
  tcs2c の生成 C が今持つ形をそのまま契約にする (`host_value` は host と共有する外部 data class だけ)
- GC の方式 (tcs2c の T252: フレーム同期の世代別、nursery は動かす、旧世代は mark-sweep) も共有する。
  回収はフレーム境界でだけ起きる。VM が今持つ確保の地点での回収は、mixed-mode では AOT の C の frame の
  pointer を辿れないので使わない (VM だけで動くときの最適化として残してよい)

## 4. 型記述子

### 4.1 runtime の形

```c
typedef struct TcsField {
  const char *name;
  uint8_t kind;      /* I32 / F32 / BOOL / REF / STRUCT */
  uint16_t type;     /* REF: 参照先の型 (無ければ NO_TYPE)、STRUCT: struct の型 */
  uint32_t offset;   /* §5 の規則で決まる byte offset (payload の先頭から) */
} TcsField;

typedef struct TcsType {
  uint8_t tkind;          /* CLASS / STRUCT / ARRAY / STRING / LIST / DICT / FUNC / IFACE / OBJECT / BOX */
  const char *name;       /* IL の型の名前。版を跨いで型を同じものと見る鍵 */
  uint32_t layout_hash;   /* IlExport の LayoutHash (field の名前と型、struct は展開)。変化の検出の高速化 */
  uint8_t flags;          /* RECORD、HAS_ON_RELOAD (instance の OnReload がある) */
  uint16_t nfields;
  TcsField *fields;       /* 宣言の順。struct の field は葉に展開しない (名前で移行するため) */
  uint32_t size, align;   /* payload (class) / 値 (struct) の大きさと alignment */
  uint32_t nrefs;         /* 参照の場所の数と offset (struct の中も展開する。GC と walker が使う) */
  uint32_t *refs;
  /* 要素 (配列 / List / Dictionary の値)、Dictionary の key、関数型の引数と戻り値、interface、itable */
  ...
} TcsType;
```

### 4.2 byte 列の形式 (v1)

`.tcsb` の `types` section と同じ。tcs2c は生成 C に `static const unsigned char tcs_type_table[]` として
同じ byte 列を置き、初期化で同じ parser に渡す。little endian で pointer の大きさに依らない。

```text
u32 型の数
型ごとに:
  u8  tkind
  u32 名前 (string 表の番号)
  u32 layout_hash
  u8  flags
  CLASS / STRUCT:
    u16 field の数、field ごとに: u32 名前、u8 kind、u16 型
  CLASS: u16 interface の数と型の番号、u16 itable の数と (selector、関数) — VM だけが使う (tcs2c は 0 を書く)
  ARRAY / LIST: 要素の (kind、型)
  DICT: key の (kind、型)、値の (kind、型)
  FUNC: 引数の葉の数と (kind、型)、戻り値の葉の数と (kind、型)
  BOX: 中の kind
```

型の番号はこの表の中の添字。string 表は同じ byte 列の前に置く (`.tcsb` と同じ)。

## 5. layout の規則

C の struct の規則に揃える (tcs2c の生成 struct をそのまま正にできる)。

- field は宣言の順に置く。各 field は自分の alignment に揃える
- 大きさ: int / float は 4、bool は 1、参照は pointer の大きさ
- struct の field は 1 つの塊として置く: struct 自身の alignment (中の最大) に揃え、struct の大きさ
  (最後を自分の alignment に揃えたもの) だけ進む。`{float x; bool b}` の後の `bool c` は offset 8 で、5 ではない
- class の payload は `uint32_t type_id` (と外部 data class の `uint64_t host_value`) の後に field を置く
- 配列 / List / Dictionary の要素は値の大きさ (struct は struct の大きさ) で詰めて並べる
- `T?` は `{ bool has; T v; }`、tuple と KeyValuePair は field を持つ struct

VM の命令が使う「葉」(struct を展開した 1 値) の offset は、field の offset と struct の中の offset の和として
loader が求める。葉は命令の operand の都合で、型記述子には載せない。

## 6. reload の計画

`ReloadPlan.Compute` (`SelfHost/VmBackend/ReloadPlan.cs`、hot-reload-design §9 の Planner の計画の部分)
が旧版と新版の型記述子から作り、VM の `tvm_apply` と tcs2c の migrator が同じ構造で受ける。restart の理由は
Lua の reload と同じ規則 (`ReloadClassifier`) で決める。C の reader は `runtime/model/tcs_walk.h` の
`tcs_plan_read`。byte 列 (v1、前に自分の string 表を置く):

```text
u32 restart の理由の数、理由ごとに: u32 文字列
u32 型の変化の数、型ごとに:
  u32 型の名前
  u8  変化 (ADDED / REMOVED / RELAYOUT / SAME)
  RELAYOUT:
    u16 新しい field の数、field ごとに:
      u8  出所 (RETAINED / ADDED / RESET)
      u16 旧版の field の番号 (RETAINED のとき。[RenamedFrom] の引き継ぎもここ)
      u32 initializer の関数 (ADDED のとき。無ければ 0 の値)
u32 static の変化の数、static ごとに: u32 型の名前、u32 field の名前、u8 変化 (ADDED / REMOVED / RETAINED)
```

- 旧版にだけある field は DISCARDED で、新しい field の列に載らないことで表す
- 型が変わった field は RESET (新しい型の 0 の値。hot-reload §5.2)
- restart の理由が 1 つでもあれば、計画は適用されない (host は restart の導線へ)
- VM の initializer は関数の番号、tcs2c は生成 C の関数の番号 (表で引く)。initializer は引数を取らず field の
  値を返す関数で (C# の field initializer は this を読めない)、ADDED と RESET の両方に付く (Lua の reload と同じく、
  型の変わった field も initializer があればその値になる)
- 型は名前で対応させる。field は名前で対応させ、種類と型の名前が同じなら RETAINED。struct の形が変わると、
  その struct を field に持つ型 (cell、`T?`、KeyValuePair を含む) も RELAYOUT になる
- static は「型.field」の名前で対応させる。`.tcsb` は static の slot の種類表の後に static の field の表
  (名前、先頭の slot、種類と型) を持つ。struct の static は葉を値に組み立て、struct の計画で移して葉に戻す。
  遅延初期化の型の「初期化を始めた」flag は `型.<init>` の名前で同じく移す
- 足した static の initializer は `型.<reload-static>` の関数にまとめる。遅延初期化の型でまだ初期化して
  いなければ走らせない (最初の読み書きで全部を初期化する)

### 6.1 適用の順 (VM の `tvm_apply`)

1. 新版を別の VM に読み、検査する (失敗は旧版のまま `TVM_LOAD_ERROR`)
2. 計画の restart の理由、生きている object の型が新版に無いこと、生きている closure の関数が新版に同じ形で
   無いことは `TVM_RESTART` (旧版のまま)
3. 反映 (失敗の経路なし): layout の変わる object を作り直して旧 object に転送先を書き、ほかは型を付け替え、
   static を移し、module を入れ替え、walker で全部の参照を転送先へ張り替える
4. ADDED / RESET の field の initializer を、作り直した instance ごとに走らせる。足した static の initializer
5. hook: OnReload を持つ型の生きている instance ごとに `OnReload()`、次に entry の型の static の `OnReload()`

initializer は新版の関数なので、型と heap が新版で揃った 3 の後に走らせる。そのため hot-reload §7 の
「評価してから反映」と違い、initializer の fault は hook の fault と同じ警告になり (field は 0 の値のまま)、
反映は巻き戻さない。旧版の heap のまま新版の関数を走らせると、`is` と cast が旧版の型と合わないため。
tcs2c の migrator も同じ順にする

## 7. heap の walker

`runtime/model/tcs_walk.{h,c}`。root と object を型記述子に従って辿り、場所ごとに sink を呼ぶ。

```c
/* 参照の場所 (読み書きできる)。GC の mark、移行の張り替え、snapshot の書き出しの sink */
typedef void (*TcsRefFn)(void *ctx, void **slot);

/* 型 t (class の payload か struct の値) の値の中の参照の場所 */
void tcs_walk_value(const TcsType *t, char *base, TcsRefFn fn, void *ctx);
/* 要素 n 個 (配列 / List の要素、Dictionary の値) */
void tcs_walk_elems(const TcsType *types, uint8_t kind, uint16_t type, uint32_t size, char *data, uint32_t n,
                    TcsRefFn fn, void *ctx);
/* 値の移行: 旧版の型の値を新版の型の値へ計画に従って写す (struct の field は再帰) */
void tcs_migrate_value(const TcsPlan *plan, const TcsType *oldtypes, const TcsType *ot, const char *src,
                       const TcsType *newtypes, const TcsType *nt, char *dst);
```

容器 (配列 / List / Dictionary) の並べ方は M-4 までは実装が持ち、中の値をこの walker に渡す。VM は
`tvm_object_refs` (object 1 つの参照の場所を順に sink に渡す) を GC の mark と reload の張り替えで共有する。

- 1 つの walker を 3 つの使い方が共有する: VM の §12.2 の移行 (layout の変わった型の object を作り直して
  張り替える)、tcs2c の copy migration (hot-reload §3 の L2)、snapshot (S、書き出しと読み戻し)
- root の列挙は呼ぶ側が渡す (VM は static と handle、tcs2c は生成側の static の表と hold)。walker は root の
  場所を sink の `ref` に渡すだけ
- 移行は 2 段 (hot-reload §7): 評価 (新しい object を作って side table に置く、失敗したら捨てる) と、
  反映 (walker で全部の参照を張り替える)。反映の段に失敗の経路を作らない

## 8. 検査

- 型記述子の往復: C# の writer が書いた byte 列を C の parser が読み、同じ名前・offset になる (VM の loader の検査と
  tcs2c.Tests)
- layout の一致: tcs2c の生成 C は、型記述子から求めた field の offset と、生成した struct の `offsetof` が
  一致することを初期化で検査する (test の build で有効にする)
- 計画の一致: 同じ v1 / v2 の組から、VM の `tvm_apply` と tcs2c の migrator が同じ object の形に移行する
  (T235 の hot reload fuzz を両方で走らせる)

## 9. 段階

| 段階 | 内容 | 完了条件 |
|---|---|---|
| M-1 | 型記述子の byte 列の C# の writer と C の parser / layout (§4、§5) を `Transpiler/Backend/` と `runtime/model/` に置き、VM がそれを使う。VM の layout を C の規則にする | VM の差分テストが通る (済み。VM の loader が往復の検査を兼ねる) |
| M-2 | tcs2c が同じ byte 列を生成 C に置き、offset の一致を検査する | tcs2c.Tests が検査付きで通る (済み: `tcs_type_table` と `TCS_VERIFY_TYPES` の `tcs_verify_types`。tcs2c.Tests は全部の program をこの検査付きで build する) |
| M-3 | walker (§7) と reload の計画 (§6)。VM の `tvm_apply` (vm-design V4) をこの上に作る | hot reload の semantic テストと T235 が VM で通る (済み: `tcs2c.Tests` の `VmReloadTests` が T235 の scenario 20 本を C# の driver で VM に当て、Lua と同じ不変量を GC stress でも確かめる。relayout は class、List / 配列 / Dictionary の struct 値、struct の static まで) |
| M-4 | header (§3) と GC を共有し、VM の object を tcs2c と同じ形にする | VM の object を tcs2c の生成 C の関数が読める (mixed-mode の最小の test。1 段目は済み: class の payload と field の offset が同じになり、`tcsvm/tests/mixed` で tcs2c の生成 C の関数が VM の class の object の int / float / bool / 参照の field を読み書きする。残り: header の struct を 1 つにする、type_id の番号、string / List / Dictionary / closure の容器の形、GC と write barrier) |
| M-5 | (§10 で置き換え) tcs2c の copy migration と snapshot を walker の上に作る | release の reload (hot-reload の D6) は採らない方針。tcs2c 側の walker は §10 の X-3 (C の module の root が VM の reload に参加する) と S (snapshot) で使う |

## 10. module 単位の混在 (2026-11-01)

目標は release か dev かで切ることではなく、「editor・engine の層・重い処理は C (tcs2c) で固定し、content だけを
VM で reload する」ように module (C# の project) 単位で実行系を混ぜること。M-1〜M-4 の共有 (型記述子、layout、
reload の計画、walker、class の payload の形) はこのための土台になる。

| 段階 | 内容 | 状態 |
|---|---|---|
| X-1 | 境界の自動化: C の module の公開 static method を VM の host 関数として登録する glue を tcs2c が生成し (`tcs2c --lib --vm-host` の `tcs_vm_register`)、VM の module はその source を `--ref` に取って呼ぶ | 済み (`tcsvm/tests/modules`) |
| X-2 | heap と GC を 1 つにする | 済み (`tcsvm/tests/modules`、§10.4): VM の GC header は tcs2c と同じ形で、shared heap では VM の object (kind FOREIGN) を tcs2c の GC (nursery と旧世代、write barrier) が辿る。C の module は VM の object を static に持て、class を返せ、VM の object に参照を書ける。残り: 容器 (string / List / Dictionary / closure) の形を 1 つに (今は境界で string を写す)、type_id の番号 |
| X-3 | VM の reload が C の module の持つ参照も張り替える | 済み: C が `tvm_hold` で持つ object と、shared heap では C の module の static・hold・object が持つ参照も張り替わる (`Editor.Keep(object)` で持った content の object が reload の後も同じ object を指す) |
| X-4 | 境界を越える呼び出し: C が VM の delegate を呼ぶ、VM の class が C の module の interface を実装し C から呼ぶ | 済み (`tcsvm/tests/modules`、§10.5) |

### 10.1 X-1 の境界の規則 (今)

- 名前は `IlExport.HostSurface` (VM の emitter が `--ref` の関数に付ける名前と同じ規則。`Ed.Editor.Log` は `ed.editor.log`)
- int / float / bool / enum はそのまま。string は両側の形が違うので境界で写す (VM → C は `tcs_string_new`、C → VM は
  `tvm_string_new`。C の戻り値は frame の境界の前に写す)
- class の object は VM の object をそのまま渡す。C の module の class (`Ed.Transform`) は、VM 側では `--ref` の data
  class になり、両側の payload の形が同じ (§3、M-4 の 1 段目) なので、C の関数が field を読み書きできる
- 制約 (X-2 まで): C の関数は VM の object を保持しない (C の module の static や object に入れると tcs2c の GC が辿って
  壊れる)。境界を越える class の field は scalar と、境界を越える class だけ (string の field は形が違う)。C の関数は
  class を返さない。out、List / Dictionary / delegate の引数は登録しない (生成 C に理由の comment)
- C (host) が frame を跨いで VM の object を持つときは `tvm_hold` の handle で持つ。GC の root になり、reload で張り替わる

### 10.2 X-2 の設計 (heap と GC を 1 つにする)

- runtime を 1 つにする: tcs2c の runtime (今は `tcs2c/CRuntime*.cs` の C# の文字列) を `runtime/core/` の C に出し、
  VM と tcs2c が同じ C を link する (hot-reload §10 の規則 4)。VM の `tcsvm_rt.c` / `tcsvm_gc.c` の容器と GC はこれに置き換える
- header は §3 の `TcsHeader` 1 つ (型記述子、size、kind、flags、type_id)。type_id は型記述子の表の添字に揃え、tcs2c の
  DFS の範囲の `is` は型記述子の継承の情報 (表に足す) で引く
- 容器は tcs2c の形 (TcsString は `{ size_t length; data[] }`、List は `{ length, capacity, element_size, data }` など) に揃える。
  Dictionary の列挙の順は .NET の順 (VM が持つ free list の規則) を tcs2c 側にも入れる
- GC は tcs2c の T252 (フレーム同期の世代別、nursery は動かす)。VM の register と frame は root の表として GC に渡し、
  nursery の移動で書き換えてもらう。VM の確保の地点での回収は、VM だけで動くときに限る (C の frame の pointer は辿れない)
- write barrier: VM の参照の store 命令 (STF_REF など) も tcs2c と同じ barrier を通る
- これが済むと、string の写しが要らなくなり、C が VM の object を static に持て、class を返せる

### 10.3 X-4 の設計 (境界を越える呼び出し)

- delegate: 両側の closure を 1 つの形 (`{ 関数 pointer、捕捉 }`) にし、VM の closure の関数 pointer には「VM で走らせる」
  trampoline を置く。C から呼ぶと trampoline が `tvm_call_closure` を呼ぶ
- interface: C の interface の呼び出し (tcs2c の dispatcher) は、object の型記述子の itable を引く形にする。VM の class の
  itable の項は VM の関数なので、同じく trampoline を通す

### 10.4 shared heap (X-2 の実装)

- VM の GC header (`TvmGcHeader`) は runtime/core の `TcsGcHeader` と同じ形 (next、型記述子 (layout の場所)、size、kind、
  flags、type_id)。VM の object は kind `TCS_KIND_FOREIGN`、VM の blob は `TCS_KIND_RAW`。flags の MARK / STATIC は同じ値で、
  VM だけの bit (reload の転送) は tcs の flags と重ならない
- `tcs2c --lib --vm-host` の `tcs_vm_register` が、その module の runtime を VM の heap にする (`tvm_use_heap`。確保、
  呼び出しの出入り、write barrier、frame の境界の回収、reload の走査)。runtime/core の GC は `tcs_gc_foreign_refs`
  (VM の `tvm_heap_refs`) で VM の object の中を、`tcs_gc_root_hook` (VM の `tvm_heap_roots`) で VM の static と handle を辿る
- 回収は frame の境界だけ (VM の呼び出しの間は C の関数から戻っても回収しない)。VM の参照の store 命令と、container を
  変える runtime の関数の呼び出しは write barrier を通る
- reload は境界で回収してから旧世代を辿り、張り替えは C の module の static・hold・全部の object も対象にする (X-3)
- 制約: 1 process に VM は 1 つ、C の module (runtime を持つもの) も 1 つ。string は写さずに渡し、型の番号は合わせる (§10.6)

### 10.5 境界を越える呼び出し (X-4 の実装)

- delegate: C の module の公開関数が `Action` / `Func` を受けると、glue が VM の closure を tcs2c の closure
  (`{ trampoline, cells[0] = VM の closure }`) に包む。trampoline は `tvm_invoke` で VM の closure を呼び、引数と戻り値を
  写す (string は写さない)。C の module はこの closure を static などに持てる (cells[0] は shared heap の root から辿られ、
  reload では closure が新版の lambda を指す)
- interface: VM の class が C の module の interface を実装でき、C の dispatcher は object が VM の object (kind FOREIGN)
  なら `tvm_invoke_method` で method を名前で呼ぶ (reload の後は新版の本文が走る)。`--ref` の interface の実装
  (RefInterfaceImplementation) は Lua の名前の写し方の制約なので、VM の emitter は止めない
- VM の中から `--ref` の interface の method を呼ぶ: VM はその interface を `HostIfaceT` (参照) で持ち、method は C の module
  の dispatcher を host の関数 (`ed.itool.use`、受け手は先頭の引数) として呼ぶ。dispatcher は C の object なら C の実装、
  VM の object なら VM へ戻る
- C の module が返した delegate を VM が呼ぶ: glue が関数型ごとの invoke を `tvm_host_delegate` で登録し、VM の CALLD は
  VM の object でない delegate に会うと関数型の sig ("i:i" など) の invoke で呼ぶ。List.Sort / FindIndex / RemoveAll などの
  runtime の関数も、C の delegate は要素の種類から作った sig の invoke で呼ぶ (`tvm_call_delegate`)。
  `Predicate<T>` / `Comparison<T>` は両側で `Func<T, bool>` / `Func<T, T, int>` と同じ関数型
- 引数と戻り値は int / float / bool / string / class / object / delegate。out、struct、List / Dictionary は境界で呼べない

### 10.6 容器の形

- string: VM の `TvmString` (`{ int32 len; int32 pad; data }`、pad は常に 0) は little endian で tcs2c の `TcsString`
  (`{ size_t length; data }`) と同じ byte 並びになる。shared heap では glue が string を写さずに渡し、C の module の
  string (kind RAW、型記述子なし) は VM では string 型として扱う (`tvm_type_of`)。VM は little endian だけを対象にする
- VM の型を引く所 (`is` / cast、等価、ToString、`tvm_invoke_method`) は `tvm_type_of` を通す。C の module の object
  (kind が FOREIGN でも RAW でもないもの) は、header の type_id が下の登録で合わせた class なら その VM の型、
  それ以外は型を持たない
- 型の番号: VM の class の object は header と payload の type_id に型ごとの番号を書く。VM だけの class は
  `TVM_TYPE_ID_VM` (0x40000000) + 型の番号で、tcs2c の番号 (class は 1..N、構造型は 0x80000000 + n) と重ならない。
  glue は C の module の class を `tvm_host_type` (名前と tcs2c の type_id) で登録し、VM が作ったその class の object は
  C の番号を持つ。だから C の型の test・cast・dispatcher は VM が作った object をそのまま扱い、VM の型の test は
  C が作った object を同じ型として扱う
- interface: VM の class の型の表は、実装する `--ref` の interface も持つ (表では object の種類の型で、名前で引く)。
  C の module の interface の判定 (`tcs_is_*`) は、VM の object なら `tvm_implements` で VM の型の表を引く。VM の
  型の test は、C の object なら glue が `tvm_host_iface` で登録した C の判定を呼ぶ
- `--ref` の generic でない class / interface への型 test と cast は Lua では止める (TCS1001 `RefTypeTest` など。
  host の値の型を見分けられない) が、VM の emitter は止めない
- List / Dictionary / closure は両側で形が違い、境界では渡せない (§10.5)
- List を渡す案 (未着手): VM の List を shared heap では tcs2c の List そのもの (kind LIST、`TcsList` の形、header に
  要素の参照の layout) として作り、VM の List の型は header の type_id から引く (VM の型なら `TVM_TYPE_ID_VM` + 番号で
  直に引き、C の module の List の型は glue が `tvm_host_type` で名前と番号を登録する)。こうすると C の runtime と GC は
  VM の List をそのまま扱える。分かっている障害: (1) `tcs_list_add` などは header の layout を書き換えるので、VM の
  object (header に VM の型記述子) をそのまま C に渡すことはできない。(2) VM の List の命令と runtime の関数は
  header の型記述子を読む (70 か所ほど) ので、type_id から引く形に変える。(3) reload は VM の object を kind FOREIGN で
  見分けているので、kind LIST の VM の List (struct の要素の layout が変わるもの) を type_id で見分けて移す。
  (4) C の runtime が作る List (Keys / ToList など) は type_id を付けない所があり、VM に来ると型が分からない
- Dictionary は両側で構造が違う (VM は挿入順の entry の列と open addressing の表、tcs2c は bucket の鎖と順の buffer)。
  どちらかに寄せるまでは境界で渡さない
