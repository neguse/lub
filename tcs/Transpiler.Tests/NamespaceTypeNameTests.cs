namespace TinyCs.Tests;

// namespace の中の型は常に namespace 修飾名 (`A_Color`) の global、namespace
// に属さない型は simple 名の global にする。型の Lua 名が他の型の有無に
// 左右されない。宣言・参照・IlExport・hot reload・snapshot・--entry/--module
// で同じ名前を使う。
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

    [Fact]
    public void SameNameInTwoNamespaces_KeepsBothTypes()
    {
        Assert.Equal("12", TestHelper.TranspileAndRun(TwoColors, "NsRepro.Test()"));
    }

    [Fact]
    public void CollidingTypes_GetNamespaceQualifiedLuaNames()
    {
        var lua = Transpiler.Transpile(TwoColors);
        Assert.Contains("A_Color = {}", lua);
        Assert.Contains("B_Color = {}", lua);
        Assert.DoesNotContain("\nColor = {}", lua);
        // 一意な型は simple 名のまま
        Assert.Contains("NsRepro = {}", lua);
    }

    [Fact]
    public void NamespacedType_IsQualifiedEvenWhenUnique()
    {
        const string source = """
            namespace Game.Gfx { public class Color { public int V = 3; } }
            public static class P { public static int Test() => new Game.Gfx.Color().V; }
            """;
        var lua = Transpiler.Transpile(source);
        Assert.Contains("Game_Gfx_Color = {}", lua);
        Assert.DoesNotContain("\nColor = {}", lua);
        Assert.Equal("3", TestHelper.TranspileAndRun(source, "P.Test()"));
    }

    [Fact]
    public void GlobalType_KeepsSimpleNameNextToNamespacedSameName()
    {
        const string source = """
            public class Color { public int V = 1; }
            namespace A { public class Color { public int V = 2; } }
            public static class P { public static int Test() => new Color().V * 10 + new A.Color().V; }
            """;
        var lua = Transpiler.Transpile(source);
        Assert.Contains("\nColor = {}", lua);
        Assert.Contains("A_Color = {}", lua);
        Assert.Equal("12", TestHelper.TranspileAndRun(source, "P.Test()"));
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
        Assert.Equal("34", TestHelper.TranspileAndRun(source, "P.Test()"));
        Assert.Contains("Game_Gfx_Color = {}", Transpiler.Transpile(source));
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
        Assert.Equal("1230", TestHelper.TranspileAndRun(source, "P.Test()"));
    }

    [Fact]
    public void Inheritance_TypeTest_AndCast_UseQualifiedNames()
    {
        const string source = """
            namespace A { public class Base { public virtual int Id() => 1; } }
            namespace B { public class Base { public virtual int Id() => 2; } }
            public class Da : A.Base { public override int Id() => base.Id() + 10; }
            public class Db : B.Base { }
            public static class P
            {
                public static int Test()
                {
                    object a = new Da();
                    object b = new Db();
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
        Assert.Equal("313", TestHelper.TranspileAndRun(source, "P.Test()"));
    }

    [Fact]
    public void EntryClass_ReturnsQualifiedTable()
    {
        var result = Transpiler.TranspileWithDiagnostics([TwoColors.Replace(
            "public int V = 1;", "public int V = 1; public static int Ping() => 7;")],
            entryClass: "A.Color");
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.EndsWith("return A_Color\n", result.Lua);
    }

    [Fact]
    public void ModuleMode_ExportsQualifiedKeys()
    {
        var result = Transpiler.TranspileWithDiagnostics([TwoColors], module: true);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains("A_Color = A_Color", result.Lua);
        Assert.Contains("B_Color = B_Color", result.Lua);
    }

    // raw Lua の require 先からは C# の simple 名で引ける (simple 名が出力内で
    // 重複するときだけ修飾名の key)
    [Fact]
    public void ModuleMode_UniqueNamespacedType_ExportsSimpleKey()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            namespace Game.Gfx { public class Color { public int V = 3; } }
            public class Plain { public int V = 4; }
            """], module: true);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains("Color = Game_Gfx_Color", result.Lua);
        Assert.Contains("Plain = Plain", result.Lua);
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

    [Fact]
    public void HotReload_MigratesInstancesOfBothTypes()
    {
        const string V1 = """
            namespace A { public class Color { public int V = 1; } }
            namespace B { public class Color { public int V = 2; public int Old = 5; } }
            """;
        const string V2 = """
            namespace A { public class Color { public int V = 1; public int Extra = 11; } }
            namespace B { public class Color { public int V = 2; public int Added = 22; } }
            """;
        var script = TestHelper.ComposeReload(V1,
            """
            local a = A_Color.new()
            local b = B_Color.new()
            local ca, cb = A_Color, B_Color
            """,
            V2,
            """
            assert(A_Color == ca and B_Color == cb, "class identity")
            assert(a.extra == 11, "A.Color added field")
            assert(b.added == 22 and b.old == nil, "B.Color migrated")
            print("ok")
            """);
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void HotReload_ReserializesNamespacedStructField()
    {
        const string V1 = """
            namespace A { public struct Vec { public int X; } }
            public class Holder { public A.Vec Pos; }
            """;
        const string V2 = """
            namespace A { public struct Vec { public int X; public int Y; } }
            public class Holder { public A.Vec Pos; }
            """;
        var script = TestHelper.ComposeReload(V1,
            """
            local h = Holder.new()
            h.pos.x = 4
            """,
            V2,
            """
            assert(h.pos.x == 4, "retained struct field")
            assert(h.pos.y == 0, "added struct field gets default")
            print("ok")
            """);
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void Snapshot_RegistersQualifiedTypeIdsAndResolvesNamespacedEntry()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("ns.cs", TwoColors.Replace(
            "public int V = 1;", "public int V = 1; public static int Ping() => 7;"))]);
        var snapshot = ModuleLinker.LinkSnapshot(session.Artifacts, session.Revision,
            "A.Color", LuaRuntime.LoadTinySystemSource(),
            LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath));
        var path = Path.GetTempFileName().Replace("\\", "/");
        try
        {
            File.WriteAllText(path, snapshot);
            var output = TestHelper.RunLua($"""
                local w = dofile("{path}")
                local reg = _G.__tcs_module_runtime.registry
                print(w.ping(), reg.types["ns.cs#A_Color"] ~= nil,
                    reg.types["ns.cs#B_Color"] ~= nil)
                """).Trim();
            Assert.Equal("7\ttrue\ttrue", output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Snapshot_AmbiguousSimpleEntry_Throws()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("ns.cs", TwoColors)]);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModuleLinker.LinkSnapshot(session.Artifacts, session.Revision,
                "Color", LuaRuntime.LoadTinySystemSource(),
                LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath)));
        Assert.Contains("ambiguous", ex.Message);
    }

    // 型の Lua 名は他ファイルの型に左右されないので、同名型を足しても既存の
    // 型は改名されない (restart 境界にならない)
    [Fact]
    public void Incremental_AddingSameNamedTypeKeepsTheOtherModuleName()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("a.cs", "namespace A { public class Color { public int V = 1; } }"),
            ("b.cs", "namespace B { public class Shade { public int V = 2; } }")]);
        Assert.Equal(["A_Color"], session.Artifacts[0].Types.Select(t => t.Name));

        var result = session.Update("b.cs",
            "namespace B { public class Color { public int V = 2; } }");
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal(["A_Color"], session.Artifacts[0].Types.Select(t => t.Name));
        Assert.Equal(["B_Color"], session.Artifacts[1].Types.Select(t => t.Name));
        Assert.DoesNotContain(result.RestartReasons, r => r.Contains("A_Color"));
    }

    // fast path の method 差し替えも full emit と同じ Lua 型名で探す。simple 名
    // で探すと global の Color と A.Color を取り違える
    [Fact]
    public void Incremental_BodyEditInQualifiedType_SplicesThatTypeOnly()
    {
        const string source = """
            public class Color { public static int F() { return 1; } }
            namespace A { public class Color { public static int F() { return 2; } } }
            """;
        var session = new IncrementalCompilationSession(checkNaming: false);
        session.OpenProject([("c.cs", source)]);
        var result = session.Update("c.cs", source.Replace("return 2;", "return 3;"));
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(result.FastPath);
        var lua = session.Artifacts.Single().Lua;
        Assert.Equal("1\t3", TestHelper.RunLua($"{lua}\nprint(Color.f(), A_Color.f())").Trim());
    }

    // C# のキーワードを `@` で逃がした namespace は、`@` を除いた名前で修飾する
    [Fact]
    public void VerbatimKeywordNamespace_QualifiesWithoutAt()
    {
        const string source = """
            namespace @event { public class C { public static int F() { return 7; } } }
            public static class Game { public static int Run() { return @event.C.F(); } }
            """;
        Assert.Contains("event_C = {}", Transpiler.Transpile(source));
        Assert.Equal("7", TestHelper.TranspileAndRun(source, "Game.Run()"));
    }
}
