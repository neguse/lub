# object model の契約 — VM と tcs2c が共有する型記述子・layout・reload の計画・heap の walker

> Status: 契約 (2026-11-01)。M-1、M-2、M-3 は実装済み (§9)。
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
| 型記述子 (§4) | byte 列 (`.tcsb` の `types` section と同じ形式) | C# の `TypeTableWriter` (Transpiler) | `runtime/model` の parser (VM の loader、tcs2c の生成 C の初期化) |
| layout の規則 (§5) | C の struct の規則 | — | `runtime/model` の layout 計算。tcs2c の生成 struct は同じ offset になることを検査する |
| object の header (§3) | C の struct | — | VM と tcs2c の GC と walker |
| reload の計画 (§6) | byte 列 | C# の `ReloadPlan` (`Transpiler/Backend/ReloadPlan.cs`) | VM の `tvm_apply`、tcs2c の migrator |
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

C# の `ReloadPlan.Compute` (`Transpiler/Backend/ReloadPlan.cs`、hot-reload-design §9 の Planner の計画の部分)
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
| M-4 | header (§3) と GC を共有し、VM の object を tcs2c と同じ形にする | VM の object を tcs2c の生成 C の関数が読める (mixed-mode の最小の test) |
| M-5 | tcs2c の copy migration と snapshot を walker の上に作る | hot-reload §3 の L2 / S の受入条件 |
