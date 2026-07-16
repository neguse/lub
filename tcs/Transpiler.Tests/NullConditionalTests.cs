namespace TinyCs.Tests;

public class NullConditionalTests
{
    [Fact]
    public void NullConditional_PropertyAccess_NotNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Obj
            {
                public int Value = 42;
            }
            public class T
            {
                public static int Test()
                {
                    Obj o = new Obj();
                    var v = o?.Value;
                    return v ?? 0;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void NullConditional_PropertyAccess_Null()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Obj
            {
                public int Value = 42;
            }
            public class T
            {
                public static string Test()
                {
                    Obj o = null;
                    var v = o?.Value;
                    return v == null ? "nil" : "not nil";
                }
            }
            """,
            "T.Test()");
        Assert.Equal("nil", result);
    }

    [Fact]
    public void IsPattern_NotNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    string s = "hello";
                    return s is not null;
                }
            }
            """,
            "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void IsPattern_Null()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    string s = null;
                    return s is null;
                }
            }
            """,
            "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void CoalesceAssignment_WhenNull_Assigns()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    string s = null;
                    s ??= "default";
                    return s;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("default", result);
    }

    [Fact]
    public void CoalesceAssignment_WhenNotNull_Skips()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    string s = "hello";
                    s ??= "default";
                    return s;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void NullConditionalIndexer_List_NotNull()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    List<int> list = new List<int> { 10, 20, 30 };
                    var v = list?[1];
                    return v ?? 0;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("20", result);
    }

    [Fact]
    public void NullConditionalIndexer_List_Null()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    List<int> list = null;
                    var v = list?[0];
                    return v == null ? "nil" : "not nil";
                }
            }
            """,
            "T.Test()");
        Assert.Equal("nil", result);
    }

    [Fact]
    public void NullConditionalIndexer_Dict_NotNull()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    Dictionary<string, int> dict = new Dictionary<string, int> { { "a", 42 } };
                    var v = dict?["a"];
                    return v ?? 0;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void NullConditionalStringMethod_UsesRuntimeMapping()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    string s = "hello";
                    return s?.Contains("ell") ?? false;
                }
            }
            """,
            "tostring(T.Test())");

        Assert.Equal("true", result);
    }

    [Fact]
    public void NullConditionalStringMethod_NullPropagates()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string s = null;
                    return s?.Substring(1, 2) ?? "nil";
                }
            }
            """,
            "T.Test()");

        Assert.Equal("nil", result);
    }

    [Fact]
    public void NullConditionalListMethod_UsesRuntimeMapping()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static bool Test()
                {
                    List<int> list = new List<int> { 1, 2, 3 };
                    return list?.Contains(2) ?? false;
                }
            }
            """,
            "tostring(T.Test())");

        Assert.Equal("true", result);
    }

    [Fact]
    public void NullConditionalDictMethod_UsesRuntimeMapping()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static bool Test()
                {
                    Dictionary<string, int> dict = new Dictionary<string, int> { { "a", 10 } };
                    return dict?.ContainsKey("a") ?? false;
                }
            }
            """,
            "tostring(T.Test())");

        Assert.Equal("true", result);
    }

    [Fact]
    public void CoalesceAssignment_WithObjectCreation()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Obj
            {
                public int Value;
                public Obj(int v) { Value = v; }
            }
            public class T
            {
                public static int Test()
                {
                    Obj o = null;
                    o ??= new Obj(99);
                    return o.Value;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("99", result);
    }

    // T139: ?. の receiver は一度だけ評価し、null のときは引数/index を
    // 評価しない (C# の評価回数・順序と一致させる)。
    [Fact]
    public void NullConditional_ReceiverWithSideEffect_EvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                public int Value = 7;
            }
            public class T
            {
                public static int Calls;

                public static Box Make()
                {
                    Calls = Calls + 1;
                    return new Box();
                }

                public static int Test()
                {
                    var v = Make()?.Value;
                    var got = v ?? 0;
                    return Calls * 100 + got;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("107", result);
    }

    [Fact]
    public void NullConditional_NullReceiver_DoesNotEvaluateArguments()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                public int Twice(int x) => x * 2;
            }
            public class T
            {
                public static int MakeCalls;
                public static int ArgCalls;

                public static Box Make()
                {
                    MakeCalls = MakeCalls + 1;
                    return null;
                }

                public static int Arg()
                {
                    ArgCalls = ArgCalls + 1;
                    return 5;
                }

                public static int Test()
                {
                    var v = Make()?.Twice(Arg());
                    var got = v ?? -1;
                    return MakeCalls * 100 + ArgCalls * 10 + got;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("99", result);
    }

    [Fact]
    public void NullConditional_ElementAccess_ReceiverOnceAndIndexLazy()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            public class T
            {
                public static int ListCalls;
                public static int IndexCalls;

                public static List<int> MakeList(bool exists)
                {
                    ListCalls = ListCalls + 1;
                    if (exists) return new List<int> { 10, 20, 30 };
                    return null;
                }

                public static int Index()
                {
                    IndexCalls = IndexCalls + 1;
                    return 1;
                }

                public static int Test()
                {
                    var hit = MakeList(true)?[Index()];
                    var miss = MakeList(false)?[Index()];
                    var got = hit ?? 0;
                    var missPart = miss == null ? 1 : 0;
                    return ListCalls * 1000 + IndexCalls * 100 + got + missPart;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("2121", result);
    }

    [Fact]
    public void NullConditional_Chain_EvaluatesEachLinkOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                public Box Inner;
                public string Label;
            }
            public class T
            {
                public static string Test()
                {
                    var deep = new Box { Inner = new Box { Label = "deep" } };
                    var got = deep?.Inner?.Label;
                    var cut = new Box();
                    var missing = cut?.Inner?.Label;
                    return (got ?? "none") + "|" + (missing ?? "none");
                }
            }
            """,
            "T.Test()");
        Assert.Equal("deep|none", result);
    }

    [Fact]
    public void NullConditional_MethodChainReceiver_EvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Node
            {
                public int Hits;

                public Node Bump()
                {
                    Hits = Hits + 1;
                    return this;
                }
            }
            public class T
            {
                public static int Test()
                {
                    var node = new Node();
                    var hits = node.Bump()?.Bump()?.Hits;
                    return hits ?? 0;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("2", result);
    }
}
