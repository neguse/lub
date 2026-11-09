namespace TinyCs.Tests;

// namespace の中の型は常に namespace 修飾名 (`A_Color`) の IL 名、namespace
// に属さない型は simple 名にする。型の IL 名が他の型の有無に
// 左右されない。宣言・参照・IlExport・hot reload の型の対応で同じ名前を使う。
public class NamespaceTypeNameTests
{
    private const string TwoColors = """
        namespace A { public class Color { public int V = 1; } }
        namespace B { public class Color { public int V = 2; } }

        public static class NsRepro
        {
            public static int Test() => new A.Color().V * 10 + new B.Color().V;
        }
        """;

    private static string[] ClassNames(string source)
    {
        var export = IlExport.Export([source]);
        Assert.Empty(export.Diagnostics);
        return [.. export.Classes.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal)];
    }

    [Fact]
    public void SameNameInTwoNamespaces_KeepsBothTypes()
    {
        Assert.Equal("12", TestHelper.CompileAndRunVm(TwoColors, "NsRepro.Test()"));
    }

    [Fact]
    public void NamespacedType_IsQualifiedEvenWhenUnique()
    {
        const string source = """
            namespace Game.Gfx { public class Color { public int V = 3; } }
            public static class P { public static int Test() => new Game.Gfx.Color().V; }
            """;
        Assert.Equal(["Game_Gfx_Color", "P"], ClassNames(source));
        Assert.Equal("3", TestHelper.CompileAndRunVm(source, "P.Test()"));
    }

    [Fact]
    public void GlobalType_KeepsSimpleNameNextToNamespacedSameName()
    {
        const string source = """
            public class Color { public int V = 1; }
            namespace A { public class Color { public int V = 2; } }
            public static class P { public static int Test() => new Color().V * 10 + new A.Color().V; }
            """;
        Assert.Equal(["A_Color", "Color", "P"], ClassNames(source));
        Assert.Equal("12", TestHelper.CompileAndRunVm(source, "P.Test()"));
    }

    [Fact]
    public void NestedNamespace_QualifiesWithEverySegment()
    {
        var source = """
            namespace Game.Gfx { public class Color { public int V = 3; } }
            namespace Ui { public class Color { public int V = 4; } }
            public static class P
            {
                public static int Test() => new Game.Gfx.Color().V * 10 + new Ui.Color().V;
            }
            """;
        Assert.Equal("34", TestHelper.CompileAndRunVm(source, "P.Test()"));
        Assert.Equal(["Game_Gfx_Color", "P", "Ui_Color"], ClassNames(source));
    }

    [Fact]
    public void StaticMembers_EnumsAndStructs_ResolvePerNamespace()
    {
        const string source = """
            namespace A
            {
                public enum Kind { X = 1, Y = 2 }
                public struct Vec { public int X; public Vec(int x) { X = x; } }
                public static class Util
                {
                    public static int Count = 10;
                    public static int F() => Count + (int)Kind.Y;
                }
            }
            namespace B
            {
                public enum Kind { X = 100, Y = 200 }
                public struct Vec { public int X; public int Y; public Vec(int x) { X = x; Y = x * 2; } }
                public static class Util
                {
                    public static int Count = 1000;
                    public static int F() => Count + (int)Kind.Y;
                }
            }
            public static class P
            {
                public static int Test()
                {
                    var a = new A.Vec(3);
                    var b = new B.Vec(3);
                    var c = b;
                    c.Y = 9;
                    return A.Util.F() + B.Util.F() + a.X + b.Y + c.Y;
                }
            }
            """;
        // 12 + 1200 + 3 + 6 + 9
        Assert.Equal("1230", TestHelper.CompileAndRunVm(source, "P.Test()"));
    }

    [Fact]
    public void TypeTest_AndCast_UseQualifiedNames()
    {
        const string source = """
            namespace A { public class Base { public int Id() => 11; } }
            namespace B { public class Base { public int Id() => 2; } }
            public static class P
            {
                public static int Test()
                {
                    object a = new A.Base();
                    object b = new B.Base();
                    var n = 0;
                    if (a is A.Base) n += 1;
                    if (b is B.Base) n += 2;
                    if (a is B.Base) n += 100;
                    if (b is A.Base) n += 100;
                    var da = (A.Base)a;
                    var db = (B.Base)b;
                    return n * 100 + da.Id() + db.Id();
                }
            }
            """;
        Assert.Equal("313", TestHelper.CompileAndRunVm(source, "P.Test()"));
    }

    [Fact]
    public void EntryClass_QualifiedName_Resolves()
    {
        var result = Checker.Check([TwoColors.Replace(
            "public int V = 1;", "public int V = 1; public static int Ping() => 7;")],
            entryClass: "A.Color");
        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    [Fact]
    public void IlExport_UsesQualifiedNamesAndKeepsDisplayNames()
    {
        var result = IlExport.Export([TwoColors]);
        Assert.Empty(result.Diagnostics);
        var names = result.Classes.Select(c => c.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["A_Color", "B_Color", "NsRepro"], names);
        var a = result.Classes.Single(c => c.Name == "A_Color");
        Assert.Equal("A.Color", a.DisplayName);
        Assert.Equal("NsRepro",
            result.Classes.Single(c => c.Name == "NsRepro").DisplayName);
    }

    // 型の IL 名は他ファイルの型に左右されないので、同名型を足しても既存の
    // 型は改名されない (reload の restart の理由にならない)
    [Fact]
    public void AddingSameNamedTypeKeepsTheOtherTypeName()
    {
        const string a = "namespace A { public class Color { public int V = 1; } }";
        var before = IlExport.Export([a, "namespace B { public class Shade { public int V = 2; } }"]);
        var after = IlExport.Export([a, "namespace B { public class Color { public int V = 2; } }"]);
        Assert.Empty(before.Diagnostics);
        Assert.Empty(after.Diagnostics);
        Assert.Contains(before.Classes, c => c.Name == "A_Color");
        Assert.Contains(after.Classes, c => c.Name == "A_Color");
        Assert.Contains(after.Classes, c => c.Name == "B_Color");
        var reasons = TcsSelf.ReloadRules.RestartReasons(TcsSelf.ReloadRules.TypesOf(before), TcsSelf.ReloadRules.TypesOf(after));
        Assert.DoesNotContain(reasons, r => r.Contains("A_Color"));
    }

    // C# のキーワードを `@` で逃がした namespace は、`@` を除いた名前で修飾する
    [Fact]
    public void VerbatimKeywordNamespace_QualifiesWithoutAt()
    {
        const string source = """
            namespace @event { public class C { public static int F() { return 7; } } }
            public static class Game { public static int Run() { return @event.C.F(); } }
            """;
        Assert.Contains("event_C", ClassNames(source));
        Assert.Equal("7", TestHelper.CompileAndRunVm(source, "Game.Run()"));
    }
}
