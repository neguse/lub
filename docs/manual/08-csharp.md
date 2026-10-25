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

- `class` / `record class` / `struct` / `record struct` / `enum` /
  `interface`、メソッド・プロパティ・フィールド
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
- `double` / `long`(数値は `int` / `float` の 2 型。実数リテラルは
  `1.5f` のように `f` を付ける。`Math.Sin` など double を返す BCL は
  `(float)` cast で受ける)
- LINQ クエリ構文(`from x in y select`)

## 診断に出ない注意点

- lub API(`cs-lib/lub_stub.cs` のような型チェック専用 stub)の呼び出しでは
  デフォルト引数値が展開されない(省略した引数は Lua の nil になる)。
  stub 側は `float? x = null` + `x ?? 既定値` で書く。自分で書いた
  TinyC# メソッドの既定引数は通常どおり効く

境界を踏むとコンパイル時に `TCS1001`(未対応構文)/ `TCS1002`(未対応 API)/
`TCS1003`(Lua table に置けない null 保存)の診断が出る。playground では
ログパネルに表示される。
`TCS1001` と `TCS1002` はエラーで、Lua を出さずに止まる(hot reload では直前の版が動き続ける)。
`TCS1003` は警告として出るが、Lua 出力の正しさに関わるので放置しない。

## lub API の呼び方

runtime API は namespace `Lub` の static class(`Gfx` / `Input` / `App` /
`Io` / `Phys2d` / ...)にある。`using Lub;` を置くと `Gfx.BeginPass(...)`
と書ける。lubx の型(`SpriteBatch` など)は namespace `Lubx` にあり、`using Lubx;` で使う
(`lub Game.csproj` は cs-lib を自動で足す。tcs2c で web 版を作るときは入力に自分で足す。手順は `docs/api-glue.md`)。名前は通常の C# 命名(PascalCase、enum は `Gfx.PixelFormat.Rgba8`)
で、Lua 側の snake_case(`lub.gfx.begin_pass`、`lub.gfx.RGBA8`)には tcs が
規則で写す。entry callback は `OnInit` / `OnEvent` / `OnFrame` / `OnQuit` / `OnError` / `OnExport`:

```csharp
using Lub;

public static class Main
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.EndPass();
    }
}
```

型定義は `cs-lib/lub_stub.cs`(参照専用 stub)にある。IDE で書くときは
これを参照に加えると補完と型チェックが効く。Lua 側の multi-return
(`Io.LoadText` など) は `out` 引数で受ける。環境変数は
`Environment.GetEnvironmentVariable`、数値の parse は `int.Parse` /
`float.Parse`、文字列の codepoint 走査は `s.EnumerateRunes()` と、実 .NET
でも通る書き方をする(Lua 標準ライブラリを直接呼ぶ stub は無い)。

サンプルは `samples/<name>/<Entry>.cs` + `<Entry>.csproj` (例:
`samples/09_breakout/Breakout09.cs` + `Breakout09.csproj`)。native での実行:

```
lub samples/09_breakout/Breakout09.csproj         # transpile + watch + hot reload
scripts/run-cs-sample.sh 09_breakout --check      # 診断のみ
scripts/run-cs-sample.sh 09_breakout --build      # transpile (警告があれば失敗)
```

playground の C# は増分コンパイラで hot reload する(編集停止から 0.5 s 未満)。
何が生きたまま反映されて何が作り直しになるかは「ライフサイクルと hot reload」
の章を参照。

csproj は lub にとっては entry 指定 (basename = entry class、入力 = 同
ディレクトリ以下の全 `*.cs`。`bin/` `obj/` と `.` で始まるディレクトリは
除く) でしかないが、dotnet 側 (Rider / VS Code) では本物のプロジェクトとして
機能する: stub と TinySystem を参照し、TinyC#
サブセット逸脱は Roslyn Analyzer が IDE 上で TCS 診断として出す。

別のディレクトリの C# を一緒に変換したいときは、`ProjectReference` に `Lub="Source"` を付ける。
lub は参照先の csproj のディレクトリ以下の全 `*.cs` も入力にし、hot reload の監視対象にもする。

```xml
<ItemGroup>
  <ProjectReference Include="../shared/Shared.csproj" Lub="Source" />
</ItemGroup>
```

参照先の csproj にある同じ印の参照も辿る。
lub は MSBuild を評価しないので、`Include` は参照元の csproj からの相対 path か絶対 path で書く (`$(...)` を含む参照は log を出して飛ばす)。
印の付いた参照先の csproj が無いときは、log を出して変換しない。
印の無い `ProjectReference` (TinySystem や Analyzer への参照) は読まない。

`Lub="Overlay"` を付けた参照は、ソースを入れたうえで、参照先の csproj の basename の class (namespace の無い static class) をゲームに重ねる overlay にする。
開発用の道具 (エディタ、デバッガの表示) を、ゲームのコードに手を入れずに同じ VM で動かすための口で、overlay は 1 つまで。
overlay の callback はゲームと同じ名前 (`OnInit` / `OnEvent` / `OnFrame` / `OnQuit` / `OnError`) で、次の順に呼ぶ。

| callback | 順 |
| --- | --- |
| `OnInit` | ゲームの後 |
| `OnEvent` | ゲームの前 |
| `OnFrame` | ゲームの後 (ゲームの上に描く)。ゲームの `OnFrame` が失敗した frame でも呼び、開いたままの pass は閉じてから渡す |
| `OnQuit` | ゲームの前 |

overlay の callback の失敗は overlay の `OnError` に聞く。
overlay が `Input.Grab(keyboard, mouse)` で入力を取っている間は、その種類の event がゲームの `OnEvent` に届かず、ゲームから読む `Input` は何も押されていない状態を返す。
overlay が付くのは native の `lub Game.csproj` だけで、`--serve`、.NET 実行、tcs2c では付かない。
`lub export` と `--serve` は `Lub="Overlay"` の参照を読まないので、参照を書いたままでも、配る物と web には overlay のソースが入らない。

## .NET で実行する

同じソースを実 .NET で動かせる (.NET Hot Reload や本物のデバッガを使いたい
とき)。`dotnet/Lub` が lub の C API の facade と host で、共有 library
(`build-release-linux/liblub.so`、CMake の `lub_shared`) を P/Invoke する。
雛形は `templates/game/` (`dotnet run` で .NET 実行、`lub Game.csproj` で
tcs→Lua。入口は `App.Run(typeof(Game), args)`)。サンプルは runner で回す:

```
dotnet run --project dotnet/SampleRunner -p:Sample=09_breakout -- --capture out.png
```

共有 library は出力の隣か環境変数 `LUB_NATIVE_LIB` (full path) で見つける。
引数は `--backend` (player では環境変数 `LUB_BACKEND`) と、player と同じ `--fixed-dt` / `--capture` / `--capture-frame` / `--digest` / `--readback-fixed-latency`。
`Bytes` (view) は `AsSpan()` で読め、frame を跨いで持つと例外になる。
数値は C# の `float` のまま .NET で計算する(BCL の
double 演算は `(float)` で丸める)ので、Lua 側の f32 と同じ精度になる。
