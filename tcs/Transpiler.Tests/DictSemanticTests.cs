namespace TinyCs.Tests;

public class DictSemanticTests
{
    [Fact]
    public void Dict_Count()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
                    return dict.Count;
                }
            }
            """, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void Dict_Remove()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
                    dict.Remove("a");
                    return dict.Count;
                }
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void Dict_Keys()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int> { { "x", 1 }, { "y", 2 } };
                    var keys = dict.Keys;
                    return keys.Count;
                }
            }
            """, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void Dict_Values()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int> { { "x", 10 }, { "y", 20 } };
                    var vals = dict.Values;
                    return vals.Count;
                }
            }
            """, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void Dict_TryGetValue_Found_AssignsOutVar()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var dict = new Dictionary<string, int> { { "x", 42 } };
                    var found = dict.TryGetValue("x", out var value);
                    return found.ToString() + ":" + value.ToString();
                }
            }
            """, "T.Test()");

        Assert.Equal("true:42", result);
    }

    [Fact]
    public void Dict_TryGetValue_Missing_AssignsDefaultToOutVar()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var dict = new Dictionary<string, int> { { "x", 42 } };
                    var found = dict.TryGetValue("missing", out var value);
                    return found.ToString() + ":" + value.ToString();
                }
            }
            """, "T.Test()");

        Assert.Equal("false:0", result);
    }

    [Fact]
    public void Dict_TryGetValue_IfCondition_OutVarVisibleInThen()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int> { { "x", 7 } };
                    if (dict.TryGetValue("x", out var value))
                    {
                        return value;
                    }
                    return 0;
                }
            }
            """, "T.Test()");

        Assert.Equal("7", result);
    }

    [Fact]
    public void Dict_TryGetValue_ExistingOutVariable()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var dict = new Dictionary<string, string> { { "x", "ok" } };
                    string value;
                    var found = dict.TryGetValue("missing", out value);
                    return found.ToString() + ":" + (value == null ? "nil" : value);
                }
            }
            """, "T.Test()");

        Assert.Equal("false:nil", result);
    }
}
