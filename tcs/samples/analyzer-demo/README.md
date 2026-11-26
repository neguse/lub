# analyzer-demo

Roslyn Analyzer の診断を確かめる project。
`Program.cs` には TinyC# 非準拠コードを意図的に入れている。

## 期待する診断

`dotnet build samples/analyzer-demo/analyzer-demo.csproj --no-incremental` で以下が出ること。

- `TCS1001` x5: `StructMember`, `LocalFunctionStatement`, `TryStatement`, `ThrowStatement`, `ListPattern`
- `TCS1002` x1: `System.IO.File.ReadAllText`
- `TCS1003` x1: `List<T>` への null 保存

`TCS1001` / `TCS1002` / `TCS1003` の既定は warning で、この project は warning で build を止めないため、build は warning だけで完了する。

## JetBrains InspectCode

Rider 本体ではなく JetBrains InspectCode 2026.1.3 の headless 実行では、ProjectReference と local nupkg `PackageReference` consumer の両方で SARIF に `TCS1001` x5 / `TCS1002` x1 / `TCS1003` x1 が出ることを確認済み。
また、PackageReference consumer の `.editorconfig` で `TCS1001` / `TCS1002` / `TCS1003` を error にした場合、InspectCode が同じ件数の error を返すことも確認する。
stdout には bundled analyzer 由来の noisy log が出る場合があるため、確認には SARIF を使う。
severity override 実行で InspectCode が incomplete な stdout を返した場合は、script が一度だけ再実行する。

```bash
samples/analyzer-demo/verify-inspectcode.sh
```

script は必要なら `${XDG_CACHE_HOME:-$HOME/.cache}/tcs/jetbrains-tools` に JetBrains ReSharper GlobalTools 2026.1.3 を install し、結果を `${XDG_CACHE_HOME:-$HOME/.cache}/tcs/inspectcode-analyzer-demo/` に出す。
どちらもユーザー専用 (0700) で、他人所有のディレクトリだった場合は実行せずに落ちる。`TCS_JETBRAINS_TOOL_DIR` / `TCS_INSPECTCODE_OUTPUT_DIR` で変更できる。
PackageReference consumer は script 内で local nupkg を pack して同じ出力ディレクトリ配下に作る。

## Rider 確認手順

1. repository root または `samples/analyzer-demo/analyzer-demo.csproj` を Rider で開く
1. Restore が終わった後、`samples/analyzer-demo/Program.cs` を開く
1. `struct`, local function, `try`, `throw`, `values is [1, 2]`, `System.IO.File.ReadAllText`, `List<string?> { null }` に inspection / squiggle が出ることを確認する
1. Build tool window で `TCS1001` x5 / `TCS1002` x1 / `TCS1003` x1 が表示されることを確認する
1. `samples/analyzer-demo/.editorconfig` を一時的に作って `dotnet_diagnostic.TCS1001.severity` / `dotnet_diagnostic.TCS1002.severity` / `dotnet_diagnostic.TCS1003.severity` を `error` にし、Rider 表示が追従することを確認する
1. 確認後、作った `.editorconfig` を消す
