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
}
