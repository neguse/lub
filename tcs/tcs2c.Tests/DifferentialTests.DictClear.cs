namespace TinyCs.Tcs2c.Tests;

// Dictionary.Clear removes all elements, and the same Dictionary can be filled again. The expected values are the stdout of running the same program on CoreCLR
public class DictClearDifferentialTests
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
