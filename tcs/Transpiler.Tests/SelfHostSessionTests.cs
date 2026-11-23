namespace TinyCs.Tests;

/// <summary>
/// Self-host incremental session (SelfHost/Compiler/Session.cs). The result (IL) after an edit is the same as compiling from scratch, and
/// only files whose content changed are parsed again. Compilation is split into Steps.
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

    // IL text for diagnostics (same as tcs vm --dump-il)
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

        // Body edit: only a.cs is parsed again
        var a2 = A.Replace("=> 1;", "=> 5;");
        session.SetSource("a.cs", a2);
        var second = session.Compile();
        Assert.Equal(1, session.ParsedFiles);
        Assert.Equal(Fresh(("a.cs", a2), ("b.cs", B)), Il(second));

        // Not parsed if unchanged
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

        // Changing a declaration in a.cs makes binding of b.cs fail (binding is redone every time)
        session.SetSource("a.cs", A.Replace("Value()", "Other()"));
        var broken = session.Compile();
        Assert.False(broken.Ok);
        Assert.Contains(broken.Diagnostics, d => d.Contains("b.cs") && d.Contains("Value"));

        // Reverting it makes it pass
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
        // Two parses + assemble + declare + bind + emit
        Assert.Equal(6, steps);
        Assert.True(session.Result.Ok);
        Assert.False(session.Busy);

        // If an edit arrives midway, the restarted compile uses the new content
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
        // Editing a.cs keeps the parse error of bad.cs (it is not parsed again, but the diagnostic is carried over)
        session.SetSource("a.cs", A.Replace("=> 1;", "=> 2;"));
        var still = session.Compile();
        Assert.Equal(1, session.ParsedFiles);
        Assert.False(still.Ok);
        Assert.Contains(still.Diagnostics, d => d.Contains("bad.cs"));
        session.SetSource("bad.cs", "public static class Bad { public static int F() => 0; }");
        Assert.True(session.Compile().Ok);
    }
}
