namespace TinyCs.Tcs2c.Tests;

// Converts TinySystem.Linq (TinySystem/Linq.cs) to Lua / C together with the user code.
// Even if a callback adds to the receiver, the scan only goes up to the Count at the time of the call, and an empty Min / Max faults.
// The expected values are the stdout of running the same program on CoreCLR (an empty Min stops with
// InvalidOperationException after "before")
public class LinqLibraryDifferentialTests
{
    [CFact]
    public void LinqLibrary_IteratesUpToCountAtCall()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class P
            {
                public static void Main()
                {
                    var list = new List<int> { 3, 1, 2 };
                    var kept = list.Where(x => { list.Add(x * 10); return x > 1; });
                    var keys = "";
                    var sorted = list.OrderBy(x => { keys += x + ","; return -x; });
                    Console.WriteLine(kept.Count + " " + list.Count + " " + sorted[0] + " " + keys);
                }
            }
            """, "P");
        Assert.Equal("2 6 30 3,1,2,30,10,20,", output);
    }

    [CTheory]
    [InlineData("new List<int>().Min()")]
    [InlineData("new List<float>().Max()")]
    [InlineData("new List<string>().Max(s => s.RuneCount())")]
    public void LinqLibrary_MinMaxOnEmpty_FaultsAfterPrecedingOutput(string call)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            using TinySystem.Text;
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine({{call}});
                }
            }
            """, "P"));
    }
}
