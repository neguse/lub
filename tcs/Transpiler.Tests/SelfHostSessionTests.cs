namespace TinyCs.Tests;

/// <summary>
/// セルフホストの増分の session (SelfHost/Compiler/Session.cs)。編集の後の結果 (IL) が 1 から compile した結果と同じで、
/// parse し直すのは内容が変わったファイルだけ。compile は Step に分かれる。
/// </summary>
public class SelfHostSessionTests
{
    private const string A = """
        public static class A
        {
            public static int Value() => 1;
        }
        """;

    private const string B = """
        public static class B
        {
            public static int Twice() => A.Value() * 2;
        }
        """;

    private static TcsSelf.Session NewSession() => new("") { ExportOnly = true };

    // IL の診断用テキスト (tcs vm --dump-il と同じ)
    private static string Il(TcsSelf.CompileResult result)
    {
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        return TcsSelf.VmRun.IlProgramText(result.Export!);
    }

    private static string Fresh(params (string Path, string Text)[] files)
    {
        var session = NewSession();
        foreach (var (path, text) in files) session.SetSource(path, text);
        return Il(session.Compile());
    }

    [Fact]
    public void Edit_ReparsesOnlyChangedFile_AndMatchesFreshCompile()
    {
        var session = NewSession();
        session.SetSource("a.cs", A);
        session.SetSource("b.cs", B);
        var first = session.Compile();
        Assert.True(first.Ok, string.Join("\n", first.Diagnostics));
        Assert.Equal(2, session.ParsedFiles);
        Assert.Equal(Fresh(("a.cs", A), ("b.cs", B)), Il(first));

        // 本体の編集: a.cs だけ parse し直す
        var a2 = A.Replace("=> 1;", "=> 5;");
        session.SetSource("a.cs", a2);
        var second = session.Compile();
        Assert.Equal(1, session.ParsedFiles);
        Assert.Equal(Fresh(("a.cs", a2), ("b.cs", B)), Il(second));

        // 変わっていなければ parse しない
        var third = session.Compile();
        Assert.Equal(0, session.ParsedFiles);
        Assert.Equal(Il(second), Il(third));
        Assert.Equal(3, session.Revision);
    }

    [Fact]
    public void ShapeEdit_RebindsDependents()
    {
        var session = NewSession();
        session.SetSource("a.cs", A);
        session.SetSource("b.cs", B);
        Assert.True(session.Compile().Ok);

        // a.cs の宣言を変えると b.cs の束縛が失敗する (束縛は毎回やり直す)
        session.SetSource("a.cs", A.Replace("Value()", "Other()"));
        var broken = session.Compile();
        Assert.False(broken.Ok);
        Assert.Contains(broken.Diagnostics, d => d.Contains("b.cs") && d.Contains("Value"));

        // 元に戻すと通る
        session.SetSource("a.cs", A);
        Assert.True(session.Compile().Ok);
    }

    [Fact]
    public void RemoveFile_DropsItsDeclarations()
    {
        var session = NewSession();
        session.SetSource("a.cs", A);
        session.SetSource("b.cs", B);
        Assert.True(session.Compile().Ok);
        Assert.True(session.Remove("b.cs"));
        var result = session.Compile();
        Assert.True(result.Ok);
        Assert.Equal(Fresh(("a.cs", A)), Il(result));
    }

    [Fact]
    public void Step_SplitsCompileIntoUnits()
    {
        var session = NewSession();
        session.SetSource("a.cs", A);
        session.SetSource("b.cs", B);
        session.Start();
        int steps = 1;
        while (!session.Step()) steps++;
        // parse 2 つ + 組み立て + 宣言 + 束縛 + 出力
        Assert.Equal(6, steps);
        Assert.True(session.Result.Ok);
        Assert.False(session.Busy);

        // 途中で編集が来たら、始め直した compile が新しい内容を使う
        session.SetSource("a.cs", A.Replace("=> 1;", "=> 7;"));
        session.Start();
        session.Step();
        session.SetSource("b.cs", B.Replace("* 2", "* 3"));
        session.Start();
        while (!session.Step())
        {
        }
        Assert.Equal(Fresh(("a.cs", A.Replace("=> 1;", "=> 7;")), ("b.cs", B.Replace("* 2", "* 3"))), Il(session.Result));
    }

    [Fact]
    public void ParseErrors_StayWithTheFileUntilItChanges()
    {
        var session = NewSession();
        session.SetSource("a.cs", A);
        session.SetSource("bad.cs", "public static class Bad { public static int F( }");
        var broken = session.Compile();
        Assert.False(broken.Ok);
        // a.cs を編集しても bad.cs の parse の誤りは残る (parse し直さないが診断は持ち越す)
        session.SetSource("a.cs", A.Replace("=> 1;", "=> 2;"));
        var still = session.Compile();
        Assert.Equal(1, session.ParsedFiles);
        Assert.False(still.Ok);
        Assert.Contains(still.Diagnostics, d => d.Contains("bad.cs"));
        session.SetSource("bad.cs", "public static class Bad { public static int F() => 0; }");
        Assert.True(session.Compile().Ok);
    }
}
