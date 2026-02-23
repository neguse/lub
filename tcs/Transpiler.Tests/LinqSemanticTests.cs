namespace TinyCs.Tests;

public class LinqSemanticTests
{
    [Fact]
    public void Linq_All()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static bool Test()
                {
                    var list = new List<int> { 2, 4, 6 };
                    return list.All(x => x % 2 == 0);
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Linq_FirstOrDefault_Found()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    return list.FirstOrDefault(x => x > 1);
                }
            }
            """, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void Linq_Min()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 5, 3, 8, 1 };
                    return list.Min();
                }
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void Linq_Max()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 5, 3, 8, 1 };
                    return list.Max();
                }
            }
            """, "T.Test()");
        Assert.Equal("8", result);
    }
}
