namespace TinyCs.Tests;

public class TinySystemFacadeTests
{
    [Fact]
    public void RandomFacade_CompilesWithoutStub()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var f = TinySystem.Random.NextFloat();
                    return f >= 0.0 && f < 1.0;
                }
            }
            """,
            "tostring(T.Test())");

        Assert.Equal("true", result);
    }

    [Fact]
    public void MathFacade_MapsToRuntimeGlobal()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    return TinySystem.Math.Clamp(12, 0, 10) == 10
                        && TinySystem.Math.PI > 3.14;
                }
            }
            """,
            "tostring(T.Test())");

        Assert.Equal("true", result);
    }

    [Fact]
    public void StringFacade_MapsToRuntimeGlobal()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    return TinySystem.String.Replace("hello world", "world", "lua");
                }
            }
            """,
            "T.Test()");

        Assert.Equal("hello lua", result);
    }

    [Fact]
    public void ListFacade_MapsToRuntimeGlobal()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var list = new System.Collections.Generic.List<int> { 1, 2, 3 };
                    return TinySystem.List.Contains(list, 2);
                }
            }
            """,
            "tostring(T.Test())");

        Assert.Equal("true", result);
    }

    [Fact]
    public void DictFacade_MapsToRuntimeGlobal()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var dict = new System.Collections.Generic.Dictionary<string, int>
                    {
                        { "a", 1 }
                    };
                    return TinySystem.Dict.ContainsKey(dict, "a");
                }
            }
            """,
            "tostring(T.Test())");

        Assert.Equal("true", result);
    }
}
