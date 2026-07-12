# C# (TinyC#) で書く

lub のゲームコードは Haxe のほかに C# でも書ける。使うのは
[tcs (TinyC#)](https://github.com/neguse/tcs) — C# のサブセットを Lua に
transpile するコンパイラで、playground ではブラウザ内 (Roslyn の WASM 版)、
native では `scripts/run-cs-sample.sh` 経由で動く。

「フル C# が動く」わけではない。TinyC# は **C# 構文の DSL** であり、
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
- `Math` / `Random` / `String` の実用サブセット

## 使えない主なもの

- `async` / `await` / `Task`、`try` / `throw`、reflection、`dynamic`
- ユーザー定義ジェネリクス、演算子オーバーロード
- `struct` / `record struct`(`class` / `record class` で代替)
- LINQ クエリ構文(`from x in y select`)

境界を踏むとコンパイル時に `TCS1001`(未対応構文)/ `TCS1002`(未対応 API)/
`TCS1003`(Lua table に置けない null 保存)の診断が出る。playground では
ログパネルに表示される。エラーではなく警告として出るものも、Lua 出力の
正しさに関わるので放置しない。

## lub API の呼び方

runtime API は Haxe と同じ namespace table
(`Gfx` / `Input` / `Lub` / `Io` / ...) をそのまま呼ぶ。メンバー名は Lua の
wire format (snake_case) に合わせる:

```csharp
public static class Main
{
    public static void onInit()
    {
        Lub.config(new ConfigOpts { width = 640, height = 360 });
    }

    public static void onFrame(double dt)
    {
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.end_pass();
    }
}
```

型定義は `cs-lib/lub_stub.cs`(参照専用 stub)にある。IDE で書くときは
これを参照に加えると補完と型チェックが効く。Lua 側の multi-return
(`Io.load_text` など) は `out` 引数で受ける。

サンプルは Haxe 版と同じディレクトリに同居する (例:
`samples/09_breakout/Breakout.cs` + `Breakout.csproj`)。playground では
画面上部の言語トグルで Haxe / C# を切り替えられる (C# 版があるサンプルのみ)。
native での実行は hxml と対称:

```
lub samples/09_breakout/Breakout.csproj           # transpile + watch + hot reload
scripts/run-cs-sample.sh 09_breakout --check      # 診断のみ
scripts/run-cs-sample.sh 09_breakout --build      # transpile のみ
```

csproj は lub にとっては entry 指定 (basename = entry class、入力 = 同
ディレクトリの全 `*.cs`) でしかないが、dotnet 側 (Rider / VS Code) では
本物のプロジェクトとして機能する: stub と TinySystem を参照し、TinyC#
サブセット逸脱は Roslyn Analyzer が IDE 上で TCS 診断として出す。
