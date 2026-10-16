namespace TinyCs.Tests;

public class ListSemanticTests
{
    [Fact]
    public void List_Remove()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    list.Remove(2);
                    return list.Count;
                }
            }
            """, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void List_RemoveAt()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 10, 20, 30 };
                    list.RemoveAt(0);
                    return list[0];
                }
            }
            """, "T.Test()");
        Assert.Equal("20", result);
    }

    [Fact]
    public void List_Clear()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    list.Clear();
                    return list.Count;
                }
            }
            """, "T.Test()");
        Assert.Equal("0", result);
    }

    [Fact]
    public void List_Contains()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static bool Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    return list.Contains(2);
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void List_IndexOf()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 10, 20, 30 };
                    return list.IndexOf(20);
                }
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void List_Sort()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var list = new List<int> { 3, 1, 2 };
                    list.Sort();
                    return list[0].ToString() + "," + list[1].ToString() + "," + list[2].ToString();
                }
            }
            """, "T.Test()");

        Assert.Equal("1,2,3", result);
    }

    // string の順序は ordinal (大文字が先)。
    // dotnet differential の .NET 側も InvariantGlobalization (test project) で同じ順序になる
    [Fact]
    public void List_Sort_StringOrdinal()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var list = new List<string> { "b", "A", "a", "B" };
                    list.Sort();
                    return list[0] + "," + list[1] + "," + list[2] + "," + list[3];
                }
            }
            """, "T.Test()");

        Assert.Equal("A,B,a,b", result);
    }

    [Fact]
    public void List_Sort_Comparison()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var list = new List<int> { 3, 1, 2 };
                    list.Sort((a, b) => b - a);
                    return list[0].ToString() + "," + list[1].ToString() + "," + list[2].ToString();
                }
            }
            """, "T.Test()");

        Assert.Equal("3,2,1", result);
    }
}
