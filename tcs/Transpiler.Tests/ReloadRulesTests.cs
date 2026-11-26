namespace TinyCs.Tests;

// Edits that hot reload cannot apply live (the rule table in docs/tcs/hot-reload-design.md §11, Tool/ReloadRules.cs).
// The tcs VM reload (tcs vm --reload-from, --watch, VmSession) splits edits into restart / live from the IL types with the same rules.
// Actual migration of live edits: VmReloadTests and VmReloadCaseTests in tcs2c.Tests
public class ReloadRulesTests
{
    private const string Game = """
        public enum E { One, Two }
        public class G
        {
            public const int K = 1;
            public static int X = 1;
            public static int Y = 1 + 2;
            public static int Z = G.Make( 1 );
            public static int Make(int v) { return v; }
            public static float F = 1f / 3f;
            public int A = 1 + 2;
            public string S;
            public int Use(int v) { return v; }
            public int Twice(int v) { return Use(v) * 2; }
        }
        public record R(int X);
        """;

    private static List<string> RestartReasons(string v1, string v2)
    {
        var old = IlExport.Export([v1]);
        var next = IlExport.Export([v2]);
        Assert.Empty(old.Diagnostics);
        Assert.Empty(next.Diagnostics);
        return TcsSelf.ReloadRules.RestartReasons(TcsSelf.ReloadRules.TypesOf(old), TcsSelf.ReloadRules.TypesOf(next));
    }

    private static string Edit(string from, string to)
    {
        Assert.Contains(from, Game);
        return Game.Replace(from, to);
    }

    [Theory]
    [InlineData( // No expression left to re-evaluate
        "public class G { public static int X = 1; }",
        "public class G { public static int X; }",
        "static initializer removed: G.X")]
    [InlineData(
        "public class A { } public class B { }",
        "public class A { }",
        "type removed: B")]
    [InlineData(
        "public class A { } public class B { }",
        "public class A { } public struct B { public int V; }",
        "type kind changed: B")]
    [InlineData(
        "public enum E { One, Two }",
        "public enum E { Two, One }",
        "enum member removed or renumbered: E.One")]
    [InlineData(
        "public enum E { One, Two }",
        "public enum E { One }",
        "enum member removed or renumbered: E.Two")]
    public void RestartEdit_ReportsReason(string v1, string v2, string reason)
    {
        Assert.Contains(reason, RestartReasons(v1, v2));
    }

    [Theory]
    [InlineData( // Static values are kept (initializer is the same)
        "public class G { public static int X = 1; public int F() { return X; } }",
        "public class G { public static int X = 1; public int F() { return X + 1; } }")]
    [InlineData( // Adding/removing a static
        "public class G { public static int X = 1; public static int Y = 2; }",
        "public class G { public static int X = 1; public static int Z = 4; }")]
    [InlineData( // Adding/removing an instance field (live instances are migrated)
        "public class G { public int A = 1; }",
        "public class G { public int B = 2; }")]
    [InlineData( // Adding a type and an enum member
        "public enum E { One }",
        "public enum E { One, Two } public class N { }")]
    public void LiveEdit_HasNoReason(string v1, string v2)
    {
        Assert.Empty(RestartReasons(v1, v2));
    }

    // Renaming a method (the caller is also rebuilt in the same build) is live. The name stays the C# identifier.
    [Fact]
    public void MethodRename_WithCallersRebuilt_IsLive()
    {
        var edited = Game.Replace("Use(", "Fetch(");
        Assert.NotEqual(Game, edited);

        Assert.Empty(RestartReasons(Game, edited));
    }

    // Adding an overload to a static method adds the argument types to the existing name (`Make` → `Make__int`). The caller is also rebuilt, so it is live.
    [Fact]
    public void AddingStaticOverload_IsLive()
    {
        var edited = Edit("public static int Make(int v) { return v; }",
            "public static int Make(int v) { return v; }\n" +
            "    public static int Make(int v, int w) { return v + w; }");

        Assert.Empty(RestartReasons(Game, edited));
    }

    [Fact]
    public void AddingInstanceOverload_IsLive()
    {
        var edited = Edit("public int Use(int v) { return v; }",
            "public int Use(int v) { return v; }\n" +
            "    public int Use(int v, int w) { return v + w; }");

        Assert.Empty(RestartReasons(Game, edited));
    }

    [Theory]
    [InlineData("enum E { One, Two }", "enum E { Two, One }",
        "enum member removed or renumbered: E.One")]
    [InlineData("enum E { One, Two }", "enum E { One }",
        "enum member removed or renumbered: E.Two")]
    [InlineData("enum E { One, Two }", "enum E { One, Two = 5 }",
        "enum member removed or renumbered: E.Two")]
    public void RestartEdit_InGame_ReportsReason(string from, string to, string reason)
    {
        Assert.Contains(reason, RestartReasons(Game, Edit(from, to)));
    }

    // A static or const whose initializer or type changed is live, and reload re-evaluates it. A static that reads it in its initializer is also re-evaluated.
    [Theory]
    [InlineData("const int K = 1;", "const int K = 2;", "G.K")]
    [InlineData("static int X = 1;", "static int X = 2;", "G.X")]
    [InlineData("static int X = 1;", "static float X = 2f;", "G.X")]
    [InlineData("static int X = 1;", "static float X = 1;", "G.X")]
    [InlineData("F = 1f / 3f;", "F = 0.333333343f;", "G.F")]
    [InlineData("public const int K = 1;", "public static int K = 1;", "G.K")]
    [InlineData("public static int X = 1;", "public const int X = 1;", "G.X")]
    [InlineData("public static int Y = 1 + 2;", "public static int Y = 1 + 2;\n    public static int W = Y * K;", "")]
    [InlineData("public const int K = 1;\n    public static int X = 1;", "public const int K = 2;\n    public static int X = K + 1;", "G.K G.X")]
    [InlineData("public static int Y = 1 + 2;", "public static int Y = X + 2;", "G.Y")]
    public void StaticEdit_IsLiveAndReevaluated(string from, string to, string statics)
    {
        var old = IlExport.Export([Game]);
        var next = IlExport.Export([Edit(from, to)]);
        Assert.Empty(next.Diagnostics);
        var v1 = TcsSelf.ReloadRules.TypesOf(old);
        var v2 = TcsSelf.ReloadRules.TypesOf(next);
        Assert.Empty(TcsSelf.ReloadRules.RestartReasons(v1, v2));
        Assert.Equal(statics, string.Join(" ", TcsSelf.ReloadRules.ReinitStatics(v1, v2).Select(k => k.Replace("\n", "."))));
    }

    // A static whose initializer reads a changed static is re-evaluated by following what it reads.
    [Fact]
    public void StaticEdit_ReevaluatesReaders()
    {
        const string v1 = "public class G { public const float K = 1f; public static float A = K * 2f; public static float B = A + 1f; public static float C = 3f; } public class H { public static float D = G.B; }";
        var old = IlExport.Export([v1]);
        var next = IlExport.Export([v1.Replace("K = 1f", "K = 2f")]);
        Assert.Equal(["G.K", "G.A", "G.B", "H.D"],
            TcsSelf.ReloadRules.ReinitStatics(TcsSelf.ReloadRules.TypesOf(old), TcsSelf.ReloadRules.TypesOf(next)).Select(k => k.Replace("\n", ".")));
    }

    // An initializer does not change with whitespace, comments, or how constants are written.
    [Theory]
    [InlineData("1 + 2;\n    public static int Z", "1+2;\n    public static int Z")]
    [InlineData("1 + 2;\n    public static int Z", "3;\n    public static int Z")]
    [InlineData("G.Make( 1 )", "G.Make(1 /* seed */)")]
    [InlineData("public int A = 1 + 2;", "public int A = 1+2;")]
    [InlineData("enum E { One, Two }", "enum E { One, Two, Three }")]
    [InlineData("F = 1f / 3f;", "F = 1f/3f;")]
    public void LiveEdit_InitializerSpellingIsIgnored(string from, string to)
    {
        Assert.Empty(RestartReasons(Game, Edit(from, to)));
    }

    // Changing the shape of an instance and adding a static with a side-effecting initializer are also live (reload migrates instances and evaluates the initializer).
    [Theory]
    [InlineData("public int A = 1 + 2;", "public int A = 1 + 2;\n    public int B = 4;")]
    [InlineData("public int A = 1 + 2;", "public float A = 1 + 2;")]
    [InlineData("public string S;", "public int[] S;")]
    [InlineData("record R(int X)", "record R(float X)")]
    [InlineData("public static int X = 1;",
        "public static int X = 1;\n    public static int W = G.Make(2);")]
    public void InstanceShapeAndNewStatic_AreLive(string from, string to)
    {
        Assert.Empty(RestartReasons(Game, Edit(from, to)));
    }

    // Changing the initializer of an instance field is live, and the host is notified.
    [Fact]
    public void InstanceFieldInitializerChange_IsANote()
    {
        var old = IlExport.Export([Game]);
        var next = IlExport.Export([Edit("public int A = 1 + 2;", "public int A = 4;")]);
        Assert.Equal(["instance field initializer changed: G.A"],
            TcsSelf.ReloadRules.Notes(TcsSelf.ReloadRules.TypesOf(old), TcsSelf.ReloadRules.TypesOf(next)));
    }
}
