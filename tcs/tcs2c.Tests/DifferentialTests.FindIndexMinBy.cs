namespace TinyCs.Tcs2c.Tests;

// List<T>.FindIndex / RemoveAll (members of the BCL List) and MinBy / MaxBy of TinySystem.Linq.
// RemoveAll calls the predicate once per element from the start and keeps the order of the remaining elements. MinBy / MaxBy pick
// the earlier element among equal keys; a float NaN key is the smallest; string keys are ordinal with null the smallest.
// The expected values are the stdout of running the same program on CoreCLR (an empty MinBy stops after "before")
public class FindIndexMinByDifferentialTests
{
    [CFact]
    public void FindIndexRemoveAll_MinByMaxBy_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public readonly record struct V(int A, float B, string N);
            public static class P
            {
                public static void Main()
                {
                    var list = new List<int> { 4, 7, 2, 7, 9 };
                    Console.WriteLine(list.FindIndex(x => x == 7) + " " + list.FindIndex(x => x > 100));
                    var seen = "";
                    var removed = list.RemoveAll(x => { seen += x + ","; return x == 7; });
                    var rest = "";
                    foreach (var x in list) rest += x + ",";
                    Console.WriteLine(removed + " " + seen + " " + rest);
                    Console.WriteLine(list.RemoveAll(x => false) + " " + new List<int>().FindIndex(x => true));
                    // Even if the predicate grows the list, compacts it with the same steps as .NET (reads the length every time)
                    var grow = new List<int> { 1, 2, 3, 2 };
                    var grown = grow.RemoveAll(x => { if (x == 3) grow.Add(9); return x == 2; });
                    var growRest = "";
                    foreach (var x in grow) growRest += x + ",";
                    Console.WriteLine(grown + " " + growRest);
                    // Does not stop even if the predicate empties the list (.NET recounts the removed elements, but Lua / C just return the count)
                    var shrink = new List<int> { 1, 2, 3, 4 };
                    var shrunk = shrink.RemoveAll(x => { if (x == 3) shrink.Clear(); return x == 2; });
                    Console.WriteLine(shrunk < 0);
                    float zero = 0f;
                    var vs = new List<V> { new V(2, 1.5f, "b"), new V(1, zero / zero, "a"), new V(1, 0.5f, "c"), new V(3, 2.5f, "a") };
                    Console.WriteLine(vs.MinBy(v => v.A).N + vs.MaxBy(v => v.A).N + vs.MinBy(v => v.B).N
                        + vs.MaxBy(v => v.B).N + vs.MinBy(v => v.N).A + vs.MaxBy(v => v.N).N);
                    var keys = "";
                    vs.MaxBy(v => { keys += v.A; return v.A; });
                    Console.WriteLine(keys);
                }
            }
            """, "P");
        Assert.Equal("""
            1 -1
            2 4,7,2,7,9, 4,2,9,
            0 -1
            2 1,3,9,
            True
            aaaa1c
            2113
            """, output);
    }

    [CTheory]
    [InlineData("new List<int>().MinBy(x => x)")]
    [InlineData("new List<string>().MaxBy(s => s.RuneCount()).RuneCount()")]
    public void MinByMaxBy_OnEmpty_FaultsAfterPrecedingOutput(string call)
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
