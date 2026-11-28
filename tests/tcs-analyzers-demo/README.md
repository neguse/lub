# tcs-analyzers-demo

analyzer (`src/tcs-analyzers`) の診断を確かめる project。
`Program.cs` には TinyC# に合わない code をわざと入れている。

## 期待する診断

`dotnet build tests/tcs-analyzers-demo --no-incremental` で次が出る。

- `TCS1001` x5: `StructMember`、`LocalFunctionStatement`、`TryStatement`、`ThrowStatement`、`ListPattern`
- `TCS1002` x1: `System.IO.File.ReadAllText`
- `TCS1003` x1: `List<T>` への null の保存

`TCS1001`、`TCS1002`、`TCS1003` の既定は warning で、この project は warning で build を止めないので、build は warning だけで終わる。
テストのランナー (`tests/lub-test` の `tcs.analyzer-demo`) がこの数を確かめる。

## JetBrains InspectCode

Rider と同じ engine を画面なしで動かす JetBrains InspectCode で、SARIF に同じ数の診断が出ることを確かめる。

```bash
tests/tcs-analyzers-demo/verify-inspectcode.sh
```

script は、無ければ `${XDG_CACHE_HOME:-$HOME/.cache}/tcs/jetbrains-tools` に JetBrains ReSharper GlobalTools を入れ、結果を `${XDG_CACHE_HOME:-$HOME/.cache}/tcs/inspectcode-analyzer-demo/` に出す。
どちらも自分だけが書ける directory (0700) で、他人の持ち物なら動かさずに止まる。
`TCS_JETBRAINS_TOOL_DIR` と `TCS_INSPECTCODE_OUTPUT_DIR` で場所を変えられる。
