# analyzer-demo

Rider / Roslyn Analyzer PoC の実機確認用 project。
`Program.cs` には TinyC# 非準拠コードを意図的に入れている。

## 期待する診断

`dotnet build samples/analyzer-demo/analyzer-demo.csproj --no-incremental` で以下が出ること。

- `TCS1001` x5: `StructDeclaration`, `LocalFunctionStatement`, `TryStatement`, `ThrowStatement`, `ListPattern`
- `TCS1002` x1: `System.IO.File.ReadAllText`
- `TCS1003` x1: `List<T>` への null 保存

root `.editorconfig` では `TCS1001` / `TCS1002` / `TCS1003` を warning にしているため、build は warning だけで完了する。
`run-tests` では analyzer nupkg を pack し、一時 project から `PackageReference` で参照して同じ診断が出ることと、`.editorconfig` で `TCS1001` / `TCS1002` / `TCS1003` を error にした build が失敗することも検証する。

## PackageReference 確認

通常の利用形に近い package consumer 経路は `run-tests` で検証する。
一時 project は local nupkg source から `TinyCs.Analyzers` 0.1.0 を restore し、`PackageReference` だけで `TCS1001` x5 / `TCS1002` x1 / `TCS1003` x1 を出す。

```xml
<PackageReference Include="TinyCs.Analyzers"
                  Version="0.1.0"
                  PrivateAssets="all" />
```

## JetBrains InspectCode

Rider 本体ではなく JetBrains InspectCode 2026.1.3 の headless 実行では、ProjectReference と local nupkg `PackageReference` consumer の両方で SARIF に `TCS1001` x5 / `TCS1002` x1 / `TCS1003` x1 が出ることを確認済み。
また、PackageReference consumer の `.editorconfig` で `TCS1001` / `TCS1002` / `TCS1003` を error にした場合、InspectCode が同じ件数の error を返すことも確認する。
stdout には bundled analyzer 由来の noisy log が出る場合があるため、確認には SARIF を使う。
severity override 実行で InspectCode が incomplete な stdout を返した場合は、script が一度だけ再実行する。

```bash
samples/analyzer-demo/verify-inspectcode.sh
```

Windows PowerShell:

```powershell
.\samples\analyzer-demo\verify-inspectcode.ps1
```

script は必要なら `/tmp/tcs-jetbrains-tools` に JetBrains ReSharper GlobalTools 2026.1.3 を install し、結果を `/tmp/tcs-inspectcode-analyzer-demo/` に出す。
PackageReference consumer は script 内で local nupkg を pack して同じ出力ディレクトリ配下に作る。
PowerShell 版は `%TEMP%\tcs-jetbrains-tools` と `%TEMP%\tcs-inspectcode-analyzer-demo\` を使う。

## Rider pre-check

Rider を開く前に、テンプレートへ転記する pre-check 結果をまとめて作れる。

```bash
samples/analyzer-demo/verify-rider-prechecks.sh
```

Windows PowerShell:

```powershell
.\samples\analyzer-demo\verify-rider-prechecks.ps1
```

script は `TCS_RIDER_COMMAND` / Rider command / display 環境情報と、この shell から Rider UI を起動できる状態かを記録し、`bash run-tests.sh`、`samples/analyzer-demo/verify-inspectcode.sh`、`dotnet build samples/analyzer-demo/analyzer-demo.csproj --no-incremental` を実行する。
結果とログパスは `/tmp/tcs-rider-verification-precheck/summary.md` に出す。
Rider の自動検出に失敗する場合は `TCS_RIDER_COMMAND=/path/to/rider.sh` を指定する。
PowerShell 版は `.\run-tests.ps1`、`.\samples\analyzer-demo\verify-inspectcode.ps1`、`dotnet build` を実行し、結果を `%TEMP%\tcs-rider-verification-precheck\summary.md` に出す。
Rider を自動検出できない場合は `$env:TCS_RIDER_COMMAND = "C:\path\to\rider64.exe"` を指定する。

## Rider 起動 helper

pre-check を通してから demo project を Rider で開く helper。

```bash
samples/analyzer-demo/open-rider-demo.sh
```

Windows PowerShell:

```powershell
.\samples\analyzer-demo\open-rider-demo.ps1
```

pre-check 済みなら `--no-precheck` を付ける。
PowerShell 版では pre-check 済みなら `-NoPrecheck` を付ける。
`samples/analyzer-demo/verify-rider-scripts.sh` は Rider 本体を使わず、fake command でこの helper の検出・エラー経路を検証する。`run-tests` からも実行される。

## Rider 確認手順

1. 必要なら `samples/analyzer-demo/open-rider-demo.sh` / `samples/analyzer-demo/verify-rider-prechecks.sh`、Windows では `.\samples\analyzer-demo\open-rider-demo.ps1` / `.\samples\analyzer-demo\verify-rider-prechecks.ps1` を実行する
1. repository root または `samples/analyzer-demo/analyzer-demo.csproj` を Rider で開く
1. Restore が終わった後、`samples/analyzer-demo/Program.cs` を開く
1. `struct`, local function, `try`, `throw`, `values is [1, 2]`, `System.IO.File.ReadAllText`, `List<string?> { null }` に inspection / squiggle が出ることを確認する
1. Build tool window で `TCS1001` x5 / `TCS1002` x1 / `TCS1003` x1 が表示されることを確認する
1. root `.editorconfig` の `dotnet_diagnostic.TCS1001.severity` / `dotnet_diagnostic.TCS1002.severity` / `dotnet_diagnostic.TCS1003.severity` を一時的に `error` へ変え、Rider 表示が追従することを確認する
1. 確認後、`.editorconfig` は repository の既定値へ戻す
1. 結果を `q.md` の Q12 に go / no-go として記録する

記録時は [`RIDER_VERIFICATION_TEMPLATE.md`](RIDER_VERIFICATION_TEMPLATE.md) を使う。

## go / no-go の判定

- go: Rider 上で realtime inspection と `.editorconfig` severity override が期待どおり反映される
- no-go: Rider 上で analyzer が読み込まれない、または severity override が安定して反映されない

go の場合は analyzer package / `tcs check` / CI を正式導線にする product task へ分解する。
no-go の場合は Rider plugin、external tool、CLI watcher 連携などの代替案を記録する。
