namespace TinyCs.Tests;

// Lua backend の instance / struct 生成は 1 つのテーブルコンストラクタで作る。畳みは
// 「他の評価と順序を入れ替えても観測できない」ものに限り、それ以外は
// C# の評価順 (field initializer → ctor 本文) のまま。Run は Lua の出力を走らせ、
// RunVm は同じ program の C# の評価順を tcs VM で見る
public class ConstructorTableTests
{
    private static string RunVm(string source, string mainBody) => TestHelper.CompileAndRunVmMain(source, mainBody);

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
            + "return v.X .. ' ' .. v.Y .. ' ' .. v.Z end)()"));
        Assert.Equal("1 2 3", RunVm(source, "var v = new V(1, 2, 3); System.Console.WriteLine(v.X + \" \" + v.Y + \" \" + v.Z);"));
        // 既定値 → 本来の値の二重代入が消えている (最適化の固定)
        Assert.DoesNotContain("self.X", Transpiler.Transpile([source]));
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
            + "return Counter.N .. ' ' .. w.A .. ' ' .. w.B end)()"));
        Assert.Equal("2 1 9", RunVm(source, "var w = new W(9); System.Console.WriteLine(Counter.N + \" \" + w.A + \" \" + w.B);"));
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
        Assert.Equal("abcctor", Run(source, "(function() W.new() return Log.S end)()"));
        Assert.Equal("abcctor", RunVm(source, "var w = new W(); System.Console.WriteLine(Log.S);"));
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
            "(function() local w = W.new(3) return w.Seen .. ' ' .. w.X end)()"));
        Assert.Equal("7 3", RunVm(source, "var w = new W(3); System.Console.WriteLine(w.Seen + \" \" + w.X);"));
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
            + "return w.X .. ' ' .. w.Y .. ' ' .. w.Z end)()"));
        Assert.Equal("4 5 4", RunVm(source, "var w = new W(4); System.Console.WriteLine(w.X + \" \" + w.Y + \" \" + w.Z);"));
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
            "(function() local w = W.new(8) return Probe.Seen .. ' ' .. w.X end)()"));
        Assert.Equal("5 8", RunVm(source, "var w = new W(8); System.Console.WriteLine(Probe.Seen + \" \" + w.X);"));
    }

    [Fact]
    public void MethodCallInBody_SeesFieldsBeforeAssignment()
    {
        const string source = """
            public class W
            {
                public int X = 2;
                public int Seen;
                public int Get() { return X; }
                public W(int p) { Seen = Get(); X = p; }
            }
            """;
        Assert.Equal("2 6", Run(source,
            "(function() local w = W.new(6) return w.Seen .. ' ' .. w.X end)()"));
        Assert.Equal("2 6", RunVm(source, "var w = new W(6); System.Console.WriteLine(w.Seen + \" \" + w.X);"));
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
        Assert.Equal("5", Run(source, "W.new(5).X"));
        Assert.Equal("5", TestHelper.CompileAndRunVm(source, "new W(5).X"));
    }

    [Fact]
    public void IntParameterToFloatField_KeepsValue()
    {
        const string source = """
            public class W
            {
                public float D;
                public W(int p) { D = p; }
            }
            """;
        Assert.Equal("3.0", Run(source, "W.new(3).D"));
        Assert.Equal("3", TestHelper.CompileAndRunVm(source, "new W(3).D"));
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
            "(function() local ok = pcall(W.new, 0) return tostring(ok) .. ' ' .. Log.N end)()"));
    }

    [Fact]
    public void FieldDefaults_AndCollectionInitializer()
    {
        const string source = """
            using System.Collections.Generic;
            public class W
            {
                public bool Flag; public string Name; public float D; public int N = 4;
                public List<int> Items = new List<int>();
                public W(List<int> items) { Items = items; }
            }
            """;
        Assert.Equal("false nil 0.0 4 true", Run(source,
            "(function() local l = {} local w = W.new(l) "
            + "return tostring(w.Flag) .. ' ' .. tostring(w.Name) .. ' ' .. w.D .. ' ' .. w.N"
            + " .. ' ' .. tostring(w.Items == l) end)()"));
        Assert.Equal("False True 0 4 True", RunVm(source, "var l = new List<int>(); var w = new W(l); System.Console.WriteLine(w.Flag + \" \" + (w.Name == null) + \" \" + w.D + \" \" + w.N + \" \" + (w.Items == l));"));
    }

    [Fact]
    public void Record_PositionalNew()
    {
        const string source = "public record Pt(int X, int Y);";
        Assert.Equal("3 4", Run(source,
            "(function() local p = Pt.new(3, 4) return p.X .. ' ' .. p.Y end)()"));
        Assert.Equal("3 4", RunVm(source, "var p = new Pt(3, 4); System.Console.WriteLine(p.X + \" \" + p.Y);"));
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
            + "c.Inner.V = 7 return s.A .. ' ' .. s.Inner.V .. ' ' .. tostring(s.Name)"
            + " .. ' ' .. c.Inner.V .. ' ' .. (c.A + 1) end)()"));
        Assert.Equal("0 0 True 7 1", RunVm(source, "var s = new S(); var c = s; c.Inner.V = 7; System.Console.WriteLine(s.A + \" \" + s.Inner.V + \" \" + (s.Name == null) + \" \" + c.Inner.V + \" \" + (c.A + 1));"));
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
            + "return tostring(ok) .. ' ' .. Log.S end)()"));
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
            "(function() local d = D.new(5) return d.M.a .. ' ' .. d.M.b end)()"));
        Assert.Equal("5 2", RunVm(source, "var d = new D(5); System.Console.WriteLine(d.M[\"a\"] + \" \" + d.M[\"b\"]);"));
        Assert.DoesNotContain("self.M", Transpiler.Transpile([source]));
    }

    // この head では user 定義 operator は Lua metamethod で動くので、
    // operand が user 型の演算は畳まない (initializer より先に operator が走る)
    [Fact]
    public void UserOperator_RunsAfterInitializers()
    {
        const string source = """
            public class Log { public static string S = ""; public static int Hit(string s) { S = S + s; return 0; } }
            public class V
            {
                public int X;
                public static V operator +(V a, V b) { Log.Hit("op"); return a; }
                public static V operator -(V a) { Log.Hit("neg"); return a; }
            }
            public class W
            {
                public V Sum;
                public V Neg;
                public int Marker = Log.Hit("init");
                public W(V a, V b) { Sum = a + b; Neg = -a; }
            }
            """;
        Assert.Equal("initopneg", Run(source,
            "(function() W.new(V.new(), V.new()) return Log.S end)()"));
        Assert.Equal("initopneg", RunVm(source, "var w = new W(new V(), new V()); System.Console.WriteLine(Log.S);"));
    }

    // --ref の host 型も operator は metamethod 経由で host code が走る
    [Fact]
    public void HostOperator_RunsAfterInitializers()
    {
        const string refSource = """
            public class HV { public static HV operator +(HV a, HV b) => a; }
            """;
        const string source = """
            public class Log { public static string S = ""; public static int Hit(string s) { S = S + s; return 0; } }
            public class W
            {
                public HV Sum;
                public int Marker = Log.Hit("init");
                public W(HV a, HV b) { Sum = a + b; }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([source],
            referenceSources: [refSource]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal("initop", TestHelper.RunLua(result.Lua
            + "\nlocal mt = {__add = function(a) Log.Hit('op') return a end}"
            + "\nW.new(setmetatable({}, mt), setmetatable({}, mt))"
            + "\nprint(Log.S)").Trim());
    }

    [Fact]
    public void PrimitiveArithmetic_IsStillFolded()
    {
        const string source = """
            public class W
            {
                public float D; public bool B;
                public W(int x, float y, bool f) { D = -x * 2 + y; B = !f && x < 3; }
            }
            """;
        Assert.Equal("1.5 true", Run(source,
            "(function() local w = W.new(1, 3.5, false) "
            + "return w.D .. ' ' .. tostring(w.B) end)()"));
        Assert.Equal("1.5 True", RunVm(source, "var w = new W(1, 3.5f, false); System.Console.WriteLine(w.D + \" \" + w.B);"));
        Assert.DoesNotContain("self.D", Transpiler.Transpile([source]));
    }

    [Fact]
    public void ThisConstructorInitializer_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class C
            {
                public int X;

                public C() : this(5) { }

                public C(int x)
                {
                    X = x;
                }
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && e.Contains("ThisConstructorInitializer"));
    }


    // constructor ごとに関数を作り、名前は呼び出しと同じ (LuaNaming.CtorName)
    [Fact]
    public void MultipleConstructors_RunTheChosenOverload()
    {
        const string source = """
            public class C
            {
                public int X = 5;

                public C()
                {
                }

                public C(int x)
                {
                    X = x;
                }
            }
            """;
        Assert.Equal("5", Run(source, "C.new().X"));
        Assert.Equal("2", Run(source, "C.new__int(2).X"));
        Assert.Equal("5,2", TestHelper.CompileAndRunVm(source, "new C().X + \",\" + new C(2).X"));
    }
}
