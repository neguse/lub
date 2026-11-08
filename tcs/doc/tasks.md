# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## IL トラック（`doc/il-design.md` M1-M5 の実施）

価値最大化の原則:
1. **情報を先に買う** — 2 大不確実性（release object model / IL 抽象度）を
   コンパイラのコードを書く前に実測 (perf) と紙の演習で決着させる
2. **各段階で独立に価値を着地させる** — 途中で止まっても損しない順序
3. **数値モデルの凍結を C backend 着手より前に** — double 前提で
   IL→C を書く手戻りを封じる

### Phase 0 — 情報を買う + 発見バグ修正（並行可）

### Phase 1 — M1: 挙動不変の内部再編 ✓ (T214a-c 完、done.md 参照)

- [x] T224 完 (2026-07-18): IlExport 契約完備、fallback 構文の整理 (static method group IL 化 / instance method group・定数式 alignment 診断化 / lock・using 等は既存診断)。IL にできない本文は TCS1001


### Phase 3 — 価値の刈り取り

- [x] T218 完 (2026-07-18): M3 IL→C backend (tcs2c)。継承 / Dict / closure /
      ctor 連鎖 / 静的 link (--lib) まで全マイルストーン受入済み
      (digest 3/3 + 全サンプル stdout 一致)。未対応構文は明示エラー方針で、
      対応面の拡張は実利用の需要駆動 (done.md 第一〜第八参照)
- [x] T240 / T241 完 (2026-10-01): tcs2c の GC (精密 heap / 保守的 stack の
      mark-sweep、`-DTCS_GC_STRESS`。T252 で世代別に置換) と対応面の完成 (enum / LINQ / String /
      Math / Dict / format / IIFE)。tcs2c.Tests が samples + 機能別 program の
      2 backend stdout 一致 (GC stress 込み) を恒常ゲートにする
      - T242–T245 完 (2026-10-02): delegate 型 field の直接呼び出し /
        `int.TryParse`、record class の IL 収載と C 構造等価、Nullable<T> の
        IL 明示ノード + lifted 演算子、Random の合意 PRNG (xoshiro256** 移植)
      - T246 完 (2026-10-02): struct / record struct の IL 契約 (member /
        ctor / 等価 / with) と C backend の値型 member、`new T[n]` の default
        要素、List の struct 値等価
      - T247 完 (2026-10-02): `T?` の `?.` / `??=` を明示 nullable ノードへ
      - T248 完 (2026-10-02): Random を System.Random 形 (instance / Shared) に
      - T249 完 (2026-10-02): シフトの C# 意味論、compound 右辺の括り、int.MinValue literal
      - T250 完 (2026-10-02): 未対応本文は TCS1001。nameof / 拡張メソッド / 入れ子 initializer / discard / 式位置の代入 / switch 早期 break を IL 化
      - T251 完 (2026-10-02): char を整数 code unit に (両 backend、Char.*)
      - T252 完 (2026-10-02): GC をフレーム同期の世代別 (nursery bump +
        境界で精密 copy 昇格 + 旧世代 mark-sweep、ライトバリア) に置き換え、
        保守的 stack 走査を廃止。実行形は Main 全体が 1 フレーム
      - T253 完 (2026-10-02): master の Codex 系列 (foreign --ref / interface /
        object / generic 単相化 / 遅延 static 初期化 / 省略引数) をこちらの
        runtime に移植して衝突解消。verify-gc / game-core / object-values /
        host が run-tests のゲート
- T219b 完 (done.md 参照): struct / record struct の値セマンティクス
  対応一式。設計方針の正本は support-matrix / CLAUDE.md / il-spec §10

---

## VM トラック (`doc/vm-design.md`)

V1 (核と verifier) は済み (done.md)。残りは次の通り:

- [ ] V0: C backend の型推論 (`SelfHost/CBackend` の `ContractFacts` / `TypeOf`) を C の名前に依らない形にし、
      VM backend (`SelfHost/VmBackend`) と共有する。sort / PRNG / dict の algorithm を `runtime/` の C に出す
- [x] V2: IL の全面と GC (done.md の V2 の各段)。GC は動かさない mark-sweep (確保の地点とフレーム境界)、interface / 型 test / cast / record、struct の member と `List<struct>` / Dictionary の struct 値、nullable / out / tuple / IIFE、文字列の書式と TinySystem の関数、Queue / HashSet、top-level 文。検査: tcs2c.Tests の差分 (C、VM、.NET の stdout の一致)、Transpiler.Tests の semantic テスト (`VmCorpus`。`TCS_DIFFERENTIAL=1` で .NET とも比べる)、C4 fuzz の比較 (`FuzzDifferentialTests`。C、VM、.NET)
- [ ] V3: host 連携。済み (done.md): `tvm_host` (host の関数表)、host が VM の object と string を読み書きする API、
      `tcs2vm --ref` (stub の関数・値・record)、`tcs2vm --watch` の stream と `tvm_apply_batch`、lub への feature request
      (vm-design §13.2)。
      host の関数の struct と out (C の layout の塊と pointer、vm-design §8.5)、host への callback (`tvm_invoke`)、List / 配列の要素を host が読む API、handle (`tvm_hold`) も済み。残り:
  - [ ] lub 側 (`lub-gen vm`、`vm_host.c`、watch の導線) は lub の作業 (feature request)
- [ ] V4: hot reload。済み (done.md、object-model M-3): `tvm_apply` (共有の計画と walker、layout を変える移行、
      static、closure の付け替え、initializer と OnReload)、`tcs2vm --reload-from`、`tcsvm --reload`、
      `VmReloadTests` (T235 の scenario)。残り:
  - [x] 生きている closure の対応 (vm-design §12.4 の決定: 古い proto は生かさず、lambda の本文の指紋と番号で新版の lambda へ付け替える)
  - [ ] watch の stream と host への組み込み (§13.3、V3 と一緒に)
  - [ ] tcs2c の migrator を同じ計画と walker の上に作り、T235 を両方で走らせる (object-model M-5。tcs2c の reload は
        hot-reload の D6 (release の reload を採るか) の決定待ち)
- [ ] module 単位の混在 (object-model §10): X-1 (境界の自動化、`tcs2c --lib --vm-host`) と X-3 の 1 段目 (`tvm_hold`) は済み。
      X-2 (shared heap)、X-3、X-4 (delegate と interface を両方向に呼ぶ) も済み。string も写さずに渡し、型の番号も合わせた。C の delegate を VM の
      runtime の関数 (List.Sort など) にも渡せる。
      塊にできる struct と out の引数も渡る (VM の host の ABI)。残り: List / Dictionary を境界で渡す
- [ ] object model の M-4 の残り (object-model §9。X-2 と同じ作業)
- [ ] lub への feature request (Dictionary の仕様、support-matrix §18): TinySystem.props が .NET の project で
      `Dictionary` / `HashSet` を TinySystem の実装 (挿入順) にするので、game の Dictionary は BCL の Dictionary と別の型になる。
      `dotnet/Lub` の facade の `Dictionary<string, object>` の引数 (`Gfx.Draw` / `Dispatch` の bindings) は
      `IReadOnlyDictionary<string, object>` で受ける形に lub-gen を直す (TinySystem の Dictionary はこの interface を実装する)。
      直すまでは、tcs の submodule を上げると lub の .NET 実行の build が通らない: header の struct を 1 つに、type_id の番号、容器 (string / List /
      Dictionary / closure) の形、GC と write barrier の共有。済み: class の payload と field の offset (`tcsvm/tests/mixed`)
- [ ] V5: web。済み (tcs 側): 増分 session `VmSession` (`tcs2vm --watch` も使う)、WasmCompiler の JSExport
      `VmExports.Open` / `Update` (実ブラウザの smoke `WasmCompiler/vm-smoke.mjs`、`TCS_WASM_SMOKE=1 bash run-tests.sh`)。
      残り: player と playground の worker は lub の作業 (vm-design §13.2)

---

## 名前の写し方トラック (q.md Q16)

- [ ] host (`--ref`) の interface の実装: 今は `RefInterfaceImplementation(IHostHook)` の TCS1001 で止める (interface 経由の呼び出しは host の snake_case、実装は C# の名前で届かない)。対応するなら、host の interface の member は実装側も host の名前で emit するか、host が C# の名前で呼ぶかを決める。lub_stub に interface は無い

---

## Dictionary トラック (q.md Q18)

- [ ] **T259** (P1): Dictionary の TinySystem 演算 — Where (→ dict) / Any / All / Count(述語) / RemoveAll (in place) / Clear を `(k, v)` の 2 引数 lambda で。`TinySystem.Dict` の拡張メソッド、allowlist、VM backend と C backend の実装、conformance test、support-matrix §18
- [ ] **T260** (P1): `foreach (var (k, v) in dict)` (ForEachVariableStatement) を Dictionary の受け手に限って対応。tcs VM と C は既存の dict foreach と同じ走査

---

## DU / パターンマッチ (q.md Q19)

- [ ] **T264** (P1): tcs2c が `switch` 文 / 式で絞ったパターン変数の member を解決できない (`unknown field: IShape.S`) を修正
- [ ] **T265** (P2): interface 型の入力の switch 式 / 文の網羅性を準拠 error に (default / `_` なしで実装型を覆っていないとき)

---

## 選定基準の適用 (q.md Q20)

- [ ] **T266** (P1): `CompilationDiagnosticPolicy` の緩和 (CS0266 / CS0029 / CS0019 の enum↔int、CS0535 の field-backed interface facade) を撤去し、sample とテストを普通の C# として通る形に直す

---

## Conformance / fuzz トラック（spec-conformance-design.md C4 の継続拡張）

方針 (ユーザー決定 2026-08-07): C# compat の differential fuzz を先に育てる。
hot reload の fuzz は compat 文法の拡張が一巡してから独立に起こす。

- T233 / T234 完 (done.md 参照)。生成文法の現状は FuzzGenerator の
  doc comment が正本
- T235 完 (done.md 参照): hot reload fuzz。v1/v2 ペア生成 + 不変量
  オラクル、run-fuzz.sh / run-tests smoke に常設。拡張候補 (record class
  migration の fuzz) は対象機能の実装が入ってから

---

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) は run-tests の恒常ゲートで守る
