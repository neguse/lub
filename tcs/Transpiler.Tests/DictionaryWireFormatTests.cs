namespace TinyCs.Tests;

// Dictionary<string, object> を --ref の関数に渡す program と、その Count と foreach の意味
public class DictionaryWireFormatTests
{
    private const string HostRefSource = """
        public class Handle
        {
            public int id;
        }

        public static class Host
        {
            public static Handle? create() => null;
            public static string inspect(
                System.Collections.Generic.Dictionary<string, object> opts) => "";
        }
        """;

    [Fact]
    public void HeterogeneousDictionary_PassedToRefFunction_IsAccepted()
    {
        var source = """
            using System.Collections.Generic;

            public static class Game
            {
                public static string Run()
                {
                    var handle = Host.create();
                    var opts = new Dictionary<string, object>
                    {
                        { "width", 640.0f },
                        { "title", "demo" },
                        { "vsync", true },
                        { "handle", handle! },
                        { "nested", new Dictionary<string, object> { { "x", 1.5f } } },
                        { "list", new List<float> { 1.0f, 2.0f } },
                    };
                    opts["late"] = 1;
                    opts.Add("added", "a");
                    return Host.inspect(opts);
                }
            }
            """;

        var result = Checker.Check([source], null, [HostRefSource]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void HeterogeneousDictionary_CountIsComputedNotStored()
    {
        var result = TestHelper.CompileAndRunVm("""
            using System.Collections.Generic;

            public class T
            {
                public static string Test()
                {
                    var d = new Dictionary<string, object>();
                    d.Add("num", 1.5f);
                    d["str"] = "s";
                    d.Add("flag", true);
                    var before = d.Count;
                    d.Remove("flag");
                    return $"{before}:{d.Count}:{d.ContainsKey("str")}:{d.ContainsKey("flag")}";
                }
            }
            """, "T.Test()");
        Assert.Equal("3:2:True:False", result);
    }

    [Fact]
    public void HeterogeneousDictionary_ForeachSeesOnlyUserEntries()
    {
        var result = TestHelper.CompileAndRunVm("""
            using System.Collections.Generic;

            public class T
            {
                public static int Test()
                {
                    var d = new Dictionary<string, object>
                    {
                        { "a", 1 },
                        { "b", "x" },
                        { "c", true },
                    };
                    var n = 0;
                    foreach (var entry in d)
                    {
                        n++;
                    }
                    return n;
                }
            }
            """, "T.Test()");
        Assert.Equal("3", result);
    }
}
