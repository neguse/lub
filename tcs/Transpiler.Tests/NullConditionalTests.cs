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
}
