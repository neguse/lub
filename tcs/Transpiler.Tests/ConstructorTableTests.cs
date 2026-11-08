namespace TinyCs.Tests;

// constructor の評価順 (field initializer → ctor 本文) と生成した値を tcs VM と .NET で見る
public class ConstructorTableTests
{
    private static string RunVm(string source, string mainBody, bool dotnet = true) => TestHelper.CompileAndRunVmMain(source, mainBody, dotnet);

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
        Assert.Equal("1 2 3", RunVm(source, "var v = new V(1, 2, 3); System.Console.WriteLine(v.X + \" \" + v.Y + \" \" + v.Z);"));
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
        Assert.Equal("3", TestHelper.CompileAndRunVm(source, "new W(3).D"));
    }

    [Fact]
    public void Division_IsNotReorderedAroundInitializers()
    {
        const string source = """
            public class Log { public static int Hit() { System.Console.WriteLine("init"); return 0; } }
            public class W
            {
                public int A = Log.Hit();
                public int Q;
                public W(int p) { Q = 10 / p; }
            }
            """;
        // 0 除算 fault より前に initializer の副作用が起きている
        var ex = Assert.Throws<InvalidOperationException>(() => RunVm(source, "var w = new W(0);"));
        Assert.Contains("fault: division by zero", ex.Message);
        Assert.Contains("stdout:\ninit", ex.Message.ReplaceLineEndings("\n"));
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
        Assert.Equal("False True 0 4 True", RunVm(source, "var l = new List<int>(); var w = new W(l); System.Console.WriteLine(w.Flag + \" \" + (w.Name == null) + \" \" + w.D + \" \" + w.N + \" \" + (w.Items == l));"));
    }

    [Fact]
    public void Record_PositionalNew()
    {
        const string source = "public record Pt(int X, int Y);";
        Assert.Equal("3 4", RunVm(source, "var p = new Pt(3, 4); System.Console.WriteLine(p.X + \" \" + p.Y);"));
    }

    [Fact]
    public void Struct_NewAndCopy_AreIndependentValues()
    {
        const string source = """
            public struct In { public int V; }
            public struct S { public int A; public In Inner; public string Name; }
            """;
        Assert.Equal("0 0 True 7 1", RunVm(source, "var s = new S(); var c = s; c.Inner.V = 7; System.Console.WriteLine(s.A + \" \" + s.Inner.V + \" \" + (s.Name == null) + \" \" + c.Inner.V + \" \" + (c.A + 1));"));
    }

    [Fact]
    public void EmptyStruct_NewAndCopy()
    {
        const string source = "public struct E { }";
        Assert.Equal("2", RunVm(source, "var e = new E(); var c = e; var a = new E[2]; a[0] = c; System.Console.WriteLine(a.Length);"));
    }

    [Fact]
    public void DictionaryLiteral_WithNullKey_FaultsAfterInitializers()
    {
        const string source = """
            using System.Collections.Generic;
            public class Log { public static int Side(string s) { System.Console.WriteLine(s); return 0; } }
            public class D
            {
                public Dictionary<string, int> M;
                public int K = Log.Side("init K");
                public D(string? key) { M = new Dictionary<string, int> { { key!, 1 } }; }
            }
            """;
        // null の key の fault より前に initializer の副作用が起きている
        var ex = Assert.Throws<InvalidOperationException>(() => RunVm(source, "var d = new D(null);", dotnet: false));
        Assert.Contains("fault:", ex.Message);
        Assert.Contains("stdout:\ninit K", ex.Message.ReplaceLineEndings("\n"));
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
        Assert.Equal("5 2", RunVm(source, "var d = new D(5); System.Console.WriteLine(d.M[\"a\"] + \" \" + d.M[\"b\"]);"));
    }

    // user 定義 operator は field initializer の後に走る
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
        Assert.Equal("initopneg", RunVm(source, "var w = new W(new V(), new V()); System.Console.WriteLine(Log.S);"));
    }

    // --ref の host 型の operator も受け付ける
    [Fact]
    public void HostOperator_IsAccepted()
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
        var result = Checker.Check([source], referenceSources: [refSource]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
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
        Assert.Equal("1.5 True", RunVm(source, "var w = new W(1, 3.5f, false); System.Console.WriteLine(w.D + \" \" + w.B);"));
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
        Assert.Equal("5,2", TestHelper.CompileAndRunVm(source, "new C().X + \",\" + new C(2).X"));
    }
}
