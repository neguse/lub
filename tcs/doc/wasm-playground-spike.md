# client-side compile spike (browser-wasm)

lub playground と同様の「ブラウザ内だけで C# → Lua をコンパイルする playground」が
tcs で成立するかの feasibility spike。2026-07-12 実施、実機検証済み。

## 結論: GO

.NET browser-wasm ランタイム (Mono interpreter) 上で Roslyn + tcs Transpiler が動き、
生成 Lua が実 Lua 5.5 で期待どおり実行できることを確認した。

- 検証系: `dotnet new wasmconsole` (Node 上の browser-wasm、ブラウザと同一ランタイム)
- 入力: class + プロパティ + LINQ チェーン + Dictionary + 三項演算子 + top-level statements
- 出力 Lua を `deps/lua/lua` で実行し期待出力一致
- tcs 全テスト 333 件通過を変更込みで確認

## 計測値 (Release, Node v26)

| 項目 | 値 | 参考: lub Haxe-wasm |
|------|----|----|
| 配信サイズ gzip | 約 6.8MB (Roslyn 1.8 / dotnet.native 1.1 / CoreLib 0.6 ほか) | 約 4MB |
| cold compile | 約 1.5s | 185ms |
| warm compile | 約 173ms | — |

ICU (`icudt_*.dat`) を InvariantGlobalization で削れば縮む余地あり。

## tcs 本体に必要な変更 (spike 後に revert 済み、再適用用)

1. `Transpiler.cs`: `DefaultReferences` を注入可能にする。browser では
   `Assembly.Location` が空でファイル読み不可のため、参照アセンブリを
   byte image (`MetadataReference.CreateFromStream`) で渡す口が要る。

```csharp
private static MetadataReference[]? _references;

// runtime pack をファイルとして読めない host (browser-wasm 等) は
// 参照アセンブリを byte image からここへ注入する
public static MetadataReference[] References
{
    get => _references ??= GetDefaultReferences();
    set => _references = value;
}
// CSharpCompilation.Create の references 引数を References に差し替え
```

2. `Transpiler.cs`: `CSharpCompilationOptions` に `concurrentBuild: false`。
   Roslyn の並列解析 (ClsComplianceChecker の worker 待ち) がシングルスレッド
   WASM で `Monitor.Wait` 不可により実行時クラッシュする。tcs の入力サイズでは
   性能影響なし。

## spike の構成 (再現手順)

- `dotnet workload install wasm-tools` が必要 (この Linux 機にはインストール済み)
- `dotnet new wasmconsole` → csproj に以下を足す:
  - Transpiler は Exe プロジェクトのため ProjectReference 不可 (NETSDK1150)。
    `<Compile Include="$(TcsRoot)/Transpiler/*.cs" Exclude="**/Program.cs" />` +
    `Shared/TinyCsComplianceFacts.cs` + TinySystem を ProjectReference で取り込む
  - `Microsoft.CodeAnalysis.CSharp` 4.14.0 を PackageReference
  - 参照アセンブリ 5 本を EmbeddedResource で同梱:
    ref pack (`/usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/<ver>/ref/net10.0/`) の
    System.Runtime / System.Collections / System.Linq / System.Console + ビルド済み TinySystem.dll
- Program.cs で `GetManifestResourceStream` → `Transpiler.References` へ注入し
  `TranspileWithDiagnostics` を呼ぶだけ

## playground 化する場合の残作業

- 参照アセンブリを EmbeddedResource でなく静的アセット fetch にし、compile を
  Web Worker 化 + `JSExport` で `compile(source) → lua` API を出す
- 実行側は lub web player の `haxe-compiler.ts` を tcs 版 worker に差し替えるのが最短。
  出力契約 (prelude 前置 + `return <Main>`) は `--entry` / `--prelude` で既に一致
- Lua 側を自前で持つなら Lua 5.5 の Emscripten ビルド (CMake に分岐あり)
- Transpiler の CLI と core の分離 (Library 化) をするとソース直取り込みが不要になる
