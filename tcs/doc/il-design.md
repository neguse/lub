# TinyC# IL 設計（中間表現と 2 backend 構成）

> Status: 実装。
>
> Scope: compiler (`SelfHost/Compiler`) の IL と、IL を読む 2 つの backend (tcs VM: `SelfHost/VmBackend` と `tcsvm/`、C: `SelfHost/CBackend` と `runtime/`)。性能前提と実測は `perf/README.md` を参照。

## 1. 結論

compiler は C# を検査済みの TinyC# IL にし、**IL を意味論の正本**にする。

- tcs VM と C は IL の対称な 2 backend。tcs VM が dev 実行 (開発と hot reload。`tcs vm`)、C が release (`tcs c`。静的 link / dll / wasm)
- IL の意味論の核は C# 形: i32/f32、値型/参照型の区別、0-based 配列、C# の除算規則
- lowering は 2 モード: dev-lowering（版を跨いで layout を移せる）と release-lowering（layout 凍結）
- ホットリロード時のデータ移行 metadata（CLOS 型プロトコル）は IL の一級要素

## 2. なぜ IL か

backend が 2 つあると、片方の出力を意味論の正本にした時点で、もう片方は「C# の意味の実装」ではなく「その出力の再現」を義務づけられ、C# が静的に保証している性質を復元できなくなる。
C# との意味のずれも、どちら側の姿が「本当」か決めずに場当たりで埋めることになる。

IL を正本にすると、tcs VM と C は「同一仕様の 2 実装」として対称になり、C# との差の各項目は「IL の仕様」(`doc/il-spec.md` §1-§15) と「各 backend の負担」(il-spec 付録 A / B) に分解される。
2 backend と .NET の一致は semantic テスト (`CompileAndRunVm` 系。`TCS_DIFFERENTIAL=1` で .NET とも比べる)、`tcs2c.Tests` の差分 (C、tcs VM、.NET の stdout 一致)、固定 workload の digest 比較で担保する。

## 3. 全体構成

```text
C# source
  → compiler（SelfHost/Compiler: 字句 / 構文 / 宣言 / 束縛 / 準拠検査）
  → TinyC# IL ── 意味論の正本（generic は閉じた型ごとに単相化）
       ├→ VmBackend（dev。tcs VM の bytecode .tcsb。VM 本体は tcsvm/）
       └→ CBackend（release。C のソース。runtime/ の C を埋め込む）
```

型検査と診断（TCS1001-1003）は compiler の束縛と準拠検査の層が行う。
IL は検査済みプログラムの表現であり、自前の型システムは持たない。
Roslyn は compiler の外の道具としてだけ使う (IDE の analyzer、playground の補完と hover、検証の oracle)。

## 4. IL の意味論の核

- **数値**: i32 + f32 のみ。double / long はサブセット外とする。理由は性能 floor（Playdate 級 Cortex-M7、FPU は単精度のみ）と、全ターゲット・全 backend での digest 一致
- **値型**: struct / record struct。IL 上は代入=コピー、struct 配列=連続レイアウトが正。C は本物の struct / 連続配列に落とし、tcs VM は register では葉の列、heap では C と同じ inline 配置にする (`doc/object-model.md` §5)
- **参照型**: class は参照意味論 (identity を持つ heap object)
- **配列 / index**: 0-based
- **整数除算 / 剰余**: C# 規則

## 5. lowering 2 モード

| | dev-lowering | release-lowering |
|---|---|---|
| layout | 版を跨いで移せる（reload で heap の object を新しい layout に作り直す） | 凍結 |
| データ移行 | 可（§6） | 不可（不要） |
| 実装 | tcs VM（`tcs vm` の bytecode を読み込んで実行） | C（`tcs c`）、静的 link |
| 用途 | 開発 PC / 実機 dev build | 出荷 |

実行系は module（C# の project）単位で混ぜられる: editor・engine の層・重い処理を C で固定し、content を tcs VM で reload する (`doc/object-model.md` §10)。
どのターゲットでも in-process の runtime codegen は行わない（JIT ではない）。

## 6. migration metadata

ホットリロード時のデータ移行を IL の一級 metadata として定義する。
CLOS（CLHS 4.3.6 Redefining Classes）の語彙を踏襲する。

- 各 class / struct: layout version hash、field 列（名前、IL 型、initializer）、ユーザーフック（`OnReload`）
- reload 時は eager migration: 生きている object へ、新旧 metadata の diff（added / discarded / retained、型の変わった field は reset）を適用する。tcs VM は layout の変わる object を作り直し、全部の参照を新しい object へ張り替えるので、インスタンスの identity は保たれる (`doc/object-model.md` §6)
- struct の値は参照 identity を持たないため、持ち主の object / 配列 / static の中で struct の計画に従って移す
- 実行中 frame は移行対象外。reload は frame 境界で行う (VM の frame が無いときだけ `tvm_apply` が受け付ける)

規則と編集の分類は `doc/hot-reload-design.md`。

## 7. 未解決の質問

v0 設計（T210 / T211）で決着した項目は `doc/il-spec.md` を正とする: IL の具体形（in-memory 正規形 + 診断用テキスト表記）、例外の扱い（fault モデル。try / throw はサブセット外を維持 — il-spec §8 §12）、外部コードとの interop 境界（class public 面に限定 — il-spec §15）。

残る未決は il-spec 付録 C と同じ。

## 8. 関連文書

- `doc/il-spec.md` — IL 意味論仕様 v0（意味論の正本）
- `doc/il-reference.md` — ノードカタログと IlExport API（backend の入力契約）
- `doc/il-lowering-examples.md` — 紙の演習（抽象度の決定根拠）
- `doc/object-model.md` — tcs VM と C が共有する型記述子、layout、reload の計画
- `doc/vm-design.md` — tcs VM（dev 実行系）の設計
- `doc/hot-reload-design.md` — hot reload の規則
- `perf/README.md` — 性能前提 (KPI floor) と実測ハーネス
- `objective.md` — tcs の目的と実行経路
