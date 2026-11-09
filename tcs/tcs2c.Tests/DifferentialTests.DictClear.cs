namespace TinyCs.Tcs2c.Tests;

// Dictionary.Clear は全ての要素を消し、同じ Dictionary にまた入れられる。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void DictClear_RemovesAllEntries()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Holder { public Dictionary<string, float> D = new Dictionary<string, float>(); }
            public static class P
            {
                public static void Main()
                {
                    var d = new Dictionary<int, int>();
                    d[1] = 2;
                    d[3] = 4;
                    d.Clear();
                    Console.WriteLine(d.Count + "," + d.ContainsKey(1));
                    d[5] = 6;
                    Console.WriteLine(d.Count + "," + d[5]);
                    var h = new Holder();
                    h.D["a"] = 1.5f;
                    h.D["b"] = 2f;
                    h.D.Clear();
                    Console.WriteLine(h.D.Count);
                    h.D["c"] = 3f;
                    Console.WriteLine(h.D.Count + "," + h.D.ContainsKey("a"));
                }
            }
            """, "P");
        Assert.Equal("0,False\n1,6\n0\n1,False", output);
    }
}
