namespace TinyCs.Tcs2c.Tests;

// Dictionary<float, V> is outside the subset (TCS1001 UnsupportedDictionaryKey(float)). .NET's EqualityComparer<float>.Default
// treats two NaNs as equal keys (Lua tables could not use NaN as a key). Like HashSet<float> (UnsupportedHashSetElement(float)),
// it is stopped at the point of creation (new Dictionary<float, V>(), target-typed new())
public class DictFloatKeyDifferentialTests
{
    private const string DictNanKeyProgram = """
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class P
            {
                static int calls;
                static float Key(float k) { calls++; return k; }
                public static void Main()
                {
                    var d = new Dictionary<float, int>();
                    d[float.NaN] = 1;
                    d[float.NaN] = 2;
                    d[1.5f] = 3;
                    Console.WriteLine(d.Count + "," + d[float.NaN] + "," + (d.ContainsKey(float.NaN) ? 1 : 0));
                    float nan = 0f / 0f;
                    d[nan] += 10;
                    Console.WriteLine(d[float.NaN] + "," + (d.TryGetValue(nan, out var v) ? v : -1));
                    int sum = 0; int nans = 0;
                    foreach (var kv in d) { if (kv.Key != kv.Key) nans++; sum += kv.Value; }
                    Console.WriteLine(nans + "," + sum);
                    Console.WriteLine(new List<float>(d.Keys).Count(k => k != k) + "," + d.Keys.Count);
                    Console.WriteLine((d.Remove(float.NaN) ? 1 : 0) + "," + (d.Remove(float.NaN) ? 1 : 0) + "," + d.Count);
                    d.Add(float.NaN, 5);
                    Console.WriteLine(d[Key(float.NaN)] + ":" + calls);
                    var init = new Dictionary<float, int> { { float.NaN, 1 }, { 2f, 2 } };
                    var init2 = new Dictionary<float, string> { [float.NaN] = "a" };
                    init2[float.NaN] = "b";
                    Console.WriteLine(init.Count + "," + init[float.NaN] + "," + init2.Count + "," + init2[float.NaN]);
                    var list = new List<float> { 1f, float.NaN, 3f };
                    var byKey = new Dictionary<float, int>();
                    foreach (var x in list) byKey[x] = 1;
                    Console.WriteLine(byKey.Count + "," + (byKey.ContainsKey(float.NaN) ? 1 : 0));
                    Dictionary<float, int> maybe = d;
                    Console.WriteLine((maybe?.ContainsKey(float.NaN) ?? false) ? 1 : 0);
                    var z = new Dictionary<float, int>();
                    z[0f] = 1; z[-0f] = 2;
                    Console.WriteLine(z.Count + "," + z[0f]);
                }
            }
        """;

    [Fact]
    public void Dict_FloatKey_BothBackendsStopWithDiagnostic()
    {
        foreach (var run in new Func<object>[]
                 {
                     () => Backends.EmitVm([DictNanKeyProgram], "P"),
                     () => Backends.EmitC([DictNanKeyProgram], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            // One per creation point (new and initializer). `Dictionary<float, int> maybe = d;` (line 33), which only declares the type, does not count
            foreach (var at in new[] { "(10,21)", "(25,24)", "(26,25)", "(30,25)", "(35,21)" })
                Assert.Contains($"{at}: error TCS1001: unsupported syntax: UnsupportedDictionaryKey(float)", ex.Message);
            Assert.Equal(5, ex.Message.Split("UnsupportedDictionaryKey(float)").Length - 1);
        }
    }
}
