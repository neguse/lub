# analyzer-demo

Rider / Roslyn Analyzer PoC の実機確認用 project。
`Program.cs` には TinyC# 非準拠コードを意図的に入れている。

## 期待する診断

`dotnet build samples/analyzer-demo/analyzer-demo.csproj --no-incremental` で以下が出ること。

- `TCS1001` x4: `StructDeclaration`, `LocalFunctionStatement`, `TryStatement`, `ThrowStatement`
- `TCS1002` x1: `System.IO.File.ReadAllText`

root `.editorconfig` では `TCS1001` / `TCS1002` / `TCS1003` を warning にしているため、build は warning だけで完了する。
`run-tests` では `TCS1002` を error にした一時 project も build し、`.editorconfig` severity override が build に反映されることを検証する。

## JetBrains InspectCode

Rider 本体ではなく JetBrains InspectCode 2026.1.3 の headless 実行では、SARIF に `TCS1001` x4 / `TCS1002` x1 が出ることを確認済み。
stdout には bundled analyzer 由来の noisy log が出る場合があるため、確認には SARIF を使う。

```bash
dotnet tool install JetBrains.ReSharper.GlobalTools --tool-path /tmp/tcs-jetbrains-tools --version 2026.1.3
/tmp/tcs-jetbrains-tools/jb inspectcode samples/analyzer-demo/analyzer-demo.csproj \
  --format=Sarif --output=/tmp/tcs-inspect.sarif --no-updates --verbosity=ERROR
rg -c '"ruleId": "TCS1001"' /tmp/tcs-inspect.sarif # 4
rg -c '"ruleId": "TCS1002"' /tmp/tcs-inspect.sarif # 1
```

## Rider 確認手順

1. repository root または `samples/analyzer-demo/analyzer-demo.csproj` を Rider で開く
2. Restore が終わった後、`samples/analyzer-demo/Program.cs` を開く
3. `struct`, local function, `try`, `throw`, `System.IO.File.ReadAllText` に inspection / squiggle が出ることを確認する
4. Build tool window で `TCS1001` x4 / `TCS1002` x1 が表示されることを確認する
5. root `.editorconfig` の `dotnet_diagnostic.TCS1002.severity` を一時的に `error` へ変え、Rider 表示が追従することを確認する
6. 確認後、`.editorconfig` は repository の既定値へ戻す
7. 結果を `q.md` の Q12 に go / no-go として記録する

## go / no-go の判定

- go: Rider 上で realtime inspection と `.editorconfig` severity override が期待どおり反映される
- no-go: Rider 上で analyzer が読み込まれない、または severity override が安定して反映されない

go の場合は analyzer package / `tcs check` / CI を正式導線にする product task へ分解する。
no-go の場合は Rider plugin、external tool、CLI watcher 連携などの代替案を記録する。
