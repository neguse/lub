namespace TinyCs.Tests;

/// <summary>
/// Tests for List/Dictionary/LINQ transpilation that maps to runtime library.
/// These tests require runtime/tinysystem.lua to be loaded.
/// </summary>
public class CollectionTests
{
    private static string TranspileAndRunWithRuntime(string csharpSource, string luaExpr)
    {
        var lua = Transpiler.Transpile(csharpSource);
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var script = $"local TinySystem = dofile(\"{runtimePath}\")\n" +
                     "List = TinySystem.List\n" +
                     "Dict = TinySystem.Dict\n" +
                     "Math = TinySystem.Math\n" +
                     $"{lua}\nprint({luaExpr})";
        return TestHelper.RunLua(script).Trim();
    }

    [Fact]
    public void List_NewEmpty()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int>();
                    return list.Count;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("0", result);
    }

    [Fact]
    public void List_NewWithInitializer()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 10, 20, 30 };
                    return list.Count;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void List_Add()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int>();
                    list.Add(1);
                    list.Add(2);
                    list.Add(3);
                    return list.Count;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void List_Indexer()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 10, 20, 30 };
                    return list[1];
                }
            }
            """,
            "T.Test()");
        Assert.Equal("20", result);
    }

    [Fact]
    public void List_Where()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4, 5 };
                    var evens = list.Where(x => x % 2 == 0).ToList();
                    return evens.Count;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void List_Select()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    var doubled = list.Select(x => x * 2).ToList();
                    return doubled[0] + doubled[1] + doubled[2];
                }
            }
            """,
            "T.Test()");
        // 2 + 4 + 6 = 12
        Assert.Equal("12", result);
    }

    [Fact]
    public void List_WhereSelectChain()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4, 5 };
                    var result = list.Where(x => x > 2).Select(x => x * 10).ToList();
                    return result[0] + result[1] + result[2];
                }
            }
            """,
            "T.Test()");
        // 30 + 40 + 50 = 120
        Assert.Equal("120", result);
    }

    [Fact]
    public void List_Any()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static bool Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    return list.Any(x => x > 2);
                }
            }
            """,
            "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void List_First()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 10, 20, 30 };
                    return list.First();
                }
            }
            """,
            "T.Test()");
        Assert.Equal("10", result);
    }

    [Fact]
    public void Dict_NewAndAccess()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
                    return dict["a"] + dict["b"];
                }
            }
            """,
            "T.Test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void Dict_Add()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int>();
                    dict.Add("x", 42);
                    return dict["x"];
                }
            }
            """,
            "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void Dict_ContainsKey()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static bool Test()
                {
                    var dict = new Dictionary<string, int> { { "key", 1 } };
                    return dict.ContainsKey("key");
                }
            }
            """,
            "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void List_Sum()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4, 5 };
                    return list.Sum();
                }
            }
            """,
            "T.Test()");
        Assert.Equal("15", result);
    }

    [Fact]
    public void List_OrderBy()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 3, 1, 2 };
                    var sorted = list.OrderBy(x => x).ToList();
                    return sorted[0] * 100 + sorted[1] * 10 + sorted[2];
                }
            }
            """,
            "T.Test()");
        Assert.Equal("123", result);
    }
}
