# TinyC# IL 設計（中間表現と 2 backend 構成）

> Status: 提案（2026-07 design session の決定。実装未着手）
>
> Date: 2026-07-18
>
> Scope: `Transpiler` core の内部再編と IL 仕様。IL→C release backend の実装先は
> `luoc/`（旧 `../luo` から 2026-07-18 に取り込み）。`../lub` は読み取り専用の文脈。決定の経緯と性能 spike は
> `doc/spike-ceiling.md` を参照。

## 1. 結論

tcs 内部を「syntax 走査 → IL → Lua 出力」に再編し、**IL を意味論の正本**にする。

- Lua と C は IL の対称な 2 backend。IL→Lua が dev 実行（現行出力の後継、挙動互換）、
  IL→C が release AOT（`luoc/`。静的 link / dll / wasm）
- IL の意味論の核は C# 形: i32/f32、値型/参照型の区別、0-based 配列、C# の除算規則
- lowering は 2 モード: dev-lowering（layout 間接参照つき、データ移行可能）と
  release-lowering（layout 凍結）
- ホットリロード時のデータ移行 metadata（CLOS 型プロトコル）は IL の一級要素
- 段階導入し、M1（内部再編）は挙動不変で既存テストがそのまま完了条件

## 2. 背景 — なぜ IL か

現行構成は C# →（syntax tree + SemanticModel 走査）→ Lua で、中間に明示的な
表現がない。
このため **Lua 出力が事実上の意味論の正本**になり、2 つの問題を生む。

1. release AOT（Lua→C）は「C# の意味の実装」ではなく「型消去後の Lua の再現」を
   義務づけられ、C# が静的に保証している性質を復元できない
2. C# と Lua の意味論ギャップを、どちら側の姿が「本当」か決めずに
   場当たりで埋めることになる

ギャップの主要項目:

| 項目 | C# | Lua 5.5 | 現行の扱い |
|---|---|---|---|
| 値型 (struct) | 代入=コピー、配列=連続 | なし（table は参照） | TCS1001 未対応 |
| 配列 index | 0-based | 1-based | 生成コードで +1 |
| 整数除算/剰余 | 切り捨て（符号は被除数） | floor | 生成コードで idiv/irem 補正済み |
| 数値型 | int/float/double/long 区別 | integer/number | i32/f32 に統一済み (M4)、double/long は診断 (T226) |
| 連続メモリ | `float[]`, struct 配列 | boxed TValue 列 | なし |
| 型 test (`is`) と継承 | 派生インスタンスも真 | metatable 完全一致比較 | バグ (T222) |
| ループ変数の capture | `for` 変数は全反復で共有 | numeric for は反復ごと | バグ (T221) |

IL を正本にすると、Lua と C は「同一仕様の 2 実装」として対称になり、
ギャップの各項目は「IL の仕様」と「各 backend の負担」に分解される。
2 backend の一致は semantic テスト（`TranspileAndRun` 系）と
固定 workload の digest 比較で担保する。

## 3. 全体構成

```text
C# source
  → Roslyn（parse / semantic / 型検査）
  → syntax tree + SemanticModel 走査（現行資産を維持。IOperation 化はしない）
  → TinyC# IL ── 意味論の正本
       ├→ IL→Lua backend（dev。挙動は現行出力互換）
       └→ IL→C backend（release。luoc/。静的 link / dll / wasm）
```

型検査と診断（TCS1001-1003、analyzer 共有ルール）は現行どおり Roslyn 層。
IL は検査済みプログラムの表現であり、自前の型システムは持たない方針を維持する。

## 4. IL の意味論の核

- **数値**: i32 + f32 のみ。double / long はサブセット外とする。
  理由は性能 floor（Playdate 級 Cortex-M7、FPU は単精度のみ）と、
  全ターゲット・全 backend での digest 一致。Lua 実行は `LUA_32BITS` ビルドを使う。
  現行 transpiler / テストは Lua 標準（double）前提のため移行作業が要る（M4）
- **値型**: struct / record struct をサブセットに追加する（TCS1001 の解除方向。
  「値セマンティクス需要が出るまで保留」の需要が本設計で確定した）。
  IL 上は代入=コピー、struct 配列=連続レイアウトが正。
  release backend は本物の struct / 連続配列に落とす。
  Lua backend は copy-on-assign 生成で意味論を模倣する（表現は §8）
- **参照型**: class は参照意味論。Lua backend は現行の table + metatable
  マッピングを維持する
- **配列 / index**: 0-based が正。+1 変換は Lua backend の負担
  （変換自体は現行と同じだが、正本がどちらかが逆転する）
- **整数除算 / 剰余**: C# 規則が正。補正コードは Lua backend の負担
- 文字列、null/nil、enum、interface 等は現行マッピングを踏襲（別段の決定まで）

## 5. lowering 2 モード

| | dev-lowering | release-lowering |
|---|---|---|
| layout | 間接参照つき（変更に追従） | 凍結 |
| データ移行 | 可（§6） | 不可（不要） |
| 実装 | IL→Lua（stock Lua runtime が loose 実行を提供） | IL→C、静的 link |
| 用途 | 開発 PC / 実機 dev build | 出荷 |

dev build は module 粒度の mixed-mode を想定する: 編集した module は
interpreter へ降格（source push は全ターゲットで可能な唯一の hot path）、
AOT 済み module は native のまま、昇格は次 deploy / OTA。どのターゲットでも
in-process の runtime codegen は行わない（JIT ではない）。

## 6. migration metadata

ホットリロード時のデータ移行を IL の一級 metadata として定義する。
CLOS（CLHS 4.3.6 Redefining Classes）の語彙を踏襲する。

- 各 class: layout version hash、field 列（名前、IL 型、initializer）、
  rename 注釈（`[RenamedFrom]` 相当の attribute）、ユーザーフック
  （`OnReload` 相当のメソッド）
- reload 時は eager migration: constructor が weak registry に登録した
  生存インスタンスへ、新旧 metadata の diff（added / discarded / retained）を適用。
  インスタンスの identity は保つ（table の in-place 更新）
- struct / struct 配列は参照 identity を持たないため再直列化で移行する
- 実行中 frame（coroutine の local 等）は移行対象外。reload は frame 境界で行う

## 7. 段階導入

| 段階 | 内容 | 完了条件 |
|---|---|---|
| M1 | Transpiler 内部を syntax 走査→IL→Lua に再編 | 挙動不変。既存テスト全通過。source module 単位 emit（増分コンパイル設計）と両立 |
| M2 | IL 仕様の文書化と metadata 出力 | IL→C backend の入力契約が固まる |
| M3 | IL→C backend（`luoc/` で実装） | 固定 workload で 2 backend の digest 一致 |
| M4 | i32/f32 数値モデル移行（`LUA_32BITS`） | 全テストが 32bit ビルドで通過 |
| M5 | struct / record struct のサブセット追加 | TCS1001 解除、値意味論の semantic テスト |

実施順は M1 → M4 → M2 → M3 ∥ M5（表の並びと異なり M4 を M3 より前に置く。
double 前提の digest baseline で IL→C backend を書き始める手戻りを封じるため）。
タスク分解は `doc/tasks.md` の IL トラック（T210-T220）。
M1 は純リファクタリングであり、
`doc/incremental-module-compilation-design.md` の常駐 session /
revision 単位 apply の構成を壊さないことを完了条件に含める。

## 8. 未解決の質問

v0 設計（T210 / T211）で決着した項目は `doc/il-spec.md` を正とする:
IL の具体形（in-memory 正規形 + 診断用テキスト表記、シリアライズ形式は M2）、
例外の扱い（fault モデル。try / throw はサブセット外を維持 — il-spec §8 §12）、
手書き Lua との interop 境界（class public 面に限定 — il-spec §15）。

残る未決（il-spec 付録 C と同期）:

- Lua backend での struct 配列表現: table of tables か userdata 連続バッファか
  （`doc/spike-ceiling.md` の `particles` kernel の実測で判断）
- release backend の class 表現（Lua table 式か native struct 式か。spike の
  合否解釈に従う）
- mixed-mode の module 境界 ABI（間接参照の具体形）
- Nullable\<T\> の位置づけ / 合意 PRNG
  （interface への type test は診断化で決着 — il-spec §2、実装は T223）

## 9. 関連文書

- `doc/il-spec.md` — IL 意味論仕様 v0（意味論の正本）
- `doc/il-reference.md` — ノードカタログと IlExport API（IL→C backend の入力契約）
- `doc/il-lowering-examples.md` — 紙の演習（抽象度の決定根拠）
- `doc/spike-ceiling.md` — 方向づけ決定リストの暫定正本、性能上界 spike
- `objective.md` — tcs の目的とデュアルランタイム戦略
- `doc/incremental-module-compilation-design.md` — browser-wasm 増分コンパイル
  （M1 が保つべき既存アーキテクチャ）
