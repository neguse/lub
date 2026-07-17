# 仕様ベース conformance / differential testing 設計

> Status: 提案（未着手。着手時に C0 から順にタスク起票する）
>
> Date: 2026-07-17
>
> Scope: `Transpiler.Tests` の conformance ハーネス / `deps` submodule / run-tests ゲート。
> `dotnet/csharpstandard` は読み取り専用参照。

## 1. 結論

「check が通った C# は C# どおりに動く」という tcs の契約を、手書き期待値に依存しない
2 つのオラクルで機械検証する。

- **仕様オラクル**: ECMA-334 作業リポジトリ [dotnet/csharpstandard](https://github.com/dotnet/csharpstandard)
  の注釈付きコード例（corpus の出所が仕様そのもの）
- **実装オラクル**: 同一ソースを実 .NET で実行した出力との differential 比較
  （期待値の出所が実 C# 処理系）

両者は対立ではなく合流する。仕様例のうち実行出力が明記されているものは少数
（下記実測）のため、実行検証の主力オラクルは dotnet differential であり、
仕様 corpus はその入力集合を「仕様の章立てで網羅された例」に置き換える役割を持つ。

## 2. 素材の実測 (2026-07-17)

dotnet/csharpstandard を shallow clone して確認した値。

| branch | 注釈付き例 | 備考 |
|---|---:|---|
| draft-v8 | 575 | expectedOutput 30 / inferOutput 42 / expectedErrors 109 |
| draft-v12 | 651 | record class の章記述あり。採用候補 |

- 例の注釈は HTML コメント内 JSON（`template` / `name` / `expectedOutput` /
  `inferOutput` / `expectedErrors` / `expectedWarnings` / `replaceEllipsis` /
  `additionalFiles` など）。文法は `tools/ExampleExtractor/ExampleAnnotationGrammar.g4`
- template は 10 種（standalone-console 系 = 実行可能、standalone-lib 系 =
  コンパイルのみ、code-in-* = 断片をテンプレートへ埋め込み）
- 公式 ExampleExtractor / ExampleTester (MIT) が抽出・検証を実装済み
- ライセンス: 仕様本文 CC-BY-4.0 / ツール MIT。submodule 参照（コピーせず）なら
  再配布に当たらない。レポートへ例文を転載する場合は帰属表記を付ける

## 3. 目標と非目標

### 3.1 目標

- 仕様例 651 個の全数を分類し、**BUG 分類（crash / 診断なしの誤出力）ゼロ**を恒常ゲート化する
- 既存セマンティックテスト corpus（`TranspileAndRun` 326 箇所）の手書き期待値を
  dotnet differential で裏取りし、allowlist 外の出力差ゼロを恒常ゲート化する
- 既知の意味論差（bool 表記・数値書式・文字列長など）を「実行可能な allowlist」として
  一元管理し、support-matrix の既知差異記述と対にする
- サブセット内 AST 生成 fuzz で silent wrong-code の検出網を常設する（max スコープ）
- 章別 conformance レポートを生成し、support-matrix の Y/P 判定に実行証拠を付ける

### 3.2 非目標

- 仕様例を「全部 pass させる」こと。DIAG 分類（サブセット外として診断）は正常であり、
  サブセット拡張の需要シグナルとして記録するに留める
- ExampleTester の完全互換（unsafe / externAlias / executionArgs 等サブセット外の
  annotation 機構）
- dotnet と Lua の完全な意味論一致。既知差異は allowlist + 文書化が正解
- csharpstandard 上流への追随自動化（pin した commit を手動 bump する）

## 4. 分類モデル

sweep は例ごとに次のいずれか 1 つへ確定させる。**どの例も silent drop しない**
（未対応 annotation も UNEXTRACTED として集計に出す）。

| 分類 | 条件 | 検証内容 |
|---|---|---|
| IN-RUN | tcs check pass かつ実行系 template | transpile + Lua 5.5 実行。オラクルと出力一致 |
| IN-COMPILE | tcs check pass かつ lib 系 template | transpile が完走し構文的に valid な Lua を出す |
| DIAG | TCS1001/1002/1003 診断 | 診断が出ること自体が正 (サブセット境界の証拠) |
| CSERR | 注釈が expectedErrors | Roslyn がコンパイルエラーを出すことのみ確認 |
| UNEXTRACTED | annotation が抽出対象外 (pseudo-code 等) | 集計のみ |
| **BUG** | crash / 診断なしで出力不一致 / invalid Lua | **ゲート失敗** |

オラクル優先順: `expectedOutput`（仕様明記）> dotnet 実行出力（differential）。
`inferOutput` の例も dotnet 実行で再取得する（上流 CI の取得値と同じ意味論）。

## 5. アーキテクチャ

```
deps/csharpstandard (submodule, pin)
  → AnnotationParser (HTML コメント内 JSON)
  → TemplateExpander (10 種 → 完全な .cs ソース)
  → 分類パイプライン:
      Roslyn compile ──error──→ CSERR 照合
      tcs check ──diagnostic──→ DIAG
      └─pass→ transpile → Lua 5.5 実行
                 ├─ オラクル比較 (Normalizer + allowlist)
                 └─ dotnet 実行 (in-memory compile + AssemblyLoadContext)
  → conformance-baseline.json 照合 + 章別レポート生成
```

- **抽出は自前実装**を第一候補とする。annotation は JSON でパースが小さく、
  tcs に必要なのは directive の subset のみ。公式 ExampleExtractor の丸ごと実行は
  上流ツールチェーンへの依存が重い。C0 で自前実装の抽出数と公式ツールの抽出数を
  突き合わせ、乖離が大きければ公式流用へ切り替える（判断を C0 の gate に含む）
- **dotnet 実行**は in-memory Roslyn compile + AssemblyLoadContext + Console capture
  を基本とし、timeout / プロセス隔離が必要な例だけ子プロセスへ fallback。
  1 例あたりの実行コストは C0 で実測し、ゲート配置（恒常 or nightly）を決める
- **Normalizer**: 比較前の正規化と例別 allowlist の 2 層。
  - 正規化（全例共通）: 改行、`True/False` ↔ `true/false`、数値書式
    (double の R/G17 表記差)、InvariantCulture 前提
  - allowlist（例別）: 正規化で吸収できない既知差異を
    `Transpiler.Tests/SpecConformance/known-differences.json` に理由付きで登録し、
    support-matrix の既知差異節から相互リンクする
- **baseline**: `conformance-baseline.json`（example id → 分類）を repo に置き、
  分類の後退（IN→BUG、IN→DIAG 等）で fail。改善は baseline 更新をコミットに含める

## 6. 実装フェーズ（最大スコープ）

### C0: corpus 取り込みと分類 sweep — 規模: 中

- deps/csharpstandard submodule 追加（branch は draft-v12 系。**各 draft の
  C# バージョン範囲と annotation 整備状況を冒頭で確認して pin を確定**する）
- AnnotationParser / TemplateExpander / 分類パイプライン（実行なし版:
  IN-RUN 判定までで実行はしない）
- 章別レポート生成と初回 baseline 作成
- gate: 全 651 例が 6 分類のどれかに確定（silent drop ゼロ）。抽出数を公式
  ExampleExtractor の出力件数と照合

### C1: 仕様オラクル実行 — 規模: 小

- IN-RUN のうち `expectedOutput` 付きを Lua 実行し比較。Normalizer 第 1 版
- gate: BUG 分類ゼロ（見つかった BUG は個別タスク起票して修正後に達成）

### C2: dotnet differential — 規模: 中

- in-memory dotnet 実行機構 + `inferOutput` / 出力注釈なし IN-RUN 例への適用
- 既存 `TranspileAndRun` corpus への differential モード追加
  （TestHelper 拡張。Lua 前提期待値・`--ref` 前提のテストは opt-out を明示）
- known-differences.json と support-matrix の相互リンク整備
- gate: 仕様例 + 既存 corpus の全 differential が allowlist 外差異ゼロ

### C3: 恒常ゲート化 — 規模: 小

- 実測時間に応じて run-tests 組み込み or 分離スクリプト
  （`run-spec-conformance.sh` + CI/nightly）を決定
- baseline 後退検知をゲート化。Windows (`run-tests.ps1` 相当) too
- gate: ゲートが CI で赤/緑を正しく出す（意図的に壊して確認）

### C4: サブセット内 AST 生成 fuzz — 規模: 大

- サブセット文法（式 / 文 / class / record / pattern / LINQ 小核）上の
  プログラム生成器。seed 固定・再現可能。数値は整数中心、浮動小数は正規化前提
- 生成 → tcs check → pass のみ dotnet / Lua differential → 差異発見時に
  自動縮小（reducer）して最小再現を保存
- nightly 運用（実行時間 budget を決めて打ち切り）
- gate: 既知バグ（T138-T153 で修正済みのクラス）を意図的に再導入した変異で
  検出できることを確認してから常設

### C5: カバレッジ計測 — 規模: 小

- coverlet で Transpiler の branch coverage を取得し、仕様 sweep + 既存テストで
  踏めていない emitter 分岐を列挙
- 未踏分岐は「corpus に例を足す」需要判定の入力にする（カバレッジ率目標は置かない）

依存関係: C0 → C1 → C2 → C3 は直列。C4 は C2 完了後に独立着手可。C5 は C0 以降いつでも。

## 7. 判断ポイントとリスク

- **branch 選定**: draft-v12 は annotation 整備が draft-v8 より進んでいる (651 > 575)
  ことは確認済みだが、例の annotation 品質（テスト済みかどうか）は branch により
  異なる可能性がある。C0 冒頭で確定する
- **正規化の設計が本丸**: 雑だと偽陽性まみれ、緩すぎると検出網が死ぬ。
  「正規化は全例共通の書式差のみ、意味論差は必ず例別 allowlist + 理由」を原則にする
- **dotnet 実行の速度**: 例 ~650 + 既存 326。in-memory 化で 1 例数十 ms を想定するが
  未計測。budget 超過なら恒常ゲートは baseline 分類の後退検知のみとし、
  differential 全実行は nightly へ逃がす
- **上流変化**: submodule pin で凍結。bump は例の増減を baseline 更新として扱う
- **CC-BY-4.0**: 例文をリポジトリへコピーしない（submodule 参照 + 実行時抽出）。
  レポート・テスト失敗メッセージに例文断片が出るのは引用の範囲で、帰属は
  レポートヘッダに記載する

## 8. 受入条件（最大スコープ完了時）

- 仕様例全数が分類済みで BUG ゼロ、章別レポートが生成される
- 既存 semantic corpus の differential が allowlist 外差異ゼロ
- 後退検知ゲートが run-tests または CI に常設されている
- fuzz が nightly で回り、seed 再現と自動縮小が機能している
- known-differences.json と support-matrix の既知差異記述が 1:1 で対応している

## 9. 関連文書

- [objective.md](../objective.md) — サブセット選定基準（棚卸し表は TODO ではない）
- [doc/support-matrix.md](support-matrix.md) — Y/P/- 判定。本設計の実行証拠の反映先
- [dotnet/csharpstandard](https://github.com/dotnet/csharpstandard) —
  仕様本文 (CC-BY-4.0) / tools (MIT)
