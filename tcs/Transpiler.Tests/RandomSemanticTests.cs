namespace TinyCs.Tests;

public class RandomSemanticTests
{
    // Random is a TinySystem-specific static API (not System.Random).
    // Define a stub so Roslyn compiles, then load runtime AFTER to overwrite the stub.
    private const string RandomStub = """
        public static class Random
        {
            public static float NextFloat() => 0f;
            public static int Next(int max) => 0;
            public static int Range(int min, int max) => 0;
        }
        """;

    private static string TranspileAndRunRandom(string csharpSource, string luaExpr)
    {
        var lua = Transpiler.Transpile(csharpSource);
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        // Load transpiled code first (defines stub Random), then runtime overwrites it
        var script = $"{lua}\n" +
                     $"local TinySystem = dofile(\"{runtimePath}\")\n" +
                     "Random = TinySystem.Random\n" +
                     $"print({luaExpr})";
        return TestHelper.RunLua(script).Trim();
    }

    [Fact]
    public void Random_NextFloat_InRange()
    {
        var result = TranspileAndRunRandom($$"""
            {{RandomStub}}
            public class T
            {
                public static bool Test()
                {
                    var f = Random.NextFloat();
                    return f >= 0.0 && f < 1.0;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Random_Next_InRange()
    {
        var result = TranspileAndRunRandom($$"""
            {{RandomStub}}
            public class T
            {
                public static bool Test()
                {
                    var n = Random.Next(10);
                    return n >= 0 && n < 10;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Random_Range_InRange()
    {
        var result = TranspileAndRunRandom($$"""
            {{RandomStub}}
            public class T
            {
                public static bool Test()
                {
                    var n = Random.Range(5, 10);
                    return n >= 5 && n <= 10;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }
}
