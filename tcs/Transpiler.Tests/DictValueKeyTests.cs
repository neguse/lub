namespace TinyCs.Tests;

// 値で比べる key (struct / record struct / struct の T? / record class) の
// Dictionary はサブセット外。.NET は等しい値の key を同じ key とみなすが、Lua
// table は table の同一性で引くので ContainsKey / 索引が黙って外れる。C backend
// も受け付けないので、transpiler は Lua を出さない error、check は exit 1 (#185)
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
        var result = Transpiler.TranspileWithDiagnostics([Source]);

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
        Assert.Empty(result.Lua);
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

    // 同一性で比べる class、数値・文字列・enum の key は従来どおり
    [Fact]
    public void ReferenceAndPrimitiveKeys_AreNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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
