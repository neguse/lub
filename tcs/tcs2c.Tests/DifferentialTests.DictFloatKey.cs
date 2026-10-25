namespace TinyCs.Tcs2c.Tests;

// Dictionary<float, V> の NaN key。.NET の EqualityComparer<float>.Default は
// NaN 同士を等しい key とみなす。Lua の期待値は同じ program を CoreCLR で実行
// した stdout。C backend は float の key を対応外として生成前に止める
public partial class DifferentialTests
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
    public void Dict_NaNKey_Lua_MatchesDotnet()
    {
        Assert.Equal("2,2,1\n12,12\n1,15\n1,2\n1,0,1\n5:1\n2,1,1,b\n3,1\n1\n1,2",
            Backends.RunLua([DictNanKeyProgram], "P"));
    }

    [Fact]
    public void Dict_FloatKey_C_StopsWithDiagnostic()
    {
        var ex = Assert.Throws<Tcs2cException>(
            () => Backends.EmitC([DictNanKeyProgram], "P"));
        Assert.Contains("Dictionary key type not supported", ex.Message);
    }

    // float 以外の静的型の key に入った NaN は対象外 (Lua table に置けず止まる)。
    // 文字列 "NaN" の key と取り違えて黙って 1 つにしない (CoreCLR は 2)
    [Fact]
    public void Dict_ObjectKeyNaN_IsNotMergedWithStringKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Backends.RunLua(["""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class P
            {
                public static void Main()
                {
                    var objs = new Dictionary<object, int>();
                    objs["NaN"] = 1;
                    objs[float.NaN] = 1;
                    Console.WriteLine(objs.Count);
                }
            }
            """], "P"));
        Assert.Contains("table index is NaN", ex.Message);
    }
}
