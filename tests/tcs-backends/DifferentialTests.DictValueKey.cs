namespace TinyCs.Tcs2c.Tests;

// A Dictionary whose keys compare by value stops before generation on both backends (#185). Lua
// tables look up keys by identity, so without stopping, ContainsKey with a key of equal value would
// return false (CoreCLR gives `rs:1:1` / `s:1` / `r:1` / `todict:1`)
public class DictValueKeyDifferentialTests
{
    private const string DictValueKeyProgram = """
        using System;
        using System.Collections.Generic;
        using TinySystem.Linq;
        public record struct K(int X, int Y);
        public struct S { public int X; }
        public record R(int X);
        public class P
        {
            public static void Main()
            {
                var d = new Dictionary<K, int>();
                d[new K(1, 2)] = 5;
                Console.WriteLine("rs:" + (d.ContainsKey(new K(1, 2)) ? 1 : 0) + ":" + d.Count);
                var s = new Dictionary<S, int>();
                s[new S { X = 1 }] = 1;
                Console.WriteLine("s:" + (s.ContainsKey(new S { X = 1 }) ? 1 : 0));
                Dictionary<R, int> r = new();
                r[new R(1)] = 1;
                r[new R(1)] = 2;
                Console.WriteLine("r:" + r.Count);
                var t = new Dictionary<K, int> { { new K(1, 1), 1 } };
                Console.WriteLine("todict:" + (t.ContainsKey(new K(1, 1)) ? 1 : 0));
            }
        }
        """;

    // C used to stop with `Dictionary key type not supported`. Both backends
    // stop with the same shared facts diagnostic before producing IL
    [Fact]
    public void Dict_ValueEqualityKey_BothBackendsStopWithDiagnostic()
    {
        foreach (var run in new Func<object>[]
                 {
                     () => Backends.EmitVm([DictValueKeyProgram], "P"),
                     () => Backends.EmitC([DictValueKeyProgram], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains("(11,17): error TCS1001: unsupported syntax: ValueEqualityDictionaryKey(K)",
                ex.Message);
            Assert.Contains("(14,17): error TCS1001: unsupported syntax: ValueEqualityDictionaryKey(S)",
                ex.Message);
            Assert.Contains("(17,32): error TCS1001: unsupported syntax: ValueEqualityDictionaryKey(R)",
                ex.Message);
            Assert.Contains("(21,17): error TCS1001: unsupported syntax: ValueEqualityDictionaryKey(K)",
                ex.Message);
        }
    }
}
