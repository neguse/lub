# tcs VM 設計 — IL から直接 bytecode を出す dev 実行系

> Status: 実装 (`tcsvm/`、`SelfHost/VmBackend/`)。命令の正本は `tcsvm/vm/tcsvm_ops.h`、API の正本は `tcsvm/vm/tcsvm.h`。
>
> Scope: tcs の dev 実行系 (開発と hot reload) の bytecode と VM。release は C (`tcs c`) で、この文書の外。VM と C が共有する型記述子、layout の規則、reload の計画、heap の walker、module 単位の混在は `doc/object-model.md`。hot reload の契約 (live と restart の分類) は `doc/hot-reload-design.md`。

## 1. 概要

tcs の IL (`IlExport`) を入力に、型付き register VM 用の bytecode (`.tcsb`) を直接出す。

- compiler の側は TinyC# で書いた `SelfHost/VmBackend/` (VmEmitter) で、`tcs vm` が動かす。.NET、C、wasm のどの tcs でも同じ `.tcsb` を出す
- 実行系は C11 で書いた小さな VM (`tcsvm/`)。値は型タグを持たない 8 byte の slot で、命令が型を持つ (`ADD_I32` / `ADD_F32`)。il-spec の意味論 (i32 wrap、f32 single rounding、0-based 配列、C# の除算、fault) を命令と runtime の関数でそのまま実装する
- struct は register では葉の field ごとの連続 register、heap では C と同じ inline 配置。class の field は固定 offset の load / store
- **register の種類 (i32 / f32 / bool / ref) は関数ごとに固定**する。この 1 つの規則で、読み込み時の型検査 (verifier)、GC の精密な stack 走査、host から状態を読む口 (§14) が同じ表から得られる
- GC は動かさない mark-sweep で、確保の地点とフレーム境界で回収する (§10)。C の module と heap と GC を共有する形もある (object-model §10)
- hot reload は VM の機能 (§12)。method の差し替えは新版の関数表への入れ替え、layout の変更は生きている object を新しい layout で作り直して参照を張り替える。生きている object は heap から列挙するので、closure の捕捉、`List<S>`、Dictionary の struct の値も live で移る
- 検証は C と .NET との差分 (§16)

## 2. dev と release の分担

tcs の実行系は 2 つで、同じ IL を読む。

| | tcs VM (dev) | C (release) |
|---|---|---|
| 入口 | `tcs vm` → `.tcsb` → `tcsvm` か host に組み込んだ VM | `tcs c` → C → C compiler |
| 用途 | 開発の反復、hot reload、web の playground | 出荷 |
| 表現 | 型付きの register と inline の layout (§6) | 型ごとの C の struct と関数 |
| reload | live (§12、hot-reload-design §5) | 再ビルドと再起動 (hot-reload-design §3) |

VM が dev を受け持つのは、reload を VM の中で閉じられること (関数表と heap を VM が持つ)、browser で C toolchain なしに動くこと、compile が C compiler を通らず速いことによる。性能は C が上で、出荷は C にする。どちらも il-spec の観測等価を満たし、差分ゲート (§16) が一致を見る。

## 3. 目標と非目標

### 3.1 目標

- G1: il-spec の観測等価 (§1) を満たす。C と .NET との差分ゲートが全部通る
- G2: hot reload の live の範囲は hot-reload-design §5.2 の通り。instance と struct の形の変更、closure の捕捉や容器の中の struct の値も live に当てる
- G3: web の dev で、編集から反映までを対話の速さに保つ。bytecode の読み込みは parse と線形の 1 pass の検査 (§9.4) で済む
- G4: VM の C 実装は MSVC / clang / gcc / emscripten でそのまま build できる。GNU 拡張は computed goto だけに使い、`#if` で switch に切り替わる
- G5: 小さい。C の VM は 1 万行未満

### 3.2 非目標

- JIT。どのターゲットでも実行時に machine code を作らない (il-design §5)
- release 用途。出荷は C (`tcs c`)。VM は dev 実行系
- 汎用 VM。tcs のサブセット専用で、命令と runtime は il-spec の面だけを持つ
- bytecode の互換を版を跨いで保つこと。bytecode は compiler と VM の同じ版の間でだけ有効 (header の形式の版と命令の表の hash で照合する)。保存形式ではない

## 4. 検討した案

| 案 | 内容 | 判断 |
|---|---|---|
| WebAssembly を bytecode にする | tcs → wasm。browser は native、PC は wasm interpreter | 採らない。GC と layout 移行を linear memory 上に自作する点は本案と同じで、そのうえ reload は instance 差し替え (hot-reload §3 L1 相当) に寄る。browser 内 compiler が wasm module を作る手間と debug の写像も重い。web release の候補としては残る |
| C + dll / wasm 差し替え (hot-reload §3 L1) | dev も C で動かす | 採らない (dev)。browser に C toolchain が無く web dev が成立しない |
| IL の木を直接 interpret | IL の object を辿る | 採らない。C の VM へ渡すには結局直列化が要り、木の走査は型付きの命令列より遅い |
| **型付き register VM (本案)** | IL を register 機械の命令列へ | 採用。表現を型付きにでき、reload も debug も VM の中で閉じる |

## 5. 全体構成

```text
C# source
  → SelfHost/Compiler: 字句、構文、束縛、準拠検査 (TCS 診断)
  → IL (IlExport、generic は特殊化済み)                ← C backend と同じ入力
  → VmEmitter (SelfHost/VmBackend): 式の型 + register 割り当て + 命令選択 + 行の表
  → module (.tcsb)  ── program 全体で 1 つ
        │
        ├─ CLI: tcs vm -o game.tcsb、tcs vm --watch が reload の batch を stream (§13.3)
        └─ web: playground の VM の増分 session (VmSession) が module と batch を player へ
              ↓
  tcsvm (C): load → verify → link → 実行 (reload は tvm_apply / tvm_apply_batch)
        ↑
  host: host の関数を登録し (tvm_host)、entry の関数を tvm_find / tvm_call で呼ぶ
```

構成要素と置き場所:

| 要素 | 場所 | 言語 |
|---|---|---|
| VmEmitter | `SelfHost/VmBackend/` (`VmEmitter.cs`、`FunctionCompiler.cs`、`ModuleBuilder.cs`、`ReloadPlan.cs`) | TinyC# |
| CLI と増分 session | `SelfHost/Tool/VmRun.cs` (`tcs vm`、`VmSession`)、`SelfHost/Tool/Playground.cs` (web の tcs)、`WasmCompiler/VmExports.cs` (.NET の wasm) | TinyC# / C# |
| VM | `tcsvm/vm/` (`tcsvm.h` が公開 API、`tcsvm_main.c` が CLI の `tcsvm`) | C11 |
| 共有 runtime | `runtime/fmt`、`runtime/math`、`runtime/utf8`、`runtime/sort`、`runtime/model` (C backend と同じ実装) | C |

## 6. 値と型の表現

### 6.1 slot と register の種類

VM の値の単位は 8 byte の slot (`union { int32_t i; float f; void *p; uint64_t u; }`)。型タグは持たない。各関数の register は次のどれか 1 つの種類に固定される。

| 種類 | 中身 | 例 |
|---|---|---|
| `i32` | int / enum / char | `int`、`Color` |
| `f32` | float | `float` |
| `bool` | 0 / 1 | `bool` |
| `ref` | GC object への pointer か null | class、record、string、配列、List、Dictionary、closure、box、cell |

compiler は register を種類ごとに割り当て、1 つの register に別の種類の値を置かない (temp の再利用も同じ種類の間だけ)。関数ごとの「種類表」が bytecode に入り、これが §9 の verifier、§10 の GC、§14 の検査の口の共通の根拠になる。

### 6.2 値型

- **struct / record struct / tuple**: register では葉の field ごとに連続する register (`Vec2` は f32 が 2 つ、`(int, Vec2)` は i32, f32, f32)。copy は葉ごとの `MOV`。il-spec §10 の copy 地点はそのまま move の列になる
- heap (class の field、配列 / List の要素、Dictionary の値、closure の cell) では C と同じ inline 配置 (自然な size と alignment。object-model §5)。layout は VM が読み込み時に決める (§9.3)。static の struct は葉ごとの static の slot に置く
- **`T?`** (T は i32 / f32 / bool / enum / struct): register では `bool has` と T の葉。heap では `{ bool has; T v; }`。C の release 表現と同じ
- generic は compiler が特殊化済み (`Session.Specialize`)。VM に型引数は無く、全 register の種類が静的に決まる

### 6.3 参照型の object

全 object は object-model §3 の header (`TvmGcHeader`: 次の object、型記述子、payload の大きさ、kind、flags、type_id) の後に payload を置く。

| object | payload |
|---|---|
| class / record / cell | `{ uint32_t type_id; field... }` の inline 配置 (cell は捕捉される変数を 1 つ持つ class の種類の型。il-spec §7) |
| string | byte 長 + UTF-8 の octet 列 + 終端の `\0` (不変。il-spec §11) |
| `T[]` | 長さ + inline 要素 |
| `List<T>` / `Queue<T>` | count + capacity + 別確保の要素 buffer (inline)。Queue は List と同じ object |
| `Dictionary<K,V>` / `HashSet<T>` | 足した順の entry の列 (列挙は挿入順) と、entry の番号を引く open addressing の表。HashSet は Dictionary と同じ object |
| closure | 関数 (§8.4) + 捕捉の slot の列 |
| box | 1 つの値 (`object` 型に入った値型) |

要素型は型記述子 (object-model §4。size / alignment / 葉 / 参照の場所) で引く。`List<int>` と `List<Vec2>` は同じ C の実装を通り、要素 size と葉だけが違う。

### 6.4 例

```csharp
struct Vec2 { public float X, Y; }
class Particle { public Vec2 Pos; public Vec2 Vel; }
static void Step(List<Particle> ps, float dt)
{
    for (int i = 0; i < ps.Count; i++)
    {
        var p = ps[i];
        p.Pos.X += p.Vel.X * dt;
        p.Pos.Y += p.Vel.Y * dt;
    }
}
```

命令の形の例 (temp の割り当ては emitter が実際に出すものと違ってよい):

```text
; Step  params: r0 ref ps, r1 f32 dt
;       regs:   r2 i32 i, r3 i32 n, r4 ref p, r5 f32, r6 f32
      LDK         r2, 0
L0:   LIST_LEN    r3, r0              ; null なら fault
      JF_LT_I32   r2, r3, L1
      LIST_LD_REF r4, r0, r2          ; 範囲外なら fault
      LDF_F32     r5, r4, @Particle.Vel.X
      MUL_F32     r5, r5, r1
      LDF_F32     r6, r4, @Particle.Pos.X
      ADD_F32     r6, r6, r5
      STF_F32     r4, @Particle.Pos.X, r6
      LDF_F32     r5, r4, @Particle.Vel.Y
      MUL_F32     r5, r5, r1
      LDF_F32     r6, r4, @Particle.Pos.Y
      ADD_F32     r6, r6, r5
      STF_F32     r4, @Particle.Pos.Y, r6
      ADDI_I32    r2, r2, 1
      JMP         L0
L1:   RET0
```

field の参照 (`@Particle.Vel.X`) は bytecode では型の葉の番号で、読み込み時に byte offset へ書き換わる (§9.3)。

## 7. 命令

### 7.1 形式

命令は 64 bit 固定長。

```text
| op:8 | a:16 | b:16 | c:16 | x:8 |     3 operand 形
| op:8 | a:16 |   imm:32   | x:8 |     即値形 (i32 / f32 の bit / 定数の番号)
| op:8 | a:16 | b:16 |   off:24   |     分岐 (次の命令からの符号付きの相対 offset)
```

- register 番号は 16 bit (1 関数 65535 個)。struct を葉に展開しても上限に掛からない
- 固定長なので decode は 1 回の 64 bit load とシフトで済み、jump 先の計算も単純
- struct の要素の葉の命令 (`LDEF_*`、`LIST_LDF_*` など) は要素の中の byte offset を `x` (8 bit) に持つので、要素の中の offset は 255 まで (超えると読み込みエラー)
- dispatch は GNU C では computed goto、それ以外 (MSVC など) と `TVM_SWITCH_DISPATCH` では switch (意味は同じ)
- 命令の並びは `tcsvm_ops.h` が正本で、`SelfHost/VmBackend/Op.cs` はそこから作る (`tcsvm/gen-ops.py`)。並びの食い違いは命令の名前の列の hash (`.tcsb` の header) で読み込み時に止める

### 7.2 命令の分類

全部で 167。型ごとの複製 (`_I32` / `_F32` / `_B` / `_REF` の 4 つ組) を含めた数で、種類は少ない。

| 分類 | 命令 (代表) | 意味の正本 |
|---|---|---|
| move / 定数 | `MOV`, `LDK` (i32 / f32 の bit / bool), `LDNULL`, `LDSTR` (string の定数) | il-spec §6 (f32 literal は bit 値で持つ) |
| i32 | `ADD/SUB/MUL_I32` (wrap), `DIV/REM_I32` (0 方向、0 除算と `INT_MIN/-1` は fault), `NEG_I32`, `AND/OR/XOR/BNOT_I32`, `SHL/SHR_I32` (count & 31、算術), checked の `CADD/CSUB/CMUL/CNEG_I32`, 即値形 `ADDI_I32`, `CMP_I32` (CompareTo) | il-spec §5 |
| f32 | `ADD/SUB/MUL/DIV/NEG_F32`, `FMOD_F32` (fmodf), `CMP_F32` | il-spec §6 (strict) |
| bool | `NOT_B`, `AND_B`, `OR_B`, `NE_B`, `EQ_B` | il-spec §4 |
| 変換 | `I2F`, `F2I` (0 方向、NaN と範囲外は fault), `CHR` (1 byte の string。0〜255 の外は fault) | il-spec §5, il-reference |
| 比較 / 分岐 | `EQ/LT/LE_I32`, `EQ/LT/LE_F32` (IEEE), `EQ_STR` (内容), `EQ_REF` (identity), `JMP`, `JT/JF`, `JNULL/JNN`, 比較と分岐の融合形 `JT_LT_I32` / `JF_LT_I32` 等 | il-spec §4, §6, §11 |
| object | `NEW` (class か cell), `NNULL` (instance method の呼び出しの前の null の fault), `LDF_*` / `STF_*`, `LDS` / `STS` (static), `EQ_REC` (record と、struct の object / interface の葉の実行時の型の値の等価), `CLONE` (record の `with`) | il-spec §9, §10 |
| 配列 / List | `NEWARR`, `ARR_LEN`, `LDE_*` / `STE_*` (範囲検査), struct の要素の葉の `LDEF_*` / `STEF_*`, `NEWLIST`, `LIST_LEN`, `LIST_LD_*` / `LIST_ST_*`, `LIST_LDF_*` / `LIST_STF_*`, `LIST_ADD_*`, `LIST_PUSH0`, `LIST_INSERT0`, `LIST_REMOVE_AT`, `LIST_CLEAR`, `LIST_SORT` | il-spec §11, §12 |
| Dictionary | `NEWDICT`, `DICT_LEN`, `DICT_FIND` (無い key は fault), `DICT_INS`, `DICT_LDV_*` / `DICT_STV_*`, 走査の `DICT_USED` / `DICT_LIVE` / `DICT_LDK`, `DICT_HAS`, `DICT_REMOVE` | il-spec §11 |
| 型 | `ISTYPE`, `CAST` (失敗は fault), `ASCAST`, `BOX_*`, `UNBOX_*` | il-spec §2, §9 |
| nullable | 葉の move と `has` の分岐に展開する。専用命令は `NVAL` (値なしで fault) だけ | il-spec §3 |
| 文字列と出力 | `CONCAT`, `I2S` / `F2S` / `B2S`, `PRINT_*` | il-spec §13 |
| 呼び出し | `CALL`, `CALLI` (interface), `CALLD` (closure), `CALLR` (runtime と host の関数), `RET`, `RET0` | §8 |
| closure | `CLOSURE`, `LDCAP` | il-spec §7 |
| 記録 | `TRACE` (文の頭), `TVAL` (代入の後の値)。`tcs vm --trace` の関数にだけ入る | §14 |

Dictionary と HashSet と Queue の残りの演算、String / Math / Random / TinySystem.Text / 数値の文字列化の残りは `CALLR` で C の runtime 関数を呼ぶ。頻度の高い読み書きと長さだけを専用命令にする。

### 7.3 IL の形の読み替え

IL のノードのいくつかは、添字の写し方の印を持つ、List の操作を名前の呼び出しで書く、といった形をとる (il-reference)。VmEmitter は C backend と同じくこれを型付きの命令に読み替える。

| IL | VM での扱い |
|---|---|
| `IlLit` (literal の値: Null、Bool、Int、Float、String) | 型付きの定数 (`LDK` の bit、string の定数、`LDNULL`) にする。float の位置の Int は float の値にする |
| `IlIndex.PlusOne` / `Unchecked` | 添字は 0 始まりのまま読み、印は見ない。範囲は命令が必ず検査する |
| `IlLen.NilElements`、`IlCall("List.Add" / "List.RemoveAt")` | List の長さと List の命令 (`LIST_LEN`、`LIST_ADD_*`、`LIST_REMOVE_AT`) |
| `IlIife` | 結果の register と終わりの label を持つ block として inline に展開する。関数は作らない |
| `IlTernary` | 分岐と結果の register |
| `IlMultiAssign` (分解、out 引数の戻り) | 結果の register の列 (§8.2) |
| `IlIsPrimType` (プリミティブの型 test) | box の型の検査 (`ISTYPE`)。値型の受け手は値の有無か常に真 |
| `IlForPairs` | List / Dictionary の Clear の形 (全部の key に `nil` を入れる) だけが来る。`LIST_CLEAR` と runtime の clear にする |
| `IlForeachDict` | entry の列を番号で辿る命令列 (`DICT_USED`、`DICT_LIVE`、`DICT_LDK`) |
| `IlStructCopy` | 葉の `MOV` か、heap との間の葉の load / store。`Copiers` は読まない (特殊化済み) |

## 8. 呼び出し規約

### 8.1 stack と frame

- VM ごとに 1 本の slot stack (既定 1M slot) と frame の表 (既定 64K)。どちらも `TvmConfig` で変えられる。溢れたら `stack overflow` の fault (il-spec §12 の実装定義の資源 fault)
- 呼び出し側は引数を連続 register `a..a+n-1` に置く。callee の frame は caller の frame の後ろから始まり、引数はそこへ copy する (`CALL a, b, #f`: a が引数の先頭、b が戻り値の先頭)。窓を重ねて copy を省くと、callee が caller の `a` 以降の register に別の種類の値を書くので、呼び出しの後で caller の種類表が崩れる (§6.1 の規則が成り立たない)。引数の copy は数 slot で済む
- frame の register は呼び出し時に、引数以外の `ref` の register を null で埋める。loader が関数ごとに `ref` の register の範囲を求めて、1 回の memset で済ませる。これで GC がいつ frame を見ても未初期化の pointer が無い

### 8.2 戻り値

- `RET a` は callee の `a` からの register を、caller の `CALL` が指した戻り値の先頭 `b` へ写す。struct と tuple は葉の数だけの register、out 引数はその後ろに並ぶ。戻り値の register の種類も関数の型表に入る。引数と戻り値の先頭を分けるのは、同じ位置で種類が違う (`int F(float x)`) ことがあるため

### 8.3 関数の解決

- VM は関数表を持つ。関数は名前を持ち (`型.method`。overload は引数の型を `__` でつなぐ `Game.Add__int__int`、constructor は `型.ctor`、lambda は `外側の関数<lambdaN:本文の指紋>`)、`CALL` の即値は関数表の番号
- host は関数を名前で引く (`tvm_find`)。hot reload は module ごと新版に入れ替えるので、番号は reload を跨いで保たれない (§13.1)
- `CALLI` (interface): receiver の型の itable (selector の番号 → 関数表の番号の整列済みの配列) を二分探索で引く。継承が無い (q.md Q15) ので dispatch はこれだけ

### 8.4 closure と method group

- closure は関数への pointer と捕捉の列を持つ。捕捉は il-spec §7 の通り変数単位で、捕捉される変数は cell に置く
- lambda の closure も static method の method group の closure も、reload では新版の対応する関数に付け替わる (§12.4)。付け替え先の無い生きている closure は restart になる
- method group の struct 引数の copy は callee 側の入口で行う (il-spec §10)

### 8.5 host 関数

- host の関数は runtime の関数と同じ `CALLR a, b, #c` で呼ぶ。host の型は `TvmStatus fn(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user)`。
- module は使う host の関数を名前で import し、読み込み時に host の登録表 (`tvm_host`) と照合する。名前が無いか、呼び出しの形が登録と合わなければ読み込みエラーにする (stub と runtime の食い違いを実行前に止める)。
- register の中では値型は葉のまま持つ (`a` からは引数の葉、`b` からは戻り値の葉と out の葉が並び、VM の中の呼び出しと同じ形)。host の境界でだけ、struct を C の layout (doc/object-model.md §5) の塊に写す。
- 登録は、C# の引数の順に並べた項の列と、戻り値の項で書く (書き方は `tcsvm.h`)。項は 1 葉の値 (`i` `f` `b` `s` `o`)、struct の値 `S{型}` (型は IL の型の名前で、`T?` は `S{Eng_Vec2?}`)、前に `&` を付けた out のどれか。
- 値の struct は、VM が引数の葉を一時の塊に詰めて `args[k].p` に渡す。host は C の struct として読む (`*(const Vec2 *)args[k].p`) ので、C の API を包む host は struct をそのまま値で渡せる。
- out は、VM が 0 で埋めた場所を `args[k].p` に渡し、host が pointer の先に書く。int / float / bool / 参照の out の場所は結果の register そのもの (slot の先頭の `int32_t` / `float` / 1 byte の `bool` / `void *`) で、host が書いた参照は frame の register にあるので GC の root になる。struct の out の場所は一時の塊で、host から戻ったら VM が結果の register (out の葉) に写す。`rets` は戻り値だけで、out の値は入らない。
- struct の戻り値は、host が `rets[0].p` の塊に書き、VM が戻り値の葉に写す。
- 塊にできる struct の field は int / float / bool / enum と、それだけでできた struct (`T?` は `{ bool has; T v; }`) だけ。参照の field を持つ struct は、塊に置いた参照が GC から見えないので、読み込みで断る。
- 写し方 (項ごとの葉の register、塊の offset、領域の大きさ) は、読み込みで module の型の表から作る (`tcsvm_host.c` の `tvm_host_plans`)。検査は CALLR ごとに、register の葉の種類を struct の葉と照らす。`S` も `&` も無い関数は、register をそのまま `args` / `rets` に渡す。
- 一時の領域 (host に渡す引数の列、戻り値の slot、塊) は VM の stack の今の frame の後ろに取る。host が `tvm_invoke` で VM に戻ると、入れ子の frame はその後ろから積むので、入れ子で呼ばれた host の関数の塊とは重ならない。
- host から VM の関数を呼ぶのは `tvm_call` / `tvm_invoke` (closure) / `tvm_invoke_method`。host の関数の中から VM に戻る入れ子を許し、入れ子の深さを VM が数える (runtime と host からの入れ子は深さ 200 で stack overflow の fault)。
- `ref` / `in` の引数は C# のサブセットに無い (TCS1001) ので、`&` は out だけに使う。

## 9. module 形式と読み込み

### 9.1 単位

1 つの program (入力の C# ファイル全部) が 1 つの module (`.tcsb`) になる。module は entry、`--export` (`C.M` か `C.*`) で選んだ関数、static initializer から到達する関数だけを持つ。C backend の module と 1 つの process に並べる形 (VM の module と C の module が heap を共有して呼び合う) は object-model §10。

### 9.2 `.tcsb` の構成

little endian、pointer の size に依らない (wasm32 と 64 bit で同じ byte 列)。

```text
header      magic "TCSB" / 形式の版 / 命令の表の hash
strings     名前の表 (UTF-8)
constants   string の literal
types       型記述子の表 (VM と C が共有する byte 列の形。object-model §4.2)
selectors   interface の method (名前、関数型)
intrinsics  使う runtime と host の関数の名前 (無い名前は読み込みエラー)
statics     static の slot ごとの種類
static fields  static の field (名前、先頭の slot、種類と型)。reload が名前で static を移す
functions   名前、source file、引数・戻り値・register の数、register の種類表、戻り値と捕捉の種類、命令列、行の表 (命令の番号 → 行)
entry       entry の関数の番号
debug       関数ごとの path と、--trace の行と値の場所の表 (§14)
```

static initializer は名前が `<static>` で終わる関数で、読み込みの最後に並びの順に走る。初期化子が定数でない型の static は遅延初期化で、「初期化を始めた」flag を `型.<init>` の static に持つ。

### 9.3 link

- 命令中の field の葉の番号は、読み込み時に VM が決めた layout (C の struct の規則。object-model §5) の byte offset に書き換わる。型・関数・定数・static・runtime の関数は表の番号のまま
- layout を VM が決めるので、bytecode は pointer の size に依らない。hot reload では新版を別の VM に読んで link し、module ごと入れ替える (§12.1)

### 9.4 検査 (verifier)

bytecode を作るのは tcs の compiler だが、browser では worker から、CLI では file か pipe から届く。壊れた入力で VM が未定義動作に入らないよう、読み込み時に線形の 1 pass で次を確かめる。

- operand の register 番号が frame の範囲内で、種類表と命令の要求が合う (`ADD_I32` の operand は全部 `i32`、`LDF_REF` の object operand は `ref`)
- jump 先が命令の境界で関数の範囲内
- 表の番号が範囲内で、参照先の種類 (型 / field / 関数) と field の型が命令と合う
- 呼び出しの引数と戻り値の register の種類が callee の型表と合う

register の種類が関数内で固定なので、data flow 解析 (JVM の verifier の型推論) は要らない。合わない入力は読み込みエラーにする。compiler の bug をここで早く見つける役目も持つ。

## 10. GC

### 10.1 方式

- 動かさない mark-sweep。object は header ごとに確保し、全 object を 1 本の列でつなぐ
- root は static、保存された VM の frame (種類表で `ref` の register だけを見る)、runtime の pin、host の handle、`--trace` の記録と状態の控えが持つ object
- 参照の位置は型記述子の葉から分かる (共有の walker、object-model §7)。精密 GC で、保守的な走査は無い
- object を動かさないので、host の C が持つ pointer は回収の前後で変わらない
- C の module と heap を共有するとき (`tvm_use_heap`、object-model §10.4) は、確保は C の runtime の nursery に置き、回収は C の runtime の GC がフレーム境界でだけ行う (VM の確保の地点では回収しない)

### 10.2 いつ回収するか

- 回収は interpreter の確保の地点 (今の frame を保存してから) とフレーム境界 (`tvm_frame_end`) でだけ起きる。前の回収から確保した量が閾値 (前の回収で生きていた量と 1 MiB の大きい方) を超えたら回収する
- host が OnFrame などから戻った後に `tvm_frame_end` を呼ぶ。このとき VM の stack は空で、root は static と host の handle だけ
- register の種類が固定なので、フレーム途中でも stack を精密に辿れる。テストや長い Main でも memory が増え続けない
- `TvmConfig.gc_stress` (`tcsvm --gc-stress`) は確保の地点とフレーム境界ごとに回収し、root の漏れを出力の違いで見つける

### 10.3 host との約束

- host は VM の object を C の変数に持ち越さず、frame を跨いで持つものは `tvm_hold` の handle にする (handle 表が root。reload で object が作り直されたら handle は新しい object を指す)
- host の関数の中では、`tvm_invoke` などで VM に戻らない限り回収は起きない。host が object を作って `tvm_call` に渡すまでの間も回収されない

## 11. fault

- il-spec §12 の fault は全部、命令か runtime 関数の中で検出する (null の member、範囲外、0 除算、除算 overflow、変換不能、cast 失敗、switch 式の no-match、`NVAL`、runtime の error)
- fault は VM の stack を捨てて `tvm_call` から `TVM_FAULT` を返す。`longjmp` は使わず、native 関数の戻り値 (`TvmStatus`) で入れ子を抜ける (emscripten の `setjmp` の費用と、MSVC での移植の手間を避ける)。host の関数は `tvm_host_fault` で fault を返す
- fault の情報は `tvm_error` の message で、種類と C# の stack trace (`fault: division by zero` に続いて `  at FaultDiv.Div (FaultDiv.cs:8)` の行が内側から外側へ) を持つ。trace は関数の行の表 (§14) から作る
- 観測としての fault の位置 (il-spec §12) は、命令が IL の評価順のまま並ぶので保たれる
- dev の host は fault の後に process を止めなくてよい。fault したフレームの後で OnFrame を呼ぶのを止め、次の reload を待って再開する、といった扱いは host の道具であって il-spec の意味論の外

## 12. hot reload

hot-reload-design の契約 (§4 の原則、§5 の分類、§8 の hook、§9 の構成、§11 の規則表) を、VM は `tvm_apply` で当てる。

```text
旧版の IL ──┐
            ├─ ReloadRules (restart の理由) + ReloadPlan.Compute (型と static の計画)
新版の IL ──┘        │
                     └─ VmEmitter.EmitReload ── 新版の module + 計画 (batch)
                                                   ↓
                                    tvm_apply / tvm_apply_batch (VM)
```

計画の byte 列の形は object-model §6。`tcs vm --reload-from OLD.cs ... --plan OUT.plan | --batch OUT.batch` と、増分 session (§15.3) が作る。

### 12.1 適用の手順 (`tvm_apply`)

host は新版の module と計画 (か、その 2 つを 1 つにした batch) を `tvm_apply` / `tvm_apply_batch` に渡す。VM の frame が無く、host の関数の入れ子も pin も無いとき (= フレーム境界) だけ受け付ける。

1. **読み込み**: 新版を別の VM に読み、parse / verify / link する。失敗したら `TVM_LOAD_ERROR` を返し、状態は旧版のまま
2. **計画**: 計画に restart の理由があれば `TVM_RESTART`。型を名前で対応させ、計画が「変わらない」と言う型の layout が新版で違えば、計画と module の食い違いとして `TVM_LOAD_ERROR`。生きている object の型と closure の関数が新版にあるかを確かめ、無ければ `TVM_RESTART` (§12.3)。ここまでは状態を変えない
3. **反映**: layout の変わる型の object を新しい layout で作り直し (§12.2)、ほかの object は型を新版に付け替え、closure を新版の関数に付け替え (§12.4)、static を名前で移し、module を入れ替え、全部の参照 (object、static、handle、C の module の root) を転送先へ張り替える。ここに失敗の経路は無い (確保の失敗は abort)
4. **initializer**: 足した field と型を変えた field の initializer を、作り直した instance ごとに走らせる。足した static の initializer (`型.<reload-static>` の関数) を走らせる。遅延初期化の型でまだ初期化していなければ走らせない (最初の読み書きで全部を初期化する)
5. **hook**: `OnReload()` を持つ型の生きている instance ごとに `OnReload()`、続いて entry の型の static の `OnReload()` (hot-reload §8)

initializer は新版の関数なので、型と heap が新版で揃った 3 の後に走らせる (旧版の heap のまま走らせると `is` と cast が型と合わない)。4 と 5 の fault は警告で、`tvm_apply` は `TVM_OK` を返し `tvm_error` が警告を持つ。反映は巻き戻さず、fault した initializer の field は 0 の値のまま。

状態の控え (`tvm_snapshot_take`) は static を名前で新版の slot へ付け替える。作り直した object があれば、それより前の控えは戻せなくなる (`tvm_layout_version` が増える)。

### 12.2 layout の変わる object の作り直し

計画が RELAYOUT と言う型 (field の名前・種類・型が変わった class と struct、その struct を field や要素に持つ型) の値を、反映の中で 1 回だけ作り直す。

- class の instance: 新しい layout の object を確保し、計画に従って field を写す (RETAINED は名前で対応する旧版の field、struct の field は struct の計画で再帰する、ADDED と RESET は 0 の値から始めて 4 で initializer)。旧 object に転送先を書く
- struct を要素に持つ配列は作り直す。`List<S>` は List の object のまま要素の buffer を新しい要素の大きさで確保し直し、Dictionary は object のまま entry の列を確保し直す (entry の番号は変えないので bucket はそのまま使える)。捕捉される変数の cell は class と同じく作り直す
- struct の static は葉を値に組み立て、struct の計画で移してから葉に戻す
- 最後に root (static と handle) と全部の生存 object の参照を辿り、転送先へ張り替える。参照が全部張り替わるので、プログラムから見た identity (`==`、Dictionary の key、`is`) は保たれる
- 費用は heap の大きさに比例し、reload を当てるときにだけかかる。dev 専用のヒッチとして許す (hot-reload §9 と同じ扱い)

### 12.3 restart になるもの

`TVM_RESTART` を返すのは次の場合で、どれも状態は旧版のまま。host は restart の導線へ進む。

- 計画が restart の理由を持つ (compiler の `ReloadRules`。hot-reload §11 の規則表)
- 生きている object の型が新版に無い (`a live object has type X, which the new version does not have`)
- 生きている closure の関数に、新版で同じ形の対応が無い (§12.4)
- 旧版が string の定数を持つのに、新版に string の型が無い

### 12.4 closure の関数の対応

古い関数は残さず、生きている closure を新版の関数へ付け替える。古い関数を新版の上で動かすには、命令の operand (型、field の offset、関数、定数、static、selector、runtime の関数) を全部名前で引き直す link が要り、動いても編集が既存の closure に効かないため。

対応の規則 (`tcsvm_reload.c` の `map_proto`)。lambda の名前は `外側の関数<lambdaN:本文の指紋>` で、どれも引数・戻り値・捕捉の (種類、型の名前) が同じことを条件にする:

1. 同じ名前の関数 (外側・番号・本文が同じ lambda を含む)
2. 同じ外側の関数の中で本文の指紋が同じ lambda がちょうど 1 つ (前に lambda を足しても対応する)
3. 本文を変えた lambda は、外側の関数の lambda の数が変わらないときだけ同じ番号の lambda (新しい本文が効く)

どれにも当たらなければ `TVM_RESTART`。

## 13. host API

### 13.1 C API (`tcsvm.h`)

主な関数 (全部と各関数の約束は `tcsvm.h`):

```c
TvmVm     *tvm_new(const TvmConfig *config);          // stack の上限、frame の上限、gc_stress
void       tvm_free(TvmVm *vm);
int        tvm_host(TvmVm *vm, const char *name, const char *params, const char *rets,
                    TvmHostFn fn, void *user);         // tvm_load の前に host の関数を登録する
TvmStatus  tvm_load(TvmVm *vm, const uint8_t *bytes, size_t size);
TvmStatus  tvm_apply(TvmVm *vm, const uint8_t *module, size_t size, const uint8_t *plan, size_t plan_size);
TvmStatus  tvm_apply_batch(TvmVm *vm, const uint8_t *batch, size_t size);   // §13.3 の vm_batch
int32_t    tvm_find(TvmVm *vm, const char *name);      // "Game.OnFrame" (関数表の番号)
int32_t    tvm_entry(TvmVm *vm);
TvmStatus  tvm_call(TvmVm *vm, int32_t f, const TvmSlot *args, size_t nargs, TvmSlot *rets, size_t nrets);
TvmStatus  tvm_invoke(TvmVm *vm, void *closure, const TvmSlot *args, size_t nargs, TvmSlot *rets, size_t nrets);
void       tvm_frame_end(TvmVm *vm);                   // GC の地点
const char *tvm_error(TvmVm *vm);                      // エラー、fault (C# の trace)、reload の警告
TvmHandle  tvm_hold(TvmVm *vm, void *object);          // frame を跨いで持つ (tvm_handle_get / tvm_release)
// host の関数の中で使う: 失敗、VM の object の field (名前で offset を引く)、string
TvmStatus  tvm_host_fault(TvmVm *vm, const char *message);
int32_t    tvm_field_offset(TvmVm *vm, const char *type, const char *field);
void      *tvm_object_new(TvmVm *vm, const char *type);
const char *tvm_object_type(const void *object);
void      *tvm_string_new(TvmVm *vm, const char *data, size_t len);
const char *tvm_string_data(const void *string, size_t *len);
```

ほかに、型記述子で object / List / 配列を作って読む関数 (`tvm_type`、`tvm_list_*`、`tvm_array_*`)、C の module との混在の関数 (`tvm_use_heap`、`tvm_host_type`、`tvm_host_iface`、`tvm_host_delegate`、`tvm_invoke_method`。object-model §10)、検査の口 (§14) と状態の控え (`tvm_snapshot_*`) がある。

- host の関数の名前は IL の host の名前 (`--ref` の stub の `Eng.Gfx.Begin` は `eng.gfx.begin`、static の値 `Gfx.MainTex` は引数の無い `eng.gfx.main_tex`)。enum の定数は module に literal で入る
- params は C# の引数の順の項、rets は戻り値の項で書く (`i` int と enum、`f` float、`b` bool、`s` string、`o` ほかの参照、`S{型}` struct の値、params の `&` は out)。struct と out は C の layout の塊と pointer で渡る (§8.5)。module が使う host の関数が無いか、struct の型が module に無いか塊にできないか、呼び出しの形が合わなければ読み込みエラーにする。
- `--ref` の stub の struct (`record struct` / `readonly record struct` を含む) は module の型の表に入り、VM の中ではほかの struct と同じ値型になる。object initializer の `new Vec2 { X = 1 }` は、0 の値に field を入れる。
- stub の data class (record) の object は型記述子の layout を持ち、host は field の offset を名前で引いて読み書きする (doc/object-model.md §5)。型の名前は IL の名前 (`Eng_Opts`)、field は IL の名前 (`title`)
- 回収は interpreter の地点とフレーム境界だけなので、host の関数の中と、host が object を作って `tvm_call` に渡すまでの間は回収されない
- `tvm_find` は関数表の番号を返し、reload は番号を保たないので、host は reload の後に引き直す
- 検査: `tcsvm/tests/host/` (`tcsvm/run-tests.sh`)。host_test.c が stub の面を登録して Game を呼ぶ。struct の値と戻り値、scalar と struct の out、入れ子の struct、`T?`、host から VM に戻る入れ子、登録が module と合わないときの読み込みの拒否 (Reject.cs) を見る。

### 13.2 host への組み込み

VM を組み込む host の流れ:

1. `tvm_new` で VM を作り、`tvm_host` で host の関数を全部登録してから `tvm_load` で module を読む (static initializer が走る)
2. entry の関数 (OnInit / OnFrame など、`tcs vm --export` で module に入れたもの) を `tvm_find` で引き、フレームごとに `tvm_call` で呼んでから `tvm_frame_end` を呼ぶ。frame を跨いで持つ object は `tvm_hold` の handle にする
3. reload の batch (§13.3 の `vm_batch`、web では session の応答) を受け取ったら、フレーム境界で `tvm_apply_batch` を呼ぶ。`TVM_OK` なら関数を `tvm_find` で引き直す (警告は `tvm_error`)。`TVM_RESTART` なら新しい module (batch の中の module か `-o` の出力) で VM を作り直す。`TVM_LOAD_ERROR` なら旧版のまま続ける
4. `TVM_FAULT` は `tvm_error` が C# の trace を持つ。止めるか、次の reload を待つかは host が決める (§11)

`tcsvm --lib CLASS N` は同じ流れの最小の host (Setup → (Frame → フレーム境界) × N → Report) で、`--reload K NEW.tcsb PLAN` / `--reload-batch K BATCH` で K 回目のフレームの後に新版を当てる。

### 13.3 watch の stream

`tcs vm --watch -o OUT.tcsb` は入力が変わるたびに直前の build からの reload を作り、`-o` を書き直してから、標準出力へ `@@tcs_<kind> <UTF-8 の byte 数>` の行と本文の組を書く。最初に通った build は `-o` を書くだけ。`--inputs-from LIST` は入力の一覧の file を読み、書き換わるたびに読み直す。

| kind | 本文 | host の扱い |
|---|---|---|
| `vm_batch` | batch の byte 列 (u32 の module の長さ、module、u32 の計画の長さ、計画) を base64 にした 1 行と改行 | フレーム境界で `tvm_apply_batch` |
| `reload_notes` | live に当てたが知らせたい変化 (instance の field の初期化子の変更など) の行。`vm_batch` の前に送る | 表示する |
| `restart_required` | restart の理由の行 | `-o` を読み直して VM を作り直す。以後の差分はその build から作る |
| `build_failed` | 診断の行 (stderr にも書く) | 表示する。最後に通った build のまま次の編集を待つ |

どの kind の本文も改行終端のテキスト行。検査は `tcsvm/tests/host/watch_test.py`。

## 14. debug 情報と検査の口

関数ごとに次を持つ。

- 行の表: 命令の番号 → 行。関数の source file と合わせて fault の trace (§11) に使う。IL の文と式が持つ `Origin` (構文の span) から作る
- `tcs vm --trace PATH_PREFIX` で作った module は、prefix の下のファイルの関数の文の頭に `TRACE`、代入の後に `TVAL` を置き、module の後ろに行の表と値の場所の表を書く

host はこれらを `tcsvm.h` の「検査」の関数で読む。

- 通った行と、代入の場所ごとの最後の値 (`tvm_trace_take_lines` / `tvm_trace_take_values`)
- 呼び出しの並び (`tvm_stack`。host の関数の中から)
- 値の木 (`tvm_inspect`): entry と `--trace` のファイルの class の static から辿れる値を、C# の書き方の path (`Game.Field.Items[2]`) で 1 段ずつ返す。`tvm_inspect_instances` は class の instance を列挙する
- 行ごとの負荷 (`tvm_profile_*`): `--trace` の行ごとの時間と確保、GC の止まり、frame を跨いで持ち越した物
- 状態の控え (`tvm_snapshot_take` / `tvm_snapshot_restore`): 同じ static から辿れる object の中身を写して持ち、同じ object に書き戻す

reload は module ごと入れ替え、古い関数を残さない (§12.4) ので、reload の後の trace と行は新版のもの。

## 15. compiler 側 (VmEmitter)

### 15.1 入力と共有

- 入力は C backend と同じ `IlExport` の結果で、generic は特殊化済み (`Session.Specialize`)
- 式の静的型は VmEmitter が IL から求める (`FunctionCompiler.TypeOf`)。C backend と `TcsC` の部品 (`ContractFacts`、型記述子の表 `TypeTable`、誤りの記録 `CFail`) を共有する
- runtime の algorithm は `runtime/` の C を C backend と共有する: List.Sort の introsort (`runtime/sort`。.NET と同じ比較の呼び出し列)、数値の書式 (`runtime/fmt`)、数学関数 (`runtime/math`)、UTF-8 (`runtime/utf8`)、型記述子と walker と reload の計画 (`runtime/model`)。Random (xoshiro256**。同じ seed なら同じ列) と Dictionary (列挙は挿入順) は VM と C がそれぞれの実装で持つ。hot-reload §10 の規則 4 (runtime を片側だけに持たない) の通り

### 15.2 register 割り当て

- IL の local は identity を持つ (il-spec §7)。各 local に、型の葉の数だけの register を割り当てる。scope が重ならない local は同じ種類の register を共有してよい
- 捕捉される local は cell を指す `ref` register 1 つ
- 式の temp は種類ごとの stack で割り当て、文の終わりで空きに戻す。register は種類と型が同じものだけを使い回す
- 呼び出しの引数は連続する区画が要るので、同じ並びの区画を文の間で使い回す

### 15.3 増分 session

- `VmSession` (`SelfHost/Tool/VmRun.cs`) は最後に通った build を持ち、編集ごとに module (最初の build)、reload の batch、restart (新しい module と理由)、error (最後に通った build のまま) のどれかを返す。`tcs vm --watch`、web の tcs (`Playground.cs`、`SelfHost/Web/tcs.mjs` の `vmOpen` / `vmUpdate`)、`WasmCompiler` の `VmExports` が同じものを使う
- 編集を跨いで compile の session を持ち、内容の変わらないファイルは parse し直さない。束縛、IL、特殊化、emit は program 全体でやり直し、module は program 全体で 1 つ作る
- reload の計画は、最後に通った build の型と static の記述 (`VmDescription`) と新しい build から作る

## 16. 検証

新しい種類の検査は作らず、C backend と同じ差分のゲートで VM を見る。

| ゲート | 内容 |
|---|---|
| Transpiler.Tests の semantic テスト | `TestHelper.CompileAndRunVm*` が VM で走らせる。`TCS_DIFFERENTIAL=1` (`run-differential.sh`) なら .NET の stdout とも比べる |
| tcs2c.Tests の差分 | `Backends.AssertParity` が C (通常と GC stress)、VM (通常と `--gc-stress`)、.NET の stdout の一致を求める |
| `tcsvm/run-tests.sh` | `tcsvm/tests/` と `samples/` の 6 本と digest kernel を VM で走らせ、期待の stdout と、C が動かせる入力は C の stdout と比べる。fault のテストは VM の trace が C# の file と行を指すことも見る。host、`--trace`、inspect、profile、mixed、modules、watch の試験も持つ |
| digest kernel | `tcsvm/run-tests.sh` (VM)、`SampleParityTests` (C)、`tcs2c/verify-digests.sh` の固定の digest |
| fuzz | `run-fuzz.sh`: 生成した program を VM (通常と GC stress) と .NET で走らせて比べる。hot reload の fuzz の scenario も VM に当てる |
| hot reload | `VmReloadTests` (fuzz の scenario の不変量、relayout、session)、`VmReloadCaseTests` (個別の編集)、`ReloadRulesTests` (restart と live の規則) |
| 読み込みの頑健さ | `tcsvm/fuzz-loader.py` が `.tcsb` を変異させ、ASan / UBSan 付きの VM で loader と verifier がエラーを返し crash しないことを見る |
| web | `WasmCompiler/vm-smoke.mjs` (headless chromium で `VmExports` の module と batch を tcsvm で当てる) |
| perf | `perf/bench-2backend.sh` が同じ kernel を VM と C で走らせ、digest の一致を fail のゲートにする (ms/frame は報告だけ) |

## 17. 将来の余地

- debugger: breakpoint と step。行の表と検査の口 (§14) の上に作る。native は host 内の DAP server、web は page 内の panel から同じ VM の API を使う
- 命令の融合 (superinstruction) や quickening。perf の実測を見てから

## 18. リスク

| リスク | 対処 |
|---|---|
| C / VM / .NET の意味が割れる | 差分ゲートで見つける。割れたら il-spec を正本に直す (il-design §2 の方針のまま) |
| 増分 session と特殊化 | 編集ごとに program 全体を compile し直す (§15.3)。program が大きくなって編集の反映が遅くなったら、session の再利用の範囲を広げる |
| layout を変える reload の bug が state を壊す | dev 専用の機能で、hot reload の fuzz と個別の編集のテストで守る |
| web (wasm) で interpreter の dispatch が遅い | wasm の build で computed goto と switch (`TVM_SWITCH_DISPATCH`) を測り比べる |

## 19. 関連文書

- `doc/il-spec.md` — 意味論の正本。VM はこれを直接実装する
- `doc/il-reference.md` — IL のノードと IlExport (VmEmitter の入力契約)
- `doc/il-design.md` — 2 backend 構成と lowering の 2 モード
- `doc/hot-reload-design.md` — reload の契約 (live と restart の分類、hook)
- `doc/object-model.md` — VM と C が共有する型記述子、layout、reload の計画、heap の walker、module 単位の混在
- `perf/README.md` — 性能の測定 (`perf/bench-2backend.sh`)
- `tcs2c/README.md` — 同じ IL を native 表現にする C backend
