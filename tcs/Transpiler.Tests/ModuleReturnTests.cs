namespace TinyCs.Tests;

[Collection(ConsoleCollection.Name)]
public class ModuleReturnTests
{
    private const string LibSource = """
        public static class Greeter
        {
            public static string Hello(string name)
            {
                return "hello " + name;
            }
        }

        public class Counter
        {
            public int N;

            public void Bump()
            {
                N = N + 1;
            }
        }
        """;

    [Fact]
    public void Module_ReturnsTableOfEmittedTypes_RequireExposesThem()
    {
        var result = Transpiler.TranspileWithDiagnostics([LibSource],
            module: true);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.EndsWith("return {\n  Counter = Counter,\n  Greeter = Greeter,\n}\n", result.Lua);

        var luaPath = Path.Combine(Path.GetTempPath(),
            $"tcs_module_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(luaPath, result.Lua);
            var script = $"""
                local m = dofile("{luaPath}")
                local c = m.Counter.new()
                c:bump()
                c:bump()
                print(m.Greeter.hello("lua") .. " " .. c.n)
                """;
            var output = TestHelper.RunLua(script).Trim();

            Assert.Equal("hello lua 2", output);
        }
        finally
        {
            File.Delete(luaPath);
        }
    }

    [Fact]
    public void Module_ExcludesReferenceOnlyTypes()
    {
        var refSource = """
            public static class Host
            {
                public static void Ping() { }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([LibSource],
            referenceSources: [refSource], module: true);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain("Host = Host", result.Lua);
        Assert.Contains("Greeter = Greeter", result.Lua);
    }
}
