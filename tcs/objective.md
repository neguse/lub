# TinyC# — ホットリロード可能なC#サブセット言語

## 概要

C#の極小サブセットをフロントエンド言語とし、Lua 5.5 ソースコードをバックエンドとするトランスパイラ。ゲームスクリプティングにおけるホットリロード、軽量組み込み、クロスプラットフォーム対応を実現しつつ、C#のエディタ体験と型安全性を得る。

開発軸は、C# 側の仕様準拠度と標準ライブラリの網羅性を棚卸ししつつ、**モダンな .NET / C# 開発体験を満たすために必要な最小ライン**を選ぶことに置く。フル C# / フル BCL を実装することは目的ではない。C# の構文・型チェック・IDE 体験を活かしながら、Go のようにコンパクトで予測しやすい言語面と標準ライブラリ面を保つ。

特定エンジンへの直接対応は tcs のコア目的ではない。外部エンジン連携は、必要になった時点で参照ソース、stub、生成器、adapter のどれが妥当かを個別に判断する。過去に lub3d 連携を想定した検討があるが、lub3d 側の方針変更を踏まえ、現時点では tcs 本体の優先タスクとはしない。

## 方向（2026-07 更新）

- **北極星は live-first**: 書いたそばから実行中のゲームに流れる開発体験
  （lub の出自であるトライアンドエラー最速化）が製品の核。バックエンド・
  VM・ツールはその手段
- **進行原則は contract-gated**: 機能・インフラは「契約（design doc /
  CONTRACT）と検証ゲートが書けたら」並列に作ってよい。需要は優先順位の
  入力であって許可条件ではない（AI 実装力を律速にしないため）。契約と
  ゲートなしに実装を積むことは引き続き禁止
- **人間の担当は決定と味見**: 決定キュー（設計文書の D 項目）の処理と、
  薄い縦切りのゲームを常に生かして真実確認すること。ゲームは律速装置では
  なく真実装置
- **1st priority は Windows dev / Windows release / web dev / web release
  の 2×2**。実機（Playdate 級）は 2nd priority で、KPI floor は性能下限と
  してのみ効かせる

## 動機

- **Lua 5.5の強み**：ホットリロード、軽量VM（数百KB）、全プラットフォーム対応、JIT不可環境での動作
- **Lua 5.5の弱み**：動的型付けによるエディタ支援の限界、大規模コードの保守性
- **C#の強み**：Roslynによる成熟したエディタ支援（補完、リファクタリング、型チェック）、広いユーザーベース
- **C#の弱み**：ランタイムが重い、ホットリロードが限定的、一部プラットフォームでJIT不可

TinyC#はこの両者の利点を組み合わせる。

## アーキテクチャ

### デュアルランタイム戦略

|       |dotnetランタイム             |Luaバックエンド           |
|-------|------------------------|--------------------|
|**用途** |エディタ補完・型チェック・単体テスト      |本番実行（唯一）            |
|**実行** |ゲーム実行しない                |Lua 5.5 VM、ホットリロード対応|
|**API**|interface定義 + mock/no-op|エンジン実装              |
|**価値** |開発体験                    |デプロイ                |

dotnet側でゲームを動かさないため、2ランタイム間の意味論一致問題は大幅に軽減される。テストはゲームロジックの純粋関数部分のみ対象。

### トランスパイルフロー

```
C#ソースコード
  → Roslyn（パース・セマンティック解析・型チェック）
  → Bound Tree走査
  → Lua 5.5 ソースコード出力
  → Lua 5.5 VM実行（ホットリロード対応）
```

Roslynを「パーサー兼型チェッカー」として利用し、自前実装を最小化する。

## 言語サブセット

### 選定基準

TinyC# は「C# の全部入り」ではなく、C# 開発者が現代的な書き味を保てる小さな核を狙う。

- **優先するもの**: nullable を含む型チェック、IDE 補完、record / pattern / lambda など日常的な C# の表現力、List/Dictionary/String/Math/LINQ の実用最小セット
- **絞るもの**: 巨大な BCL 面、暗黙に重い機能、意味論差が大きい機能、Lua 5.5 に不自然な機能
- **判断方法**: `doc/support-matrix.md` で棚卸しし、Core / Useful / Out of scope を明示して、必要なものだけ TDD で足す
- **目標感**: C# ベースだが、Go の標準ライブラリ選定に近いコンパクトさと説明しやすさを保つ

### 残すもの

- **型**: int, float, bool, string, class, enum, record, interface
  - ユーザー定義 `struct` / `record struct` は現時点で TCS1001 未対応診断にし、値セマンティクスが必要なサンプルが出るまでは `class` / `record class` で代替する。
- **メンバー**: メソッド, プロパティ, フィールド
- **制御構文**: if/else, for, foreach, while, switch式（パターンマッチング）
- **ラムダ**: `(x) => expr` / `(x) => { stmts }`
- **Nullable annotations**: コンパイル時型チェックのみ（ランタイムコストなし）
- **コレクション初期化構文**
- **継承**: 浅いヒエラルキー（interface + compositionベース）
- **演算子オーバーロード**: 二項 `+ - * / %` と単項 `-` のみ（Lua metamethod へ写像）。
  `==` / `!=` は record の値等価 (`__eq`) だけを提供し、通常 class の operator 宣言は対象外。

### 切るもの

- async / await / Task系全般
- LINQ クエリ構文（`from x in y select`）
- ユーザー定義ジェネリクス（class / struct / method）
- reflection, dynamic, Expression Tree
- unsafe, Span, ref struct
- イベント / デリゲートのマルチキャスト
- 変換演算子 (implicit / explicit) と `==` / `!=` / 比較系の演算子オーバーロード
- using statement / using declaration / IDisposable 自動 dispose

### ジェネリクス方針

**型消去方式**を採用する。

- コンパイル時：Roslynのセマンティック解析でジェネリクスの型チェックを完全に実施
- 出力時：Lua 5.5 tableとして型パラメータを消去
- ランタイム：Lua 5.5 VM上、型情報なし

対応範囲は組み込み型のみ：`List<T>`, `Dictionary<TKey, TValue>` および TinySystem の提供する型。ユーザー定義ジェネリクスは対象外。

### LINQメソッドチェーン

クエリ構文は切るが、メソッドチェーン形式（`.Where().Select().ToList()`）はサポートする。

**実装要件**: extension methods + ラムダ + 限定的ジェネリクスメソッド（LINQ用スコープ限定）

**重要な差異**: 遅延評価はしない。C#のLINQは`IEnumerable<T>`による遅延評価だが、TinyC#ではLua 5.5 table を即時生成・返却する。チェーンが長い場合、パフォーマンス特性がC#と異なる。

対応メソッド：

- Where, Select, Any, All
- First, FirstOrDefault, Last, LastOrDefault
- Count, ToList, ToDictionary
- OrderBy, OrderByDescending, Take, Skip, Min, Max

## TinySystem 標準ライブラリ

独自の最小標準ライブラリ。dotnet側では`System`名前空間の型に委譲、Lua 5.5側ではtable操作・math libraryのラッパーとして実装する。

dotnet側では `TinySystem.Random`, `TinySystem.Math`, `TinySystem.String`, `TinySystem.List`, `TinySystem.Dict` の facade を提供し、Lua側 runtime と同期する。
`System.Action` / `System.Func` は標準 BCL 型をそのまま使う。

### コレクション

|型                         |Lua表現                                       |
|--------------------------|--------------------------------------------|
|`List<T>`                 |Lua 5.5 sequence table `{[1]=v, [2]=v, ...}`|
|`Dictionary<TKey, TValue>`|Lua 5.5 hash table `{[k]=v, ...}`           |

Lua table の制約により、`List<T>` の null 要素と `Dictionary<TKey, TValue>` の null 値は未対応として診断する。

### 数学

`Math` — Min, Max, Clamp, Abs, Floor, Ceil, Sin, Cos, Atan2, Sqrt, Pow, PI

### 乱数

`Random` — Next, NextFloat, Range

### 文字列

`String` 基本操作 — Contains, Split, Replace, StartsWith, EndsWith, Trim, Substring, IndexOf, Join

### 関数型

`Action`, `Action<T>`, `Func<T, TResult>` — コールバック・LINQラムダに必要

### LINQメソッド

`List<T>` 上の extension methods として提供（Where, Select, Any, All, First, FirstOrDefault, Last, LastOrDefault, Count, ToList, ToDictionary, OrderBy, OrderByDescending, Take, Skip, Min, Max, Sum）

### 規模感

型定義 20〜30個、メソッド合計 100〜150個。1人で実装可能な範囲。

## プラットフォーム特性

|プラットフォーム    |備考                          |
|------------|----------------------------|
|PC          |JIT可。dotnetのほうが速いがLuaで十分    |
|iOS         |AOT必須。Lua 5.5 VMは制約なし       |
|WebGL / WASM|.NETランタイムは巨大。Lua 5.5→WASMが軽量|
|コンソール機     |.NET公式サポートなしの機種あり。Lua 5.5は組み込み容易 |

Lua 5.5バックエンドの存在意義はパフォーマンスではなく、組み込みの軽さ・ホットリロード・プラットフォーム制約の回避にある。

## リスクと対策

### ユーザー期待値の管理

C#構文を使うため「普通のC#が書ける」と誤解されるリスクがある。TinyC#は **C#構文のDSL** であり、C#ではないという認識を明確に伝える必要がある。

- 未対応の構文・型を使用した場合のコンパイルエラーメッセージを明瞭にする
- Rider など標準的な C# 開発ツールキット上で動く Roslyn Analyzer を用意し、書いている最中に tcs 準拠性を検出する
- CLI / CI 向けには同じルールを使う `tcs check` を後から追加する
- 対応範囲をドキュメント化し、初期に示す

### dotnet⇔Lua 意味論ギャップ

dotnetでテストが通りLua 5.5で落ちるケースが発生しうる（数値精度、文字列挙動等）。

- dotnetは開発補助、本番動作確認はLua 5.5上で行う運用を前提とする
- 完璧な一致を目指さない
- 既知の差異を文書化する

### サブセット境界のスコープクリープ

「あれも欲しい、これも欲しい」で機能が膨張するリスク。

- support matrix は棚卸し表であって、全対応の TODO リストではない
- Core / Useful / Out of scope を先に分け、Core だけを高優先度にする
- ボトムアップで検証：実際のサンプルと標準ライブラリ利用から必要性を確認してから追加
- 上から削るより下から積む

## 次のステップ

1. **ユースケース検証**: エンティティ定義・状態遷移・衝突処理・インベントリ管理を TinyC# サブセットで擬似的に記述し、破綻しないか確認
1. **Compact C# baseline**: モダン C# 開発体験に必要な言語機能・標準ライブラリの Core 範囲を定義
1. **tcs 準拠チェック**: まず Rider リアルタイム警告向け Roslyn Analyzer PoC を作り、その後 `tcs check` へ同じルールを展開
1. **TinySystem 型定義**: dotnet側の型チェック用 facade と Lua runtime を最小セットで同期
1. **外部エンジン連携方針**: tcs 本体を engine agnostic に保ち、必要になった連携だけ adapter/stub として検証


-----

適宜外部リポジトリをdepsディレクトリにsubmodule登録すること
作業の単位でコミットし、作業ログをつけること
テストファーストでTDD開発すること

参考リポジトリ(参照のみ上書き禁止)
../lub3d 参考実装。直接対応先とは限らない
../lubs linter 開発ワークフローが十分整備されてる
