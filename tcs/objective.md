# TinyC# — ホットリロード可能なC#サブセット言語

## 概要

C#の極小サブセットをフロントエンド言語とし、tcs VM の bytecode (開発とホットリロード) と C のソース (release) を出力するコンパイラ。ゲームスクリプティングにおけるホットリロード、軽量組み込み、クロスプラットフォーム対応を実現しつつ、C#のエディタ体験と型安全性を得る。

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

- **tcs VM の強み**：状態を保ったホットリロード (型の形が変わっても object を移す)、C11 で書いた小さな VM で組み込みやすい、JIT 不可環境での動作
- **C (tcs2c) の強み**：ネイティブの速度、C の toolchain がある環境ならどこでも build できる
- **C#の強み**：Roslynによる成熟したエディタ支援（補完、リファクタリング、型チェック）、広いユーザーベース
- **C#の弱み**：ランタイムが重い、ホットリロードが限定的、一部プラットフォームでJIT不可

TinyC#は C# の書き味と、tcs VM と C の実行系を組み合わせる。

## アーキテクチャ

### 3 つの実行経路

同じ C# ソースを 3 つの経路で実行し、結果を一致させる。

|       |tcs VM (`tcs vm`)         |C (`tcs c`)   |.NET (TinySystem) |
|-------|------------------|------------|------------------|
|**用途** |開発中の実行、ホットリロード、web|ネイティブの配布   |配布、デバッガ、単体テスト、検証の正解     |
|**実行** |tcs VM (`tcsvm/`) が `.tcsb` を実行        |C コンパイラの出力  |.NET ランタイム        |

一致は differential テストで検査する。一致させられない機能は、仕様を定めるかサブセットから外す (選定基準の 1)。

### トランスパイルフロー

```
C#ソースコード
  → tcs の compiler（字句・構文・束縛・型検査・準拠検査）
  → IL
  → tcs VM の bytecode (.tcsb) / C ソースコード

C#ソースコード + TinySystem → .NET ビルド
```

compiler は TinyC# で書いた 1 つの実装 (`SelfHost/`) で、.NET、C、wasm のどれでも同じ実装が動く (`doc/self-hosting-design.md`)。Roslyn は compiler の外で、IDE の analyzer と補完、C# として正しいかの判定 (検証の正解) に使う。

## 言語サブセット

### 選定基準

TinyC# は C# の全部入りではなく、C# 開発者が現代的な書き味を保てる小さな核を狙う。

機能を足すか切るかは、書く人の効率とランタイムの効率で比べて、いちばん効率が良い選択をする。「他の書き方で書けるから要らない」は理由にならない。今使っている箇所の数も、その機能を禁止しているサブセットの中で数えたものなら根拠にならない。性能は C (tcs2c) と .NET の Release を第一に、tcs VM を次に見て、実測で比べる。

その選択は次の 6 つの基準を満たす。

1. **全 backend で同じ結果になる**。評価順、丸め、エラーの扱いが tcs VM / C / .NET で異なる機能は、仕様を定めて揃えるか、サブセットから外す。差異を黙って残さない (Dictionary と HashSet の列挙は挿入順と定め、3 つの経路で同じ順にする)。
2. **名前と意味を C# から変えない**。残す API は C# と同じ名前で同じ意味を持つ。C# と意味が異なるものに C# の名前を付けない。対応しない機能は TCS1001 / TCS1002 で明示的に外す。C# と意味が異なる Select (Dictionary を返すもの) は置かない。LINQ の即時評価は C# との差で、`System.Linq` ではなく `TinySystem.Linq` の名前で提供する。
3. **コストを書いた形から読めるようにする**。書いた形から読めない確保や遅延評価は持ち込まない (遅延評価の `IEnumerable<T>` は持たず、LINQ は `List<T>` を即時に返す)。struct の代入の copy、ラムダの確保、interface の呼び出しは C# を読む人が形から分かるので残す。
4. **同じことを書く方法を 1 つにする**。C# は言語仕様が増えるたびに同じ目的の手段を重ねてきたが、tcs は目的ごとに一番よい方法だけを残す (`List<T>` があれば `ArrayList` は要らない)。
5. **語彙を増やさない**。BCL に同じ意味のものがあれば BCL の名前で足し、独自の名前を作らない (見つける演算は `FindIndex` と `RemoveAll` を BCL の名前のまま足す)。BCL に無いものだけ `TinySystem` に置く (UTF-8 の byte 列の `TinySystem.Text.Utf8Bytes`)。
6. **C# コンパイラが受理するものだけを受理する**。C# コンパイラ (Roslyn) が受理しないものを tcs は受理せず、tcs 独自の緩和を入れない。C# の警告を準拠 error に格上げするなど、C# より厳しくすることは許容する (enum と int の暗黙変換を tcs だけ通すような緩和は持たない)。

基準 2 と 4 が対立する場合 (C# の名前と意味を保ったままでは一番よい方法にならない場合) は、基準 2 を優先してその演算を外す。

棚卸しは `doc/support-matrix.md` で行い、Core / Useful / Out of scope を明示して、必要なものだけ TDD で足す。C# を基盤としつつ、Go の標準ライブラリ選定に近いコンパクトさと説明しやすさを保つ。

### 残すもの

- **型**: int, float, bool, string, class, struct, enum, record, record struct, interface, タプル, 入れ子の型
- **引数**: `out` は `out var` / `out _` だけ
- **メンバー**: メソッド, プロパティ, フィールド
- **制御構文**: if/else, for, foreach, while, switch式（パターンマッチング）
- **ラムダ**: `(x) => expr` / `(x) => { stmts }`
- **Nullable annotations**: コンパイル時型チェックのみ（ランタイムコストなし）
- **コレクション初期化構文**
- **継承**: class / record の継承は対象外 (q.md Q15)。多態は interface の実装と型 switch、組み合わせは field (Go と同じ構成)
- **演算子オーバーロード**: class と struct の二項 `+ - * / %` と単項 `-` のみ（型の static 関数へ写像し、呼び出し箇所で静的に解決して直呼び）。
  `==` / `!=` は record と record struct の値等価だけを提供し、operator の宣言は対象外。

### 切るもの

- async / await / Task系全般
- LINQ クエリ構文（`from x in y select`）
- reflection, dynamic, Expression Tree
- unsafe, Span, ref struct, `ref` / `in`
- イベント / デリゲートのマルチキャストと同一性 (`+=` / `-=` / `==` / `!=` / `Equals`)
- `Equals` / `GetHashCode` の `override` (値の比較は record / record struct で書く)
- 変換演算子 (implicit / explicit) と `==` / `!=` / 比較系の演算子オーバーロード
- using statement / using declaration / IDisposable 自動 dispose

### ジェネリクス方針

**単相化**を採用する。

- コンパイル時：tcs の compiler がジェネリクスの型チェックを行い、閉じた型と method ごとに型引数を置き換えた copy を作る
- ランタイム：tcs VM と C は単相化した IL を読み、型引数を持たない (`default(T)` は閉じた型の既定値になる)

組み込み型 (`List<T>`, `Dictionary<TKey, TValue>`、TinySystem の型) に加えて、ユーザー定義の generic な型と method も対象にする (q.md Q20)。generic な型の static field は TCS1001 (`GenericStaticField`)。

### LINQメソッドチェーン

クエリ構文は切るが、メソッドチェーン形式（`.Where().Select()`）はサポートする。`System.Linq` ではなく `TinySystem.Linq`（`using TinySystem.Linq;`）の `List<T>` の拡張メソッドで、tcs のサブセットの C# で書いた `TinySystem/Linq.cs` を user のコードと一緒に tcs VM / C へ変換する。.NET の project も同じファイルをコンパイルするので、3 経路が同じ実装で動く（q.md Q18）。

**重要な差異**: 遅延評価はしない。C#のLINQは`IEnumerable<T>`による遅延評価だが、TinyC#では新しい `List<T>` を即時に返す。チェーンが長い場合、パフォーマンス特性がC#と異なる。

対応メソッド：

- Where, Select, Any, All, Count(predicate)
- FirstOrDefault
- Sum, Min, Max（int / float、Min / Max は string も）
- OrderBy, OrderByDescending（key は int / float / string）

## TinySystem 標準ライブラリ

独自の最小標準ライブラリ。dotnet側では`System`名前空間の型に委譲し、tcs VM と C では VM (`tcsvm/`) と C の runtime (`runtime/`) が同じ意味を実装する。

dotnet側では `TinySystem.Random`, `TinySystem.Math`, `TinySystem.String`, `TinySystem.List`, `TinySystem.Dict` の facade を提供し、tcs VM と C の runtime と同じ意味に保つ。`TinySystem.Linq` は facade ではなく、サブセットの C# で書いた実装を source のまま配る。
`System.Action` / `System.Func` は標準 BCL 型をそのまま使う。

### コレクション

|型                         |tcs VM                                       |C                                       |
|--------------------------|--------------------------------------------|--------------------------------------------|
|`List<T>`                 |要素を inline に並べる伸びる buffer|要素型ごとの連続 buffer|
|`Queue<T>`                |`List<T>` と同じ object (先頭から取り出す)|`List<T>` と先頭の添字|
|`HashSet<T>`              |`Dictionary` と同じ object (key だけを持つ)|.NET の HashSet と同じ slot と bucket|
|`Dictionary<TKey, TValue>`|足した順の entry の列と、entry を引く open addressing の表|chain の hash の表と、足した順の列|

列挙の順はどれも挿入順 (support-matrix §18)。`List<T>` の要素は null を持てる。配列の null 要素と `Dictionary<TKey, TValue>` の null 値は TCS1003 で診断する。

### 数学

`Math` — Min, Max, Clamp, Abs, Floor, Ceil, Sin, Cos, Atan2, Sqrt, Pow, PI

### 乱数

`Random` — Next, NextFloat, Range

### 文字列

文字列は UTF-8 と定める。.NET の `string` だけが UTF-16 で位置を扱う API の結果が backend で揃わないので、`Length`、`Substring`、`IndexOf`、添字、`foreach (char c in s)` は外す。

`String` 基本操作 — Contains, Split, Replace, StartsWith, EndsWith, Trim, Join

`TinySystem.Text` — `s.RuneCount()`, `s.RuneSubstring(start, count)`, `Utf8Bytes`

### 関数型

`Action`, `Action<T>`, `Func<T, TResult>` — コールバック・LINQラムダに必要

### LINQメソッド

`TinySystem.Linq` の `List<T>` 上の extension methods として提供（Where, Select, Any, All, Count, FirstOrDefault, OrderBy, OrderByDescending, Min, Max, MinBy, MaxBy, Sum。Dictionary の Keys / Values も受け手）。`List<T>` の写しは copy constructor `new List<T>(source)`

### 規模感

型定義 20〜30個、メソッド合計 100〜150個。1人で実装可能な範囲。

## プラットフォーム特性

|プラットフォーム    |備考                          |
|------------|----------------------------|
|PC          |JIT可。開発は tcs VM、配布は C    |
|iOS         |AOT必須。tcs VM は JIT を使わない interpreter、C はネイティブのコード       |
|WebGL / WASM|.NETランタイムは巨大。tcs が出す C は emscripten で wasm にできる (tcs 自身の wasm 版 `SelfHost/Web` もこの形)|
|コンソール機     |.NET公式サポートなしの機種あり。tcs VM と生成する C は C のソースで、C の toolchain があれば組み込める |

tcs VM の存在意義はパフォーマンスではなく、状態を保ったホットリロードと組み込みの軽さにある。性能が要る配布は C で行う。

## リスクと対策

### ユーザー期待値の管理

C#構文を使うため「普通のC#が書ける」と誤解されるリスクがある。TinyC#は **C#構文のDSL** であり、C#ではないという認識を明確に伝える必要がある。

- 未対応の構文・型を使用した場合のコンパイルエラーメッセージを明瞭にする
- Rider など標準的な C# 開発ツールキット上で動く Roslyn Analyzer を用意し、書いている最中に tcs 準拠性を検出する
- CLI / CI 向けには同じルールを使う `tcs check` を後から追加する
- 対応範囲をドキュメント化し、初期に示す

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
1. **TinySystem 型定義**: dotnet側の型チェック用 facade と tcs VM / C の runtime を最小セットで同期
1. **外部エンジン連携方針**: tcs 本体を engine agnostic に保ち、必要になった連携だけ adapter/stub として検証


-----

適宜外部リポジトリをdepsディレクトリにsubmodule登録すること
作業の単位でコミットし、作業ログをつけること
テストファーストでTDD開発すること

参考リポジトリ(参照のみ上書き禁止)
../lub3d 参考実装。直接対応先とは限らない
../lubs linter 開発ワークフローが十分整備されてる
