namespace TinyCs.Tests;

// instance / struct 生成は 1 つのテーブルコンストラクタで作る。畳みは
// 「他の評価と順序を入れ替えても観測できない」ものに限り、それ以外は
// C# の評価順 (field initializer → ctor 本文) のまま
public class ConstructorTableTests
{
    private static string Run(string source, string luaExpr)
    {
        var lua = Transpiler.Transpile([source]);
        return TestHelper.RunLua($"{lua}\nprint({luaExpr})").Trim();
    }

    [Fact]
    public void CtorAssignments_AreFoldedIntoTableConstructor()
    {
        const string source = """
            public class V
            {
                public int X; public int Y; public int Z;
                public V(int x, int y, int z) { X = x; Y = y; this.Z = z; }
            }
            """;
        Assert.Equal("1 2 3", Run(source,
            "(function() local v = V.new(1, 2, 3) "
            + "return v.x .. ' ' .. v.y .. ' ' .. v.z end)()"));
        // 既定値 → 本来の値の二重代入が消えている (最適化の固定)
        Assert.DoesNotContain("self.x", Transpiler.Transpile([source]));
    }

    [Fact]
    public void Initializer_WithSideEffect_StillRunsWhenOverwritten()
    {
        const string source = """
            public class Counter { public static int N; public static int Next() { N = N + 1; return N; } }
            public class W
            {
                public int A = Counter.Next();
                public int B = Counter.Next();
                public W(int p) { B = p; }
            }
            """;
        Assert.Equal("2 1 9", Run(source,
            "(function() local w = W.new(9) "
            + "return Counter.n .. ' ' .. w.a .. ' ' .. w.b end)()"));
    }

    [Fact]
    public void Initializers_KeepDeclarationOrder()
    {
        const string source = """
            public class Log { public static string S = ""; public static int Add(string s) { S = S + s; return 0; } }
            public class W
            {
                public int A = Log.Add("a");
                public int B = Log.Add("b");
                public int C = Log.Add("c");
                public W() { Log.Add("ctor"); }
            }
            """;
        Assert.Equal("abcctor", Run(source, "(function() W.new() return Log.s end)()"));
    }

    [Fact]
    public void BodyReadingFieldBeforeAssignment_SeesInitializer()
    {
        const string source = """
            public class W
            {
                public int X = 7;
                public int Seen;
                public W(int p) { Seen = X; X = p; }
            }
            """;
        // Seen = X は field を読むので畳まれず、X = p より前に実行される
        Assert.Equal("7 3", Run(source,
            "(function() local w = W.new(3) return w.seen .. ' ' .. w.x end)()"));
    }

    [Fact]
    public void FoldStopsAtNonFoldable_LaterAssignmentsKeepOrder()
    {
        const string source = """
            public class W
            {
                public int X; public int Y; public int Z;
                public W(int p) { X = p; Y = X + 1; Z = p; }
            }
            """;
        Assert.Equal("4 5 4", Run(source,
            "(function() local w = W.new(4) "
            + "return w.x .. ' ' .. w.y .. ' ' .. w.z end)()"));
    }

    [Fact]
    public void ThisEscapingBeforeAssignment_SeesDefault()
    {
        const string source = """
            public class Probe { public static int Seen = -1; public static void Look(W w) { Seen = w.X; } }
            public class W
            {
                public int X = 5;
                public W(int p) { Probe.Look(this); X = p; }
            }
            """;
        Assert.Equal("5 8", Run(source,
            "(function() local w = W.new(8) return Probe.seen .. ' ' .. w.x end)()"));
    }

    [Fact]
    public void VirtualCallInBody_SeesFieldsBeforeAssignment()
    {
        const string source = """
            public class W
            {
                public int X = 2;
                public int Seen;
                public virtual int Get() { return X; }
                public W(int p) { Seen = Get(); X = p; }
            }
            """;
        Assert.Equal("2 6", Run(source,
            "(function() local w = W.new(6) return w.seen .. ' ' .. w.x end)()"));
    }

    [Fact]
    public void RepeatedAssignment_LastWins()
    {
        const string source = """
            public class W
            {
                public int X;
                public W(int p) { X = 1; X = p; }
            }
            """;
        Assert.Equal("5", Run(source, "W.new(5).x"));
    }

    [Fact]
    public void IntParameterToDoubleField_KeepsValue()
    {
        const string source = """
            public class W
            {
                public double D;
                public W(int p) { D = p; }
            }
            """;
        Assert.Equal("3", Run(source, "W.new(3).d"));
    }

    [Fact]
    public void Division_IsNotReorderedAroundInitializers()
    {
        const string source = """
            public class Log { public static int N; public static int Hit() { N = 1; return 0; } }
            public class W
            {
                public int A = Log.Hit();
                public int Q;
                public W(int p) { Q = 10 / p; }
            }
            """;
        // 0 除算 fault より前に initializer の副作用が起きている
        Assert.Equal("false 1", Run(source,
            "(function() local ok = pcall(W.new, 0) return tostring(ok) .. ' ' .. Log.n end)()"));
    }

    [Fact]
    public void Derived_RunsBaseFieldInitializersThenOwn()
    {
        const string source = """
            public class Base { public int A = 1; public Base() { A = A + 10; } }
            public class Derived : Base
            {
                public int B = 2;
                public Derived(int p) { B = p; }
            }
            """;
        Assert.Equal("11 9", Run(source,
            "(function() local d = Derived.new(9) return d.a .. ' ' .. d.b end)()"));
    }

    [Fact]
    public void FieldDefaults_AndCollectionInitializer()
    {
        const string source = """
            using System.Collections.Generic;
            public class W
            {
                public bool Flag; public string Name; public double D; public int N = 4;
                public List<int> Items = new List<int>();
                public W(List<int> items) { Items = items; }
            }
            """;
        Assert.Equal("false nil 0 4 true", Run(source,
            "(function() local l = {} local w = W.new(l) "
            + "return tostring(w.flag) .. ' ' .. tostring(w.name) .. ' ' .. w.d .. ' ' .. w.n"
            + " .. ' ' .. tostring(w.items == l) end)()"));
    }

    [Fact]
    public void Record_PositionalNew()
    {
        const string source = "public record Pt(int X, int Y);";
        Assert.Equal("3 4", Run(source,
            "(function() local p = Pt.new(3, 4) return p.x .. ' ' .. p.y end)()"));
    }

    [Fact]
    public void Struct_NewAndCopy_AreIndependentValues()
    {
        const string source = """
            public struct In { public int V; }
            public struct S { public int A; public In Inner; public string Name; }
            """;
        Assert.Equal("0 0 nil 7 1", Run(source,
            "(function() local s = S.new() local c = S.__copy(s) "
            + "c.inner.v = 7 return s.a .. ' ' .. s.inner.v .. ' ' .. tostring(s.name)"
            + " .. ' ' .. c.inner.v .. ' ' .. (c.a + 1) end)()"));
    }

    [Fact]
    public void EmptyStruct_NewAndCopy()
    {
        const string source = "public struct E { }";
        Assert.Equal("table", Run(source,
            "type(E.__copy(E.new()))"));
    }
    [Fact]
    public void DictionaryLiteral_WithParamKey_FaultsAfterInitializers()
    {
        // nil key は Lua のテーブル生成中に fault するので、畳むと先行する
        // initializer の副作用が消える
        const string source = """
            using System.Collections.Generic;
            public class Log { public static string S = ""; public static int Side(string s) { S = S + s; return 0; } }
            public class D
            {
                public Dictionary<string, int> M;
                public int K = Log.Side("init K");
                public D(string key) { M = new Dictionary<string, int> { { key, 1 } }; }
            }
            """;
        Assert.Equal("false init K", Run(source,
            "(function() local ok = pcall(D.new, nil) "
            + "return tostring(ok) .. ' ' .. Log.s end)()"));
    }

    [Fact]
    public void DictionaryLiteral_WithLiteralKeys_IsStillFolded()
    {
        const string source = """
            using System.Collections.Generic;
            public class D
            {
                public Dictionary<string, int> M;
                public D(int v) { M = new Dictionary<string, int> { { "a", v }, { "b", 2 } }; }
            }
            """;
        Assert.Equal("5 2", Run(source,
            "(function() local d = D.new(5) return d.m.a .. ' ' .. d.m.b end)()"));
        Assert.DoesNotContain("self.m", Transpiler.Transpile([source]));
    }
}
