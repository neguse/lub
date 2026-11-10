# TinyC# (tcs) — C#サブセットのコンパイラ (tcs VM と C)

## プロジェクト概要

C#の小さなサブセットをフロントエンド言語とし、tcs VM の bytecode (開発と hot reload) と C のソース (release) を出力するコンパイラ。
フル C# / フル BCL ではなく、モダンな .NET / C# 開発体験を満たす最小ラインを選び、Go のようなコンパクトさを保つ。
Rider など標準的な C# ツールキット上で tcs 準拠コードかをリアルタイム確認できる Roslyn Analyzer も開発体験の中核に置く。
詳細は `objective.md` を参照。

tcs は lub の repo の `tcs/` にある。作業の流れ (branch、PR、CI、format、記録の置き場所) は repo の root の `CLAUDE.md` に従い、このファイルは tcs に固有のことだけを書く。
このファイルの path とコマンドは `tcs/` を基準にする。

## ディレクトリ構成

```
tcs/
├── CLAUDE.md              # このファイル
├── objective.md           # プロジェクト目標・設計文書
├── q.md                   # 未解決の質問
├── doc/
│   └── done.md            # 2026-11-10 までの完了の記録 (追記しない)
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

- .NET 10 SDK / C# 14 — SDK 版は `global.json` で固定し、Dependabot (`dotnet-sdk`) が bump PR を出す。
  マシンへの導入は `dotnet-install.sh --jsonfile global.json` の per-user install (distro package と混ぜると workload manifest が更新で消えて壊れる)
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

1. テストを先に書く (TDD)
2. 実装し、`dotnet test` で全テスト通過を確認
3. テスト、実装、ドキュメントの更新を 1 つの PR に入れる

仕様の正本は support-matrix / README / design doc。`doc/done.md` は 2026-11-10 までの記録で、追記しない。

## 進め方

- 実装は Claude (Fable) が直接行う。Codex は反証限定の second opinion レビューに限定し、実装委任はユーザーが明示的に求めた場合のみ

## コード規約

### 命名

- 「spike」という名称は使わない（識別子・ファイル名・新規ドキュメントとも）。旧 spike 由来の実体はすべて `perf` 系の名前を使う
- tcs / tcs2c はプロジェクト全体のリブランディング（時期・名称未定）を控えた暫定名。新規の命名は tcs 系に揃える

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

## 既知の制約・注意事項

- 数値は i32 と f32 (long / double は TCS1001)。f32 の演算は積和を融合しない build flag を前提にする
- 文字列: UTF-8 と定める。.NET の string だけが UTF-16 なので、位置を扱う API (`Length`、`Substring`、`IndexOf`、添字、`foreach (char c in s)`) は TCS1002 で止め、`TinySystem.Text` を使う
- struct / record struct: 値セマンティクス対応済み (T219b)。override は ToString だけ
- generic: tcs VM と C の backend は閉じた型ごとに単相化した IL を読む
