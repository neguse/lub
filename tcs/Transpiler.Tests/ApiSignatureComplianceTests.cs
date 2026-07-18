namespace TinyCs.Tests;

// T138: supported API allowlist は完全シグネチャ単位。メソッド名が同じでも
// Lua runtime が実装していない overload は TCS1002 になる。
public class ApiSignatureComplianceTests
{
    private static string Wrap(string statement) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;

        public class Api
        {
            public static void Run()
            {
                var list = new List<int> { 1, 2, 3 };
                var names = new List<string> { "a", "b" };
                var dict = new Dictionary<string, int> { { "a", 1 } };
                var s = "a,b";
                {{statement}}
            }
        }
        """;

    [Theory]
    [InlineData("list.Select((x, i) => x + i);", "System.Linq.Enumerable.Select")]
    [InlineData("list.OrderBy(x => x, Comparer<int>.Default);", "System.Linq.Enumerable.OrderBy")]
    [InlineData("list.FirstOrDefault(-1);", "System.Linq.Enumerable.FirstOrDefault")]
    [InlineData("names.ToDictionary(x => x, StringComparer.Ordinal);", "System.Linq.Enumerable.ToDictionary")]
    [InlineData("s.Contains('a');", "string.Contains")]
    [InlineData("s.Contains(\"a\", StringComparison.Ordinal);", "string.Contains")]
    [InlineData("s.StartsWith(\"a\", StringComparison.Ordinal);", "string.StartsWith")]
    [InlineData("s.Split(',');", "string.Split")]
    [InlineData("s.Split(\",\", StringSplitOptions.None);", "string.Split")]
    [InlineData("s.Split(',', ';');", "string.Split")]
    [InlineData("var c = new List<int>(4);", "List<T>")]
    [InlineData("var c = new Dictionary<string, int>(4);", "Dictionary<TKey, TValue>")]
    [InlineData("dict.Remove(\"a\", out var removed);", "Dictionary<TKey, TValue>.Remove")]
    [InlineData("list.Sort(Comparer<int>.Default);", "List<T>.Sort")]
    [InlineData("Math.Round(1.55f, MidpointRounding.AwayFromZero);", "System.Math.Round")]
    [InlineData("string.Join('-', \"a\", \"b\");", "string.Join")]
    public void UnimplementedOverload_ReportsTcs1002(string statement,
        string expectedApiName)
    {
        var result = Transpiler.TranspileWithDiagnostics([Wrap(statement)]);

        var warning = Assert.Single(result.Warnings,
            w => w.Contains(TinyCsDiagnosticIds.UnsupportedApi));
        Assert.Contains(expectedApiName, warning);
    }

    [Theory]
    [InlineData("list.Sort(); list.Sort((a, b) => b - a);")]
    [InlineData("dict.TryGetValue(\"a\", out var value);")]
    [InlineData("var r = Math.Round(1.234f, 2) + Math.Log(8.0f, 2.0f);")]
    [InlineData("var parts = s.Split(\",\"); var all = s.Split();")]
    [InlineData("var i = s.IndexOf(\"a\", 1); var sub = s.Substring(1, 2);")]
    [InlineData("var j = string.Join(\",\", names) + string.Join(\",\", \"x\", \"y\");")]
    [InlineData("var byName = names.ToDictionary(x => x); var byLen = names.ToDictionary(x => x, x => x.Length);")]
    [InlineData("var f = list.First() + list.Count(x => x > 1) + list.Min() + list.Sum();")]
    [InlineData("var any = list.Any() || list.Any(x => x > 2);")]
    [InlineData("var shortest = names.Min(x => x.Length);")]
    [InlineData("list[0] = list[1]; dict[\"a\"] = dict[\"a\"] + 1;")]
    public void ImplementedOverload_PassesWithoutTcs1002(string statement)
    {
        var result = Transpiler.TranspileWithDiagnostics([Wrap(statement)]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains(TinyCsDiagnosticIds.UnsupportedApi));
    }
}
