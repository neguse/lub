# C# (TinyC#) で書く

lub のゲームコードは C# で書く。使うのは
[tcs (TinyC#)](https://github.com/neguse/tcs) — C# のサブセットを Lua に
transpile するコンパイラで、playground ではブラウザ内 (Roslyn の WASM 版)、
native では lub が `third_party/tcs` を dotnet で動かす。

「フル C# が動く」わけではない。TinyC# は C# 構文の DSL であり、
モダンな C# の書き味 (record / pattern / lambda / LINQ メソッドチェーン /
nullable 型チェック) を保ちつつ、Lua 5.5 に素直に落ちる小さな核だけを
サポートする。

## 使える主なもの

- `class` / `record class` / `enum` / `interface`、メソッド・プロパティ・フィールド
- `if` / `for` / `foreach` / `while` / `switch` 式(パターンマッチング)
- ラムダ、コレクション初期化子、string interpolation
- `List<T>` / `Dictionary<K,V>`(Lua table に対応)
- LINQ メソッドチェーン(`Where` / `Select` / `First` / `OrderBy` など。
  遅延評価はせず即時実行)
- 演算子オーバーロード(二項 `+ - * / %` と単項 `-`。`lub.Math` の
  Vec/Mat がこれで書かれている)、整数ビット演算子
- `Math` / `Random` / `String` の実用サブセット

## 使えない主なもの

- `async` / `await` / `Task`、`try` / `throw`、reflection、`dynamic`
- ユーザー定義ジェネリクス
- `struct` / `record struct`(`class` / `record class` で代替)
- LINQ クエリ構文(`from x in y select`)

## 診断に出ない注意点

- lub API(`cs-lib/lub_stub.cs` のような型チェック専用 stub)の呼び出しでは
  デフォルト引数値が展開されない(省略した引数は Lua の nil になる)。
  stub 側は `double? x = null` + `x ?? 既定値` で書く。自分で書いた
  TinyC# メソッドの既定引数は通常どおり効く
- static 初期化子から cs-lib のクラスを参照しない。生成 Lua はサンプル
  → cs-lib の順で定義されるため、ロード時に nil 呼び出しになる。
  `OnInit` / `OnFrame` で遅延生成する(リテラルだけの static は可)

境界を踏むとコンパイル時に `TCS1001`(未対応構文)/ `TCS1002`(未対応 API)/
`TCS1003`(Lua table に置けない null 保存)の診断が出る。playground では
ログパネルに表示される。エラーではなく警告として出るものも、Lua 出力の
正しさに関わるので放置しない。

## lub API の呼び方

runtime API は root class `Lub` の下の nested static class(`Gfx` / `Input` /
`Io` / `Phys2d` / ...)にある。`using static Lub;` を置くと `Gfx.BeginPass(...)`
と書ける。名前は通常の C# 命名(PascalCase、enum は `Gfx.PixelFormat.Rgba8`)
で、Lua 側の snake_case(`lub.gfx.begin_pass`、`lub.gfx.RGBA8`)には tcs が
規則で写す。entry callback も `OnInit` / `OnEvent` / `OnFrame` / `OnQuit`
(Lua では `on_init` 等):

```csharp
using static Lub;

public static class Main
{
    public static void OnInit()
    {
        Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    public static void OnFrame(double dt)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.EndPass();
    }
}
```

型定義は `cs-lib/lub_stub.cs`(参照専用 stub)にある。IDE で書くときは
これを参照に加えると補完と型チェックが効く。Lua 側の multi-return
(`Io.LoadText` など) は `out` 引数で受ける。環境変数は
`Environment.GetEnvironmentVariable`、数値の parse は `int.Parse` /
`double.Parse`、文字列の codepoint 走査は `s.EnumerateRunes()` と、実 .NET
でも通る書き方をする(Lua 標準ライブラリを直接呼ぶ stub は無い)。

サンプルは `samples/<name>/<Entry>.cs` + `<Entry>.csproj` (例:
`samples/09_breakout/Breakout09.cs` + `Breakout09.csproj`)。native での実行:

```
lub samples/09_breakout/Breakout09.csproj         # transpile + watch + hot reload
scripts/run-cs-sample.sh 09_breakout --check      # 診断のみ
scripts/run-cs-sample.sh 09_breakout --build      # transpile のみ
```

playground の C# は増分コンパイラで hot reload する(編集停止から 0.5 s 未満)。
何が生きたまま反映されて何が作り直しになるかは「ライフサイクルと hot reload」
の章を参照。

csproj は lub にとっては entry 指定 (basename = entry class、入力 = 同
ディレクトリの全 `*.cs`) でしかないが、dotnet 側 (Rider / VS Code) では
本物のプロジェクトとして機能する: stub と TinySystem を参照し、TinyC#
サブセット逸脱は Roslyn Analyzer が IDE 上で TCS 診断として出す。

## .NET で実行する

同じソースを実 .NET で動かせる (.NET Hot Reload や本物のデバッガを使いたい
とき)。`dotnet/Lub` が lub の C API の facade と host で、共有 library
(`build-release-linux/liblub.so`、CMake の `lub_shared`) を P/Invoke する。
雛形は `templates/game/` (`dotnet run` で .NET 実行、`lub Game.csproj` で
tcs→Lua。入口は `Lub.Run(typeof(Game), args)`)。サンプルは runner で回す:

```
dotnet run --project dotnet/SampleRunner -p:Sample=09_breakout -- --capture out.png
```

共有 library は出力の隣か環境変数 `LUB_NATIVE_LIB` (full path) で見つける。
引数は player と同じ (`--backend` / `--fixed-dt` / `--capture` /
`--capture-frame` / `--digest`)。`Bytes` (view) は `AsSpan()` で読め、frame を
跨いで持つと例外になる。数値は .NET の型どおり (double) なので、tcs→Lua
(float) とは描画結果の低位 bit が違いうる。
