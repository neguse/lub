namespace TinyCs.Tests;

// A Dictionary with keys compared by value (struct / record struct / T? of struct / record class)
// is outside the subset. .NET treats keys with equal values as the same key, but Lua
// tables looked keys up by table identity, so ContainsKey / indexing silently missed. The C backend
// does not accept them either, so it is a build-stopping error, and check exits 1 (#185)
[Collection(ConsoleCollection.Name)]
public class DictValueKeyTests
{
    private const string Source = """
        using System.Collections.Generic;
        using TinySystem.Linq;
        public record struct K(int x, int y);
        public struct S { public int X; }
        public record R(int x);
        public static class P
        {
            public static int Test()
            {
                var d = new Dictionary<K, int>();
                Dictionary<R, int> r = new();
                var n = new Dictionary<S?, int>();
                var t = new Dictionary<S, int>();
                return d.Count + r.Count + n.Count + t.Count;
            }
        }
        """;

    [Fact]
    public void Transpile_ValueEqualityKeyIsBuildBlockingError()
    {
        var result = Checker.Check([Source]);

        Assert.Equal(4, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Contains(
            "error TCS1001: unsupported syntax: ValueEqualityDictionaryKey(", e));
        Assert.StartsWith("(10,17)", result.Errors[0]);
        Assert.EndsWith("ValueEqualityDictionaryKey(K)", result.Errors[0]);
        Assert.StartsWith("(11,32)", result.Errors[1]);
        Assert.EndsWith("ValueEqualityDictionaryKey(R)", result.Errors[1]);
        Assert.EndsWith("ValueEqualityDictionaryKey(S?)", result.Errors[2]);
        Assert.StartsWith("(13,17)", result.Errors[3]);
        Assert.EndsWith("ValueEqualityDictionaryKey(S)", result.Errors[3]);
    }

    [Fact]
    public void Check_ValueEqualityKeyExits1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_dict_value_key_");
        try
        {
            var input = Path.Combine(dir.FullName, "input.cs");
            File.WriteAllText(input, Source);
            var (exitCode, _, stderr) =
                ConsoleCapture.Run(() => Program.Main(["check", input]));
            Assert.Equal(1, exitCode);
            Assert.Contains(
                "input.cs(10,17): error TCS1001: unsupported syntax: ValueEqualityDictionaryKey(K)",
                stderr);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // Classes compared by identity, and number / string / enum keys, work as before
    [Fact]
    public void ReferenceAndPrimitiveKeys_AreNotDiagnosed()
    {
        var result = Checker.Check(["""
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class C { }
            public enum E { A, B }
            public struct S { public int X; }
            public static class P
            {
                public static int Test()
                {
                    var c = new Dictionary<C, int>();
                    var e = new Dictionary<E, int>();
                    var i = new Dictionary<int, S>();
                    var s = new Dictionary<string, int>();
                    var t = new Dictionary<int, S> { { 1, new S { X = 1 } } };
                    return c.Count + e.Count + i.Count + s.Count + t.Count;
                }
            }
            """]);

        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("ValueEqualityDictionaryKey"));
    }
}
