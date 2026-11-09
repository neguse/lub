# TinyC# (tcs) — C#サブセットのコンパイラ (tcs VM と C)

## プロジェクト概要

C#の小さなサブセットをフロントエンド言語とし、tcs VM の bytecode (開発と hot reload) と C のソース (release) を出力するコンパイラ。
フル C# / フル BCL ではなく、モダンな .NET / C# 開発体験を満たす最小ラインを選び、Go のようなコンパクトさを保つ。
Rider など標準的な C# ツールキット上で tcs 準拠コードかをリアルタイム確認できる Roslyn Analyzer も開発体験の中核に置く。
詳細は `objective.md` を参照。

## ディレクトリ構成

```
tcs/
├── CLAUDE.md              # このファイル
├── objective.md           # プロジェクト目標・設計文書
├── q.md                   # 未解決の質問
├── doc/
│   ├── tasks.md           # タスクリスト
│   └── done.md            # 完了ログ
├── tcs2c/                 # tcs c (C backend) を .NET で動かす入口と、C の出力のテスト用のスクリプト
├── tcs2c.Tests/           # differential (C 通常 + GC stress、tcs VM 通常 + GC stress、.NET の stdout 一致)
├── perf/                  # 性能実測ハーネス (kernel 契約 CONTRACT.md + 変種実装 + 実測記録)
├── SelfHost/              # tcs の本体 (TinyC# で書いたセルフホストの実装)
│   ├── Compiler/          # 字句、構文、束縛、準拠検査、IL、単相化
│   ├── CBackend/          # IL を C にする backend (tcs c。自前 GC 付きの C runtime は runtime/core)
│   ├── VmBackend/         # IL を tcs VM の .tcsb にする backend (tcs vm。VM 本体は tcsvm/)
│   ├── Tool/              # CLI の本体 (check、c、vm と --watch、playground の VM の session)。OS に触る所は TcsHost の stub
│   ├── Native/host.c      # C の host (tcs2c で C にした tcs の main)
│   ├── Web/               # WebAssembly の host (tcs_web.c と tcs.mjs。playground の VM の増分 session)
│   ├── native.sh          # tcs2c で C にした tcs を作り、.NET の tcs と出力を比べる
│   ├── build.sh           # bootstrap (自分を C にして build し、固定点を確かめる)
│   └── ...
├── Transpiler/            # .NET の host (TcsHost の実装、WasmCompiler とテストのための入口、tcs2c への受け渡し)
│   ├── Transpiler.csproj
│   ├── Program.cs         # CLI エントリポイント (SelfHost/Tool の Cli を動かす)
│   └── ...
├── Transpiler.Tests/      # xUnit テスト
│   └── ...
├── TinySystem/            # 標準ライブラリ型定義 (.NET)
│   ├── TinySystem.csproj
│   └── ...
├── TinyCs.Analyzers/      # Roslyn Analyzer PoC
├── TinyCs.Analyzers.Tests/
├── Shared/                # 診断の id (TinyCsDiagnosticIds。規則は compiler にある)
├── samples/               # サンプル TinyC# スクリプト
├── runtime/               # C の runtime (core/、tcs c が埋め込む) と、VM と C が共有する C 実装 (math/、fmt/、utf8/、model/、sort/)
├── deps/                  # 外部依存 (git submodule)
│   └── csharpstandard/    # C# の仕様 (spec conformance の例)
├── tcs.slnx               # .NET ソリューション
└── run-tests.sh           # テスト実行スクリプト (CI の test job が走らせる)
```

## ビルド・テスト

### 前提

- .NET 10 SDK / C# 14 — SDK 版は `global.json` で固定し、Dependabot (`dotnet-sdk`) が
  bump PR を出す。マシンへの導入は `dotnet-install.sh --jsonfile global.json` の
  per-user install (distro package と混ぜると workload manifest が更新で消えて壊れる)
- CMake 3.12+ / C compiler (tcsvm の build と、tcs c の出力のテスト)

### コマンド

```bash
# テスト実行（基本操作）
dotnet test

# 準拠チェック（出力なし、警告/エラーで exit 1）
dotnet run --project Transpiler -- check input.cs --ref engine-stub.cs

# tcs VM の bytecode にして実行
dotnet run --project Transpiler -- vm --entry Game input.cs -o output.tcsb
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release --target tcsvm
build/tcsvm/tcsvm output.tcsb

# C にする
dotnet run --project Transpiler -- c --entry Game input.cs -o output.c

# 全テスト（シェルスクリプト）
bash run-tests.sh
```

`run-tests.sh` / `run-tests.ps1` は `dotnet test`、代表 sample の `tcs check`、analyzer-demo build の expected diagnostics 検証、analyzer nupkg の PackageReference consumer / `.editorconfig` severity override 検証を実行する。Rider helper script (`verify-rider-scripts.sh`) の検証は `run-tests.sh` のみ。
analyzer / `tcs check` の診断一致は run-tests のこの恒常ゲートで守る（個別タスクにはしない）。

## 開発ワークフロー

### 作業手順

1. `doc/tasks.md` から次のタスクを選択（残作業の正本。直近の文脈は `doc/done.md` 末尾と `git log`）
2. テストを先に書く（TDD）
3. 実装し、`dotnet test` で全テスト通過を確認
4. `doc/done.md` に完了ログを記録し、`doc/tasks.md` から削除
5. コミット（1タスク = 1コミット）

進捗スナップショット（旧 doc/current.md）は持たない。仕様の正本は
support-matrix / README / design doc、歴史は done.md と git log。

### 完了ログ形式（done.md）

```markdown
### タスク名 ✓ (YYYY-MM-DD)
- [実施内容]（変更ファイルの列挙は不要 — commit が正本）
- 検証: [実行したコマンドと結果]
- よかったこと: [成功したアプローチ、再利用可能なパターン]
- 判断: [トレードオフ、選択理由、却下した代替案]
- 残課題: [残作業]
```

### コミットルール

- 1タスク = 1コミット（`git bisect` / `git revert` 可能に）
- テスト + 実装 + ドキュメント更新をセットで
- コミットメッセージ: `feat: T{番号} {概要}`（例: `feat: T3 if/else文のトランスパイル`）

## 進め方（このリポジトリ固有）

- 実装は Claude (Fable) が直接行う。Codex は反証限定の second opinion レビューに
  限定し、実装委任はユーザーが明示的に求めた場合のみ

## コード規約

### 命名

- 「spike」という名称は使わない（識別子・ファイル名・新規ドキュメントとも）。
  旧 spike 由来の実体はすべて `perf` 系の名前を使う
- tcs / tcs2c はプロジェクト全体のリブランディング（時期・名称未定）を控えた
  暫定名。新規の命名は tcs 系に揃える

### C# (.NET 10 / C# 14)

- `<TargetFramework>net10.0</TargetFramework>` / `<LangVersion>14</LangVersion>`
- nullable enable（全プロジェクト）
- テストは xUnit + FluentAssertions スタイル
- Roslyn の `Microsoft.CodeAnalysis.CSharp` パッケージを使用

### テスト方針

- **セマンティックテスト**: C#入力 → tcs VM で実行 → 結果検証 (`TCS_DIFFERENTIAL=1` なら実 .NET の結果とも比べる)
- 出力の完全一致テストは使わない（出力形式の変更に弱い）
- `TestHelper.CompileAndRunVm(csharpSource, csharpExpr)` ヘルパーで統一 (`CompileAndRunVmMain` / `CompileAndRunVmProgram` も)
- C backend と VM と .NET の一致は `tcs2c.Tests` の `Backends.AssertParity`
- 診断だけを見るテストは `Checker.Check` (`tcs check` と同じ検査)

## compiler の方針

### compiler の実装と Roslyn

- compiler はセルフホストの実装 1 つ (`SelfHost/Compiler/`、TinyC# で書く)。字句、構文、宣言、束縛、準拠検査、IL の書き出し、generic の単相化を持つ。C backend (`SelfHost/CBackend/`) と tcs VM の backend (`SelfHost/VmBackend/`) も TinyC# で、`tcs c` と `tcs vm` で動く。.NET、C、wasm のどれでも同じ実装が動く (設計は `doc/self-hosting-design.md`)
- CLI (`tcs check`、`tcs c`、`tcs vm` と `--watch`、playground の VM の増分 session) も TinyC# (`SelfHost/Tool/`) で、OS に触る所は `TcsHost` の関数にして host が実装する。host は .NET (`Transpiler/DotnetHost.cs`)、C (`SelfHost/Native/host.c`、tcs2c で C にした tcs)、wasm (`SelfHost/Web/tcs_web.c`)。`SelfHost/build.sh` と `SelfHost/native.sh` が .NET と C の経路の出力の一致を確かめる
- Roslyn は compiler の外の道具としてだけ使う: IDE の Roslyn Analyzer (`TinyCs.Analyzers`)、playground の補完と hover (`SemanticQueries`)、検証の oracle (C# として正しいかの判定と .NET での実行の比較)
- Roslyn Analyzer は compiler (`SelfHost/Compiler`、netstandard2.0 で analyzer に入れる) を動かし、`tcs check` と同じ TCS1001〜1003 を報告する (準拠の規則は compiler の 1 か所)。`--ref` の stub は AdditionalFiles の `TinyCsRef="true"` で渡す。TCS1004 (System.Math の超越関数が .NET の実装で動く) だけは analyzer が Roslyn で見る
- `TCS1001`: 未対応構文 (`try`, `throw`, local function, list pattern, Equals の override など)
- `TCS1002`: 未対応 BCL API / core library allowlist 外 member (`Math.Cbrt`, `List.Reverse`, `Enumerable.Single` など)
- `TCS1003`: 配列と Dictionary の null 保存

## 外部エンジン連携メモ

tcs のコア目的は engine agnostic な C# サブセットのコンパイラ (tcs VM と C) であり、lub3d への直接対応は現時点の前提ではない。
lub3d 側のプロジェクト方針が変わっているため、以下の TinyCSharpGen 案は過去の検討メモとして扱う。実装タスクに戻す場合は、その時点の lub3d 側設計を読み直してから判断する。

### 旧案: lub3d API interface 生成（TinyCSharpGen）

lub3d の Generator パイプラインに乗せて C# interface を自動生成する案。

### アーキテクチャ

```
Clang AST → TypeRegistry → ModuleSpec（既存IR）
                                ├→ CBindingGen.Generate()   → .c  (既存)
                                ├→ LuaCatsGen.Generate()    → .lua (既存)
                                └→ TinyCSharpGen.Generate() → .cs  (新規)
```

### BindingType → C# 型マッピング

| BindingType       | C# 型           |
|-------------------|-----------------|
| Int               | int             |
| Int64             | long            |
| UInt32            | uint            |
| Float             | float           |
| Double            | double          |
| Bool              | bool            |
| Str               | string          |
| Void              | void            |
| Struct(_, _, lua) | class名         |
| Enum(_, lua)      | enum名          |
| Callback(p, r)    | Action/Func     |
| Vec2              | Vector2         |
| Vec4              | Vector4         |
| Ptr(inner)        | inner（透過）    |
| VoidPtr           | nint            |

### 生成物の配置

- lub3d の `Generator/TinyCSharp/TinyCSharpGen.cs` に実装
- 出力先: tcs の `TinySystem/` または `gen/` ディレクトリ
- lub3d の `Program.cs` に `TinyCSharpGen.Generate(spec)` 呼び出しを追加

### 段階的展開

1. App + Gfx + Glm で検証
2. 全モジュール展開（Audio, Imgui, Shape, Time, DebugText, ...）

## 参考リポジトリ（読み取り専用）

- `../lub` — tcs を使うゲームエンジン。ゲームは C# で書き、tcs を git submodule として取り込み、既定では tcs VM の player (C# → `.tcsb`) で動かす。readonly で、lub 側に変更が必要な場合は feature request を出す
- `../lub3d` — 参考実装。直接対応先とは限らない

### lub3d から学ぶこと

- Generator/ の .NET プロジェクト構成・IModule/ModuleSpec/BindingType
- LuaCatsGen の実装パターン（TinyCSharpGen のテンプレート）
- xUnit テストの書き方
- deps/ の submodule 管理

## 既知の制約・注意事項

- 数値は i32 と f32 (long / double は TCS1001)。f32 の演算は積和を融合しない build flag を前提にする
- 文字列: UTF-8 と定める。.NET の string だけが UTF-16 なので、位置を扱う API (`Length`、`Substring`、`IndexOf`、添字、`foreach (char c in s)`) は TCS1002 で止め、`TinySystem.Text` を使う
- struct / record struct: 値セマンティクス対応済み (T219b)。override は ToString だけ
- generic: tcs VM と C の backend は閉じた型ごとに単相化した IL を読む
