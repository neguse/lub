# tcs VM 設計 — IL から直接 bytecode を出す dev 実行系

> Status: 提案 (2026-10-31)。実装未着手。決定項目は §18。
>
> Scope: dev 実行系 (現行の IL→Lua + Lua 5.5 VM) を置き換える、tcs 専用の
> bytecode と VM。hot-reload-design §10 の選択肢 (A)「自前 IL interpreter」を
> 具体化する。release (tcs2c) と .NET 実行は変えない。lub 側の変更は §13 に
> feature request として列挙する。

## 1. 結論

tcs の IL (`IlExport`) を入力に、型付き register VM 用の bytecode を直接出す。
Lua source の生成も Lua の parse も通らない。

- 実行系は C で書いた小さな VM (`tcsvm/`)。値は型タグを持たない 8 byte の
  slot で、命令が型を持つ (`ADD_I32` / `ADD_F32`)。il-spec の意味論
  (i32 wrap、f32 single rounding、0-based 配列、C# の除算、fault) を
  そのまま実装し、Lua に合わせるための変形 (+1 添字、`n` 長さ、隠れ
  default 引数、IIFE、`LUA_32BITS`) を持たない
- struct は register では葉の field ごとの連続 register、heap では C と同じ
  inline 配置。class の field は固定 offset の load / store。perf が示した
  「boxed TValue と hash 引きが律速」(perf/README) を表現の段階で外す
- **register の種類 (i32 / f32 / bool / ref) は関数ごとに固定**する。
  この 1 つの規則で、読み込み時の型検査 (verifier)、GC の精密な stack 走査、
  debugger の変数表示が同じ表から得られる
- GC は tcs2c と同じフレーム同期の世代別 (T252) を基本にし、上の規則により
  フレーム途中の回収も安全に行える
- hot reload は VM の機能として持つ。method 差し替えは関数表の 1 entry の
  書き換え、layout 変更は「layout を変える GC」(全 heap を精密に辿って
  object を作り直し、参照を張り替える) で行う。weak registry が要らず、
  hot-reload-design §6 の restart detector が必要だった経路 (closure の捕捉、
  `List<S>`、Dictionary の struct 値) も live で移行できる
- module 形式は incremental-module-compilation-design の module artifact の
  中身を bytecode に差し替えたもの。読み込みは revision 単位の atomic apply
- 検証は既存の差分ゲート (semantic テスト、tcs2c.Tests、digest、fuzz、
  spec conformance、hot reload fuzz、lub の frame digest) に VM を 3 本目の
  実行系として加える。Lua の撤去は全ゲート通過後の別決定 (D-V1)

## 2. 背景 — Lua が払わせているもの

hot-reload-design §10 が整理した通り、Lua が買っているのは成熟した VM・
reload に要る動的性・lub の既存統合で、払っているのは次の 4 つ。

1. **意味論の二重実装と変形コスト**: IL は C# の意味 (il-spec) が正本だが、
   Lua backend は付録 A の義務 (添字 +1、範囲検査、`n` 長さ、隠れ default
   引数、tuple の multi-return、IIFE、`__copy`、`LUA_32BITS`) を全部 Lua の
   上に組み立てている。`LuaEmitter.*` と `runtime/tinysystem.lua` の多くは
   この変形のためのコード
2. **性能**: perf の PC 実測で interp/native は 19〜36 倍。表現 (boxed
   TValue と table) が律速で、Lua の C API を直接叩く aot-slot でも 17 倍
   (perf/results-pc.md)。dev-on-device の 10ms 予算を圧迫する
3. **reload の不完全さ**: Lua の table には型が無いので、struct 値の居場所を
   型で辿れない。restart detector (hot-reload §6) と weak registry
   (`--instance-registry`、構築ごとの ephemeron 挿入) はその穴埋め
4. **debug の二重の頭**: source map は行単位で、lowering の temp や IIFE が
   step と変数表示を濁す (hot-reload §10 の評価軸)

tcs は既に、Roslyn 層で型検査した IL を正本にし、C backend (tcs2c) が同じ
IL から型付きの native 表現を作っている。dev 側も同じ IL から型付きの
表現を作れば、上の 4 つは Lua の上で頑張る問題ではなくなる。

## 3. 目標と非目標

### 3.1 目標

- G1: il-spec の観測等価 (§1) を満たす。Lua / C / .NET との差分ゲートが
  全部通る
- G2: hot reload の live 範囲が現行の chunk backend 以上 (§5.2 の live 項目は
  全部 live、§6 の detector 対象も live)
- G3: web dev の SLO (edit-stop → commit ACK の p95 < 500ms) を維持。
  bytecode の読み込みは Lua の `load()` より速い
- G4: perf kernel で lua32 interp より速い (目標値は D-V2)
- G5: VM の C 実装は MSVC / clang / gcc / emscripten でそのまま build できる
  (GNU 拡張を使わない。tcs2c の statement expression 依存を持ち込まない)
- G6: 小さい。C の VM が 1 万行未満、emitter が tcs2c と同程度

### 3.2 非目標

- JIT。どのターゲットでも実行時に machine code を作らない (il-design §5)
- release 用途。出荷は tcs2c のまま。VM は dev 実行系
- 汎用 VM。tcs のサブセット専用で、命令と runtime は il-spec の面だけを持つ
- bytecode の互換を版を跨いで保つこと。bytecode は compiler と VM の同じ版の
  間でだけ有効 (header の ABI hash で照合)。保存形式ではない
- tcs2c の出力と同じ process で object を共有する mixed-mode (il-design §5)。
  将来の余地は §17 に残すが v1 の契約にしない

## 4. 検討した案

| 案 | 内容 | 判断 |
|---|---|---|
| Lua bytecode を直接出す | Lua source の生成と parse を省く | 却下。省けるのは parse だけで、表現 (TValue / table) と意味の変形はそのまま。Lua の binary chunk は公開仕様でなく版で変わり、`load` に検査器も無い |
| WebAssembly を bytecode にする | tcs → wasm。browser は native、PC は wasm interpreter | 却下 (v1)。GC と layout 移行を linear memory 上に自作する点は本案と同じで、そのうえ reload は instance 差し替え (hot-reload §3 L1 相当) に寄る。browser 内 compiler が wasm module を作る手間と debug の写像も重い。web release の候補としては残る |
| tcs2c + dll / wasm 差し替え (hot-reload §10 B) | dev も C で動かす | 却下 (dev)。browser に C toolchain が無く web dev が成立しない |
| IL の木を直接 interpret | IL の .NET object を辿る | 却下。IL は .NET 側の object で、C の VM へ渡すには結局直列化が要る。木の走査は Lua より速くならない |
| **型付き register VM (本案)** | IL を register 機械の命令列へ | 採用。表現を型付きにでき、reload も debug も VM の中で閉じる |

## 5. 全体構成

```text
C# source
  → Roslyn (parse / 型検査 / TCS 診断)            ← 現行のまま
  → IL (IlExport、generic は特殊化済み)           ← 現行のまま (tcs2c と同じ入力)
  → 型付き IL view (式の静的型。tcs2c の推論を共有化) ← V0 で切り出す
  → VmEmitter: register 割り当て + 命令選択 + debug 情報
  → module artifact (.tcsb)  ── 1 source module = 1 artifact
        │
        ├─ CLI: tcs --target vm -o game.tcsb / --watch が batch を stream
        └─ web: WasmCompiler の増分 session が ArrayBuffer で player へ
              ↓
  tcsvm (C): load → verify → link → (reload なら plan / migrate) → 実行
        ↑
  host (lub): native 関数表を登録し、OnInit / OnFrame / OnEvent / OnQuit を呼ぶ
```

構成要素と置き場所:

| 要素 | 場所 | 言語 |
|---|---|---|
| 型付き IL view | `Transpiler/Backend/` (tcs2c の `ContractFacts` / `TypeOf` から C の名前を除いて移す) | C# |
| VmEmitter | `Transpiler/Vm/` (WasmCompiler と `--watch` から使うので Transpiler に置く) | C# |
| VM | `tcsvm/` (`tcsvm.h` が公開 API) | C11 |
| 共有 runtime | `runtime/fmt`、`runtime/math`、`runtime/utf8` (既存) に、sort / PRNG / dict hash を足す (§15) | C |

## 6. 値と型の表現

### 6.1 slot と register の種類

VM の値の単位は 8 byte の slot (`union { int32_t i; float f; uint8_t b; void *p; }`)。
型タグは持たない。各関数の register は次のどれか 1 つの種類に固定される。

| 種類 | 中身 | 例 |
|---|---|---|
| `i32` | int / enum / char | `int`、`Color` |
| `f32` | float | `float` |
| `bool` | 0 / 1 | `bool` |
| `ref` | GC object への pointer か null | class、record、string、配列、List、Dictionary、closure、box、cell |

compiler は register を種類ごとに割り当て、1 つの register に別の種類の値を
置かない (temp の再利用も同じ種類の間だけ)。関数ごとの「種類表」が bytecode
に入り、これが §9 の verifier、§10 の GC、§14 の debugger の共通の根拠になる。

### 6.2 値型

- **struct / record struct / tuple**: register では葉の field ごとに連続する
  register (`Vec2` は f32 が 2 つ、`(int, Vec2)` は i32, f32, f32)。copy は
  連続 register の move。il-spec §10 の copy 地点はそのまま move 命令になり、
  `__copy` 関数は無い
- heap (class の field、配列 / List の要素、static、closure の cell) では
  C と同じ inline 配置 (自然な size と alignment)。layout は VM が読み込み時に
  決める (§9.3)
- **`T?`** (T は i32 / f32 / bool / enum / struct): register では `bool has` と
  T の葉。heap では `{ bool has; T v; }`。tcs2c の release 表現と同じ
- generic は compiler が特殊化済み (IlExport の `specializeGenerics`)。VM に
  型引数は無く、全 register の種類が静的に決まる。Lua backend の隠れ default
  引数 (il-spec 付録 A) は要らない

### 6.3 参照型の object

全 object は共通 header (`TvmType *type; uint32_t gc_flags; uint32_t aux;`) を持つ。

| object | 配置 |
|---|---|
| class / record | header + field の inline 配置 |
| string | header + byte 長 + UTF-8 の octet 列 (不変。il-spec §11) |
| `T[]` | header + 要素型 + 長さ + inline 要素 |
| `List<T>` | header + 要素型 + count + capacity + 別確保の要素 buffer (inline) |
| `Queue<T>` / `HashSet<T>` / `Dictionary<K,V>` | header + 要素型 + C runtime の構造 (tcs2c の実装と同じ algorithm) |
| closure | header + 関数 (§8.4) + 捕捉 cell / 捕捉値の列 |
| cell | header + 1 つの値 (捕捉される変数。il-spec §7) |
| box | header + 1 つの値 (`object` 型に入った値型) |

要素型は `TvmType` (size / alignment / 参照の offset 表 / 等価と hash の関数)
への pointer。`List<int>` と `List<Vec2>` は同じ C の実装を通り、要素 size と
参照 map だけが違う。

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

```text
; Step  params: r0 ref ps, r1 f32 dt
;       regs:   r2 i32 i, r3 i32 n, r4 ref p, r5 f32, r6 f32
      LDI       r2, 0
L0:   LIST_LEN  r3, r0              ; null なら fault
      JGE_I32   r2, r3, L1
      LIST_LDR  r4, r0, r2          ; 範囲外なら fault
      LDF_F32   r5, r4, Particle.Vel.X
      MUL_F32   r5, r5, r1
      LDF_F32   r6, r4, Particle.Pos.X
      ADD_F32   r6, r6, r5
      STF_F32   r4, Particle.Pos.X, r6
      LDF_F32   r5, r4, Particle.Vel.Y
      MUL_F32   r5, r5, r1
      LDF_F32   r6, r4, Particle.Pos.Y
      ADD_F32   r6, r6, r5
      STF_F32   r4, Particle.Pos.Y, r6
      ADDI_I32  r2, r2, 1
      JMP       L0
L1:   RET0
```

field の参照 (`Particle.Vel.X`) は bytecode では記号で、読み込み時に byte
offset へ解決される (§9.3)。Lua 出力では同じ本文が `p.Pos.X` ごとに table の
hash 引きを 2 回行う。

## 7. 命令

### 7.1 形式

命令は 64 bit 固定長。

```text
| op:8 | a:16 | b:16 | c:16 | x:8 |     3 operand 形
| op:8 | a:16 |   imm:32   | x:8 |     即値形 (i32 / f32 の bit / 相対 jump / link 番号)
```

- register 番号は 16 bit (1 関数 65535 個)。struct を葉に展開しても上限に
  掛からない。Lua の 255 register / 200 local の上限 (`LuaLocalRenamer` が
  避けている制約) は無くなる
- 固定長なので decode は 1 回の 64 bit load とシフトで済み、jump 先の計算も
  単純。命令 1 つが 8 byte なのは Lua の 2 倍だが、dev 実行系では問題にしない
- dispatch は gcc / clang では computed goto、MSVC と emscripten では switch
  (`#ifdef` で切り替え、意味は同じ)

### 7.2 命令の分類

全部で 120 前後。型ごとの複製を含めた数で、種類は少ない。

| 分類 | 命令 (代表) | 意味の正本 |
|---|---|---|
| move / 定数 | `MOV`, `MOVN a,b,n` (struct の copy), `LDI`, `LDF` (f32 の bit), `LDNULL`, `LDK` (string 定数など link 表の値) | il-spec §6 (f32 literal は bit 値で持つ) |
| i32 | `ADD/SUB/MUL_I32` (wrap), `DIV/REM_I32` (0 方向、0 除算と `INT_MIN/-1` は fault), `NEG`, `AND/OR/XOR/NOT`, `SHL/SHR` (count & 31、算術), checked の `CADD/CSUB/CMUL/CNEG`, 即値形 `ADDI_I32` 等 | il-spec §5 |
| f32 | `ADD/SUB/MUL/DIV/NEG_F32`, `MOD_F32` (fmodf) | il-spec §6 (strict) |
| 変換 | `I2F`, `F2I` (0 方向、NaN と範囲外は fault), `CHAR` (0〜255 の外は fault) | il-spec §5, il-reference |
| 比較 / 分岐 | `EQ/LT/LE_I32`, `EQ/LT/LE_F32` (IEEE), `EQ_STR` (内容), `EQ_REF` (identity), `JMP`, `JT/JF`, `JNULL/JNN`, 比較と分岐の融合形 `JLT_I32` 等 | il-spec §4, §6, §11 |
| object | `NEW type`, `LDF_{I32,F32,BOOL,REF} a,obj,field`, `STF_*`, `LDS/STS` (static) | il-spec §9, §10 |
| 配列 / List | `NEWARR`, `LDE_* / STE_*` (範囲検査)、`ARR_LEN`, `LIST_LEN`, `LIST_LD* / LIST_ST*`, `LIST_ADD` | il-spec §11, §12 |
| 型 | `ISTYPE`, `ISIFACE`, `CAST` (失敗は fault), `BOX`, `UNBOX` | il-spec §2, §9 |
| nullable | 葉の move と `has` の分岐に展開する。専用命令は `NVAL` (値なしで fault) だけ | il-spec §3 |
| 呼び出し | `CALL`, `CALLI` (interface), `CALLD` (closure), `CALLN` (host), `CALLR` (runtime intrinsic), `RET`, `RET0` | §8 |
| closure | `CLOSURE proto, …`, `NEWCELL`, `LDCELL_* / STCELL_*` | il-spec §7 |
| debug | `BREAK` (debugger が命令を差し替えて使う) | §14 |

`List` の Sort / Dictionary / HashSet / Queue / String / Math / Random /
TinySystem.Text / 数値の文字列化は `CALLR` で C の runtime 関数を呼ぶ。
頻度の高い List の読み書きと長さだけを専用命令にする。

### 7.3 IL の Lua 寄りの部分の扱い

IL には Lua backend のための情報が混ざっている (il-reference)。VmEmitter は
tcs2c と同じくこれを読み替える。

| IL | VM での扱い |
|---|---|
| `IlLit.LuaText` | 型付きの定数に読み直す (V0 で IL に型付きの値を足すのが望ましい — §18 D-V6) |
| `IlIndex.PlusOne` / `Unchecked` | 無視 (0-based のまま)。`Unchecked` は範囲検査の省略に使ってよい |
| `IlLen.NilElements`、`IlCall("table.insert" / "table.remove")` | List の命令へ。`n` 長さは無い |
| `IlIife` | 結果 register を持つ block として inline 展開。関数は作らない |
| `IlTernary` | 分岐と結果 register |
| `IlMultiAssign` / tuple の multi-return | 結果 register の列 (§8.2) |
| `IlIsLuaType` | box の型の検査 |
| `IlForPairs` / `IlForeachDict` | Dictionary の走査命令列 (runtime の iterator を ref register に持つ) |
| `IlStructCopy` | `MOVN` か、heap との間の葉の load / store |
| 型引数の隠れ default 引数 | 現れない (特殊化済み) |

## 8. 呼び出し規約

### 8.1 stack と frame

- VM ごとに 1 本の slot stack (既定 1M slot、設定可)。溢れたら資源 fault
  (il-spec §12 の実装定義の資源 fault)
- 呼び出し側は引数を連続 register `base..base+n-1` に置く。callee の frame は
  `base` から始まる (Lua と同じ窓の重ね方で、引数の copy が無い)
- frame の register は呼び出し時に、引数以外の `ref` 種の register を null で
  埋める。compiler は `ref` 種の register を引数の直後にまとめて置くので、
  1 回の memset で済む。これで GC がいつ frame を見ても未初期化の pointer が無い

### 8.2 戻り値

- callee は戻り値を自分の frame の先頭 (`base`) から書く。struct と tuple は
  葉の数だけの register、out 引数はその後ろに並ぶ (il-spec §10 の multi-return
  と同じ形)。戻り値の register の種類も関数の型表に入る

### 8.3 関数の解決

- VM は関数表 (`TvmProto *funcs[]`) を持つ。method は安定した名前
  (`型名.method名/引数の型列`) で関数表の番号に解決され、`CALL` の即値は
  読み込み時にこの番号へ書き換わる
- hot reload で method の本文が変わると、関数表の entry だけが新しい proto を
  指す。次の呼び出しから新しい本文が走る (il-design §6、hot-reload §5.2 の
  「method body は live」)
- `CALLI` (interface): receiver の型の itable (selector 番号 → 関数表番号の
  整列済み配列) を引く。命令ごとに直前の (型, 関数) を 1 組持つ inline cache
  を付ける。継承が無い (q.md Q15) ので dispatch はこれだけ

### 8.4 closure と method group

- closure は proto への pointer と捕捉の列を持つ。捕捉は il-spec §7 の通り
  変数単位で、捕捉変数は cell に置く (tcs2c の closure と同じ)。捕捉の後に
  書き換えられない変数は値を直接持ってよい (観測等価な最適化。compiler の
  捕捉解析で判断する)
- lambda の closure は作った時点の proto を指し続ける。reload 後も古い本文で
  走る (hot-reload §5.1「capture 済み function 値は古いまま」と同じ契約)
- static method の method group (`IlMethodGroup`) は関数表番号を持つ closure で、
  reload 後は新しい本文を呼ぶ。struct 引数の copy は callee 側の入口で行う
  (il-spec §10)

### 8.5 host 関数

- `CALLN` は host が登録した native 関数を呼ぶ。native の型は
  `TvmStatus fn(TvmCtx *ctx, TvmSlot *args)` で、結果は `args` から書く
- module は使う host 関数を名前と引数型の hash で import し、読み込み時に
  host の登録表と照合する。名前が無い、または hash が違えば読み込みエラー
  (stub と runtime の食い違いを実行前に止める)
- host から VM の関数を呼ぶのは `tvm_call` (callback を含む)。native の中から
  VM に戻る入れ子を許し、入れ子の深さを VM が数える (§10.3)

## 9. module 形式と読み込み

### 9.1 単位

1 source module (C# ファイル) = 1 module artifact。incremental-module-
compilation-design §10 の ModuleArtifact の manifest (module ID、
semantic surface、依存、所有する型、static initializer の thunk と依存順) は
そのままで、descriptor 部分が Lua chunk から bytecode になる。
hot-reload-design §10 の表の「descriptor の中身を IL/bytecode に差し替え」が
これにあたる。

### 9.2 `.tcsb` の構成

little endian、pointer の size に依らない (wasm32 と 64 bit で同じ byte 列)。

```text
header      magic "TCSB" / 形式の版 / compiler ABI hash / referenceAbiHash /
            module ID / revision
strings     名前の表 (UTF-8)
constants   i32 / f32 (bit) / string literal
types       class / record / struct / enum: 名前、種類、layout hash、
            field (名前、型、initializer の thunk、[RenamedFrom])、
            実装する interface、method の selector、OnReload の有無
imports     他 module の型 / field / 関数、host 関数 (名前 + 型 hash)
functions   名前、引数と戻り値の型、register の種類表、命令列、
            relocation 表 (命令中の記号参照の位置)、debug 情報 (§14)
init        型ごとの static initializer の thunk と依存 (StaticInitOrder の結果)
```

### 9.3 link

- 命令中の field / 型 / 関数 / 定数の参照は、module 内の link 表の番号として
  書かれている。読み込み時に VM が layout を決め (自然な size と alignment。
  参照は pointer の size)、relocation 表に従って命令の operand を byte offset /
  関数表番号 / 型 pointer に書き換える
- layout を VM が決めるので、bytecode は pointer の size に依らない。hot reload
  で layout が変わったら、その型を参照する proto を relocation 表から
  link し直す (§12.4)。relocation 表は proto が生きている間は捨てない

### 9.4 検査 (verifier)

bytecode を作るのは tcs の compiler だが、browser では worker から、CLI では
pipe から届く。壊れた入力で VM が未定義動作に入らないよう、読み込み時に
線形の 1 pass で次を確かめる。

- operand の register 番号が frame の範囲内で、種類表と命令の要求が合う
  (`ADD_I32` の operand は全部 `i32`、`LDF_REF` の object operand は `ref`)
- jump 先が命令の境界で関数の範囲内
- link 表の番号が範囲内で、参照先の種類 (型 / field / 関数) と field の型が
  命令と合う
- 呼び出しの引数と戻り値の register の種類が callee の型表と合う

register の種類が関数内で固定なので、data flow 解析 (JVM の verifier の型推論)
は要らない。合わない入力は読み込みエラーにする。compiler の bug をここで
早く見つける役目も持つ。

## 10. GC

### 10.1 方式

tcs2c の T252 と同じ構成。

- nursery: bump 確保の chunk 列
- 回収: nursery の生存 object を old へ copy して昇格、old は mark-sweep
  (動かない)
- write barrier: `STF_REF` / `STE_REF` / `LIST_STR` / `STCELL_REF` 等で、
  old の object に nursery の参照を書いたら dirty にする
- 参照の位置は型の参照 map (§6.3) から分かる。精密 GC で、保守的な走査は無い

### 10.2 いつ回収するか

- 基本はフレーム境界。host が OnFrame から戻った後に `tvm_frame_end` を
  呼び、そこで回収する。このとき VM の stack は空で、root は static と
  host の handle だけ
- register の種類が固定なので、フレーム途中でも stack を精密に辿れる。
  nursery が上限 (設定可) を超えたら、確保の地点で回収してよい。
  tcs2c は「Main 全体が 1 フレーム」で回収しないが、VM はテストや長い
  Main でも memory が増え続けない

### 10.3 host との約束

- host は VM の object を C の変数に持ち越さず、frame を跨いで持つものは
  `tvm_hold` の handle にする (handle 表が root)
- native 関数の実行中 (入れ子の深さ > 0) は、nursery の object を動かす
  回収をしない。native が受け取った string の pointer などが途中で動かない
  ようにするため。この間は nursery を伸ばすだけにする

## 11. fault

- il-spec §12 の fault は全部、命令か runtime 関数の中で検出する
  (null の member、範囲外、0 除算、除算 overflow、変換不能、cast 失敗、
  switch 式の no-match、`NVAL`、runtime の error)
- fault は VM の stack を捨てて `tvm_call` から `TVM_FAULT` を返す。
  `longjmp` は使わず、native 関数の戻り値 (`TvmStatus`) で入れ子を抜ける
  (emscripten の `setjmp` の費用と、MSVC での移植の手間を避ける)
- fault の情報: 種類、message、C# の stack trace
  (`at Game.Update (Game.cs:42:13) [rev 7]`)。trace は §14 の行表から作り、
  reload 後でも proto が持つ revision で正しい版の行を出す
- 観測としての fault の位置 (il-spec §12) は、命令が IL の評価順のまま
  並ぶので保たれる
- dev の host は fault の後に process を止めなくてよい。fault したフレームの
  後で OnFrame を呼ぶのを止め、次の reload を待って再開する、といった扱いは
  host の道具であって il-spec の意味論の外 (D-V5)

## 12. hot reload

hot-reload-design の契約 (§4 の原則、§5 の分類、§7 の 2 段、§8 の hook、
§9 の ReloadPlanner) はそのまま使い、適用形式としての VM backend を足す。

```text
v1 metadata ──┐
              ├─ ReloadPlanner ── live batch / restart + 理由
v2 metadata ──┘        │
                       ├─ chunk backend  (Lua。現行)
                       ├─ module backend (Lua ModuleRegistry。現行)
                       └─ VM backend     (tvm_apply。本書)
```

### 12.1 適用の手順 (`tvm_apply`)

host は batch (revision と module artifact の列) を `tvm_apply` に渡す。
VM の stack が空で、native の入れ子が無いとき (= フレーム境界) だけ受け付ける。

1. **読み込み**: 全 artifact を parse / verify。VM の状態は変えない。
   失敗したら旧状態のまま続行してエラーを返す
2. **計画**: 旧と新の型 metadata を比べ、型ごとに retained / added /
   discarded の field と layout の変化、関数表の差し替え、新しい型と
   static を決める。restart の判定は compiler の ReloadPlanner が済ませて
   いるので、VM は「計画できない差分」(例: 型の種類の変更) を見つけたら
   エラーにするだけ (二重の安全弁)
3. **評価**: added field の initializer を、移行する生存 instance ごとに
   VM で実行し、結果を side table に置く。fault したら side table を捨てて
   旧状態のまま返す (hot-reload §7 の evaluate)
4. **反映**: 関数表の差し替え、型の layout の更新、§12.2 の移行、§9.3 の
   link し直し。ここではエラーの経路を作らない (hot-reload §7 の apply)
5. **hook**: 移行した instance の `OnReload()`、続いて entry の `OnReload()`。
   hook の fault は反映を巻き戻さず警告として返す (hot-reload §7, §8)
6. revision の ACK を host に返す (`@@tcs_commit` 相当)

### 12.2 layout を変える GC

layout が変わる型があるとき、反映の中で 1 回だけ全 heap の回収を行い、
その途中で object を作り直す。

- mark の途中で、layout の変わった型の object に会ったら、新しい layout の
  object を old に確保し、retained の field を名前で写し (struct は再帰的に
  新しい layout へ)、added の field に side table の値を入れ、旧 object に
  転送先を書く
- 配列 / List / cell / box / static の中の struct 値も、要素型から同じく
  作り直す。`List<S>` の buffer は新しい要素 size で確保し直す
- 最後に root (static と handle) と全生存 object の参照を辿り、転送先へ
  張り替える
- 参照が全部張り替わるので、プログラムから見た identity (`==`、Dictionary の
  key、`is`) は保たれる。型の descriptor は同じ pointer のまま中身を更新する
  ので、`ISTYPE` と link 表も有効なまま
- 費用は heap の大きさに比例し、layout の変わる reload でだけかかる。
  dev 専用のヒッチとして許す (hot-reload §9 と同じ扱い)

### 12.3 Lua の reload との違い

| 項目 | Lua (現行) | VM |
|---|---|---|
| 生存 instance の列挙 | weak registry (`--instance-registry`、構築ごとに費用) | GC の heap 走査。定常の費用なし |
| closure が捕捉した struct 値 | 届かない → restart detector | cell の型から届く → live |
| `List<S>` / Dictionary の struct 値 | 型付きの走査が無い → restart | 要素型から届く → live |
| field の型変更 | 新しい型の default に戻す | 同じ |
| identity | table を in-place で書き換えて保つ | 参照の張り替えで保つ |
| restart detector (hot-reload §6) | 要る | VM backend では要らない (Planner は VM 向けの計画では detector を使わない) |

restart のまま残るもの (既存 static の initializer 変更、const の値変更、
enum member の削除と値の変更、top-level 文の編集、型の種類の変更) は
hot-reload §5.2 と同じ。理由は表現ではなく「値の意図を推測できない」こと
だから。

### 12.4 古い本文の扱い

- 関数表から外れた proto でも、closure が指していれば生き続ける
  (proto は GC の対象。dev の session では残り続けてよい)
- 古い proto が参照する型の layout が変わったら、その proto も link し直す。
  参照していた field が消えていたら、その命令を「古い closure が消えた field
  `X` を参照した (rev N)」という fault の命令に置き換える。呼ばれなければ
  何も起きない

## 13. host API と lub への組み込み

### 13.1 C API (`tcsvm.h`)

```c
TvmVm      *tvm_new(const TvmConfig *config);         // stack / nursery の上限、alloc
void        tvm_free(TvmVm *vm);
TvmStatus   tvm_register_natives(TvmVm *vm, const TvmNative *natives, size_t n);
TvmStatus   tvm_apply(TvmVm *vm, const TvmBatch *batch, TvmApplyResult *result);
TvmFunc     tvm_find(TvmVm *vm, const char *name);    // "Game.OnFrame" (関数表番号を返す)
TvmStatus   tvm_call(TvmVm *vm, TvmFunc f, TvmSlot *args, size_t nargs);
void        tvm_frame_end(TvmVm *vm);                 // GC の地点
TvmHandle   tvm_hold(TvmVm *vm, void *ref);
void        tvm_release(TvmVm *vm, TvmHandle h);
const TvmFault *tvm_fault(TvmVm *vm);                 // 種類、message、trace
// native 関数の中で使う: 引数の読み出し、string の作成、object の確保
```

`tvm_find` は関数表番号を返すので、host が OnFrame を引き直さなくても
reload 後の本文が呼ばれる。

### 13.2 lub への feature request

tcs からは lub を変えない (CLAUDE.md の参考リポジトリの扱い)。VM を使うには
lub に次が要る。

| 項目 | 内容 |
|---|---|
| binding の生成 | `lub-gen vm`: `cs-lib/lub_stub.cs` から native 関数表と型 hash を生成 (`lub-gen lua` / `lub-gen tcs` と同じ記述から)。record の引数 (`LubConfigOpts` 等) は VM の layout から C の struct への変換を生成 |
| host | `src/vm_host.c`: `tcs_host.c` と同じ entry 契約 (OnInit / OnEvent / OnFrame / OnQuit、Quit の扱い) を VM で行う。フレームごとに `tvm_frame_end` |
| reload の導線 | `lub Game.csproj` の watch が `tcs --watch --target vm` の stream を読み、フレーム境界で `tvm_apply` |
| web | player の wasm に tcsvm を入れる。playground の worker は ArrayBuffer (transferable) で batch を送り、player が `tvm_apply` し、ACK で「synced rev N」を出す |
| 検証 | native gate の frame digest 比較 (`--digest`) に VM を加える (tcs→Lua、.NET、VM の 3 つが一致) |

### 13.3 watch の stream

hot-reload §9 の stream に kind `vm_batch` を足す。本文は batch の byte 列を
base64 にした 1 行と改行。`reload_chunk` だけを解するホストが行単位で
読み飛ばせる条件 (本文が改行終端のテキスト行で `@@tcs_reload_chunk ` で
始まらない) を保つ。`restart_required` は今のまま。

## 14. debug 情報

関数ごとに次を持つ。fault の trace は最初から使い、debugger は後から
同じ情報で作る。

- 行表: 命令番号 → (source file、行、列)。IL の文と式が持つ `Origin`
  (構文の span) から作る。差分で符号化する
- 変数表: register の範囲 → (C# の名前、C# の型、生存区間)。struct は
  葉の register の組として 1 つの変数に戻す。compiler の temp は載せない
  (Lua の `__tcs_*` temp が変数ペインに漏れる問題が無くなる)
- revision: proto が属する artifact の revision。reload 後の trace で版を
  間違えない

debugger (後の段階): breakpoint は命令を `BREAK` に差し替え、step は行表で
行う。native は host 内の DAP server、web は page 内の panel から同じ VM の
API を使う。hot-reload §10 の評価軸で、Lua 経路より単純になる部分。

## 15. compiler 側 (VmEmitter)

### 15.1 入力と共有

- 入力は tcs2c と同じ `IlExport` の結果 (`specializeGenerics: true`)
- 式の静的型が要る。今は tcs2c が `ContractFacts` と `TypeOf` で IL から
  推論している。V0 でこれを C の名前に依らない形で `Transpiler/Backend/` に
  移し、tcs2c と VmEmitter の両方が使う。tcs2c の挙動は変えない (tcs2c.Tests
  が完了条件)
- runtime の algorithm (List.Sort の introsort、OrderBy の安定 merge sort、
  xoshiro256**、Dictionary の hash と走査順) は、tcs2c が C の文字列として
  埋め込んでいるものを `runtime/` の C ファイルに出し、tcs2c と tcsvm の
  両方が同じものを使う。hot-reload §10 の規則 4 (runtime を片側だけに
  持たない) の延長

### 15.2 register 割り当て

- IL の local は identity を持つ (il-spec §7)。各 local に、型の葉の数だけの
  register を割り当てる。scope が重ならない local は同じ種類の register を
  共有してよい
- 捕捉され、かつ書き換えられる local は cell を指す `ref` register 1 つ
- 式の temp は種類ごとの stack で割り当て、文の終わりで戻す
- `ref` 種の register を引数の直後にまとめる (§8.1)

### 15.3 増分 compile との関係

- 特殊化 (`IlSpecialization`) は今は program 全体で行う。増分 session では、
  閉じた generic の実体をそれを使う module の artifact に入れ、VM は
  同じ名前の実体を 1 つにまとめる。generic の定義が変わったら、それを使う
  module を全部作り直す (semantic surface の依存として扱う)
- emit の単位は module で、session の構成 (incremental-module-compilation-
  design §7) は変えない。session は artifact の生成を backend の interface
  越しに呼び、Lua と VM のどちらでも同じ manifest を作る

## 16. 検証

VM を足すことで新しい種類の検査は作らず、既存のゲートの実行系を 1 つ増やす。

| ゲート | VM で足すもの |
|---|---|
| Transpiler.Tests の semantic テスト | `TranspileAndRun` と同じ形の `CompileAndRunVm` を作り、semantic テスト群を Lua と VM の両方で走らせる |
| tcs2c.Tests の差分 | Lua / C の stdout 一致を Lua / C / VM に (GC stress 版も。VM は nursery を極小にする stress mode を持つ) |
| digest kernel | `Transpiler.Tests/DigestKernels/` の 3 kernel が VM でも一致 |
| perf | `perf/bench-2backend.sh` に `vm` を足す。ms/frame の表に lua32 と並べる |
| spec conformance / fuzz | `run-spec-conformance.sh`、`run-differential.sh`、`run-fuzz.sh` の実行系に VM |
| hot reload fuzz (T235) | v1/v2 の組を VM の `tvm_apply` でも適用し、同じ不変量を検査 |
| 読み込みの頑健さ | `.tcsb` を変異させた入力で、loader と verifier がエラーを返し crash しないこと (fuzz) |
| lub | frame digest の 3 者一致、golden |

Lua を撤去するまでは Lua が差分の相手として残るので、VM の bug は差分で
見つかる。

## 17. 段階導入

| 段階 | 内容 | 完了条件 |
|---|---|---|
| V0 | 型付き IL view と runtime algorithm の共有化 | tcs2c の挙動不変 (tcs2c.Tests、verify-*.sh が通る) |
| V1 | VM の核 (i32 / f32 / bool / class / 配列 / static 呼び出し / 制御) と emitter の最小面、verifier | digest 3 kernel 一致。perf の表で lua32 との比を出し、D-V2 の基準で続行か判断 |
| V2 | IL の全面 (string、List / Queue / HashSet / Dictionary、closure、interface、record、struct、nullable、文字列化、Random、TinySystem.Text、box) | semantic テストの全件と tcs2c.Tests の 3 者一致、spec conformance、fuzz |
| V3 | host 連携 (native 関数表、`tvm_call`、handle、fault trace) と lub の組み込み (§13.2、lub 側の作業) | lub の全 C# サンプルの frame digest が Lua 経路と一致 |
| V4 | hot reload (`tvm_apply`、関数表の差し替え、§12.2 の移行、hook) と watch の stream | hot reload の semantic テストと T235 の fuzz が VM で通る |
| V5 | web: 増分 session の VM artifact、player への組み込み | playground の A1〜A9 と web golden、SLO p95 < 500ms |
| V6 | Lua の撤去の判断 (D-V1)。撤去するなら LuaEmitter、`runtime/*.lua`、`deps/lua`、lub の Lua host | objective.md の「3 つの実行経路」を VM / C / .NET に更新 |

V1 は小さく、続けるかどうかを perf の実測で決める地点。il-design の
「情報を先に買う」原則と同じ。

### 17.1 規模の見込み

| 部分 | 見込み |
|---|---|
| VM の核 (interpreter、frame、呼び出し) | C 2,000 行 |
| loader / verifier / link | C 1,200 行 |
| GC (T252 の移植と、途中回収のための stack 走査) | C 1,000 行 |
| runtime (collections / string / 文字列化の接続。algorithm は §15.1 で共有) | C 2,500 行 |
| reload (計画 / 評価 / 移行 / link し直し) | C 1,200 行 |
| debug 情報と trace | C 500 行 |
| VmEmitter | C# 6,000〜8,000 行 (tcs2c の約 9,600 行より小さい。C の文字列化と runtime の埋め込みが無いため) |

### 17.2 将来の余地 (v1 の契約にしない)

- mixed-mode: tcs2c の object 表現と VM の表現は、header と inline 配置の
  考え方が同じ。将来 AOT した module と VM の module を同じ process で混ぜる
  なら、header と layout 規則を揃えることから始められる (il-design §5、
  付録 C の mixed-mode ABI)
- debugger (§14)
- 命令の融合 (superinstruction) や quickening。V1 の perf 実測を見てから

## 18. 決定が必要な点

- D-V1: VM を dev 実行系として採るか。採るなら Lua は V6 まで差分の相手として
  並走させ、V6 で撤去を判断する (推奨)。並走中は Lua backend への機能追加を
  止めるかも決める (止めると VM への移行が早まり、二重実装の期間が短い)
- D-V2: V1 の続行基準。推奨は「perf の全 kernel で lua32 interp の 0.5 倍以下の
  ms/frame、かつ digest 一致」。満たさなければ表現の見直しか中止
- D-V3: register の種類を関数内で固定する規則 (§6.1)。verifier・GC・debugger
  をこれ 1 つで支えるので推奨。代わりに data flow の verifier と stack map を
  持つ案は複雑になる
- D-V4: GC をフレーム境界だけでなく、確保の地点でも回収してよいとするか
  (§10.2)。推奨は「してよい」(テストと長い Main の memory のため)
- D-V5: dev の host で fault の後に何をするか (§11)。推奨は「OnFrame を
  止めて reload を待つ」。il-spec の「fault は即時停止」は観測の定義として
  残し、host の扱いは dev の道具として別に書く
- D-V6: IL の `IlLit.LuaText` などの Lua 寄りの部分を、型付きの値に直すか
  (§7.3)。V0 で直すと tcs2c と VmEmitter の読み替えが減る。Lua 撤去 (V6) の
  後は Lua 寄りの部分を全部外せる
- D-V7: 命令を 64 bit 固定長にするか (§7.1)。推奨は固定長 (decode と
  register 番号の上限が単純)。memory を詰めたいなら 32 bit + 拡張語の可変長
- D-V8: VM backend の reload で restart detector を外す (§12.3)。VM の移行が
  届く範囲を hot reload の fuzz で確かめてから外す

## 19. リスク

| リスク | 対処 |
|---|---|
| VM が Lua より速くならない | V1 で perf を測って止める (D-V2)。表現が律速という perf の結論が根拠だが、実測で確かめる |
| 3 者 (Lua / C / VM) の意味が割れる | 差分ゲートで見つける。割れたら il-spec を正本に直す (il-design §2 の方針のまま) |
| 増分 compile と特殊化 | §15.3。generic の定義の変更で作り直す範囲が広がるのは許す |
| lub の作業量 (binding 生成、host、web player) | `lub-gen tcs` と `tcs_host.c` が既に「Lua を使わない host」の形を持っているので、それを VM に向ける |
| layout を変える GC の bug が state を壊す | dev 専用の機能で、T235 の fuzz と、移行後の heap を型で検査する debug mode (全 object の参照 map を辿って型の合わない参照を探す) で守る |
| WASM で computed goto が使えず遅い | switch dispatch で測る。web の dev 体験の律速は compile 側 (incremental §2) なので、VM の dispatch は二次的 |

## 20. 関連文書

- `doc/il-spec.md` — 意味論の正本。VM はこれを直接実装する
- `doc/il-reference.md` — IL のノードと IlExport (VmEmitter の入力契約)
- `doc/il-design.md` — 2 backend 構成と lowering の 2 モード
- `doc/hot-reload-design.md` — reload の契約。§10 の選択肢 (A) が本書
- `doc/incremental-module-compilation-design.md` — module artifact と増分 session
- `perf/README.md`、`perf/results-pc.md` — 表現が律速という実測
- `tcs2c/README.md` — 同じ IL を native 表現にする先例
