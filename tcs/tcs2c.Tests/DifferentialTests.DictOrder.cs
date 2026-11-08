namespace TinyCs.Tcs2c.Tests;

// Dictionary / HashSet の列挙は挿入順 (support-matrix §18)。C / VM / .NET (TinySystem の Dictionary) が同じ順で列挙し、
// Dictionary の foreach の途中の追加は全部の経路で止まる
public partial class DifferentialTests
{
    private const string DictOrderProgram = """
        using System;
        using System.Collections.Generic;
        public class P
        {
            public static void Main()
            {
                var d = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };
                d.Remove("a");
                d["d"] = 4;
                d["a"] = 5;
                d["b"] = 20;
                foreach (var kv in d) Console.Write(kv.Key + "=" + kv.Value + " ");
                Console.WriteLine();
                for (int i = 0; i < 40; i++) d["k" + i] = i;
                for (int i = 0; i < 40; i += 3) d.Remove("k" + i);
                d["z"] = 0;
                Console.WriteLine(string.Join(",", d.Keys));
                var sum = 0;
                foreach (var v in d.Values) sum = sum * 31 + v;
                Console.WriteLine(sum);
                var ids = new Dictionary<int, string>();
                for (int i = 10; i > 0; i--) ids[i * 7 % 11] = "v" + i;
                ids.Remove(3);
                ids[3] = "again";
                foreach (var kv in ids) Console.Write(kv.Key + ":" + kv.Value + " ");
                Console.WriteLine();
                foreach (var kv in d)
                    if (kv.Key == "b") d.Remove("c");
                Console.WriteLine(d.Count + " " + d.ContainsKey("c"));
                var s = new HashSet<int> { 5, 3, 9 };
                s.Remove(3);
                s.Add(1);
                s.Add(3);
                foreach (var x in s) Console.Write(x + " ");
                Console.WriteLine();
                foreach (var x in s)
                    if (x == 9) s.Add(100);
                Console.WriteLine(s.Count);
            }
        }
        """;

    [CFact]
    public void DictOrder_InsertionOrderOnCVmAndDotnet()
    {
        var output = Backends.AssertParity(DictOrderProgram, "P");
        Assert.StartsWith("b=20 c=3 d=4 a=5 \nb,c,d,a,k1,k2,k4,", output);
        Assert.Contains("\n5 9 1 3 \n", output);
    }

    [CFact]
    public void DictOrder_AddingDuringForeachFaults()
    {
        const string program = """
            using System;
            using System.Collections.Generic;
            public class P
            {
                public static void Main()
                {
                    var d = new Dictionary<int, int> { [1] = 1, [2] = 2 };
                    foreach (var kv in d)
                    {
                        Console.WriteLine(kv.Key);
                        d[kv.Key + 10] = 0;
                    }
                    Console.WriteLine("unreachable");
                }
            }
            """;
        Assert.Equal("1", Backends.AssertFaultParity(program, "P"));
    }
}
