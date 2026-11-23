namespace TinyCs.Tcs2c.Tests;

// Uses Dictionary Keys / Values as TinySystem.Linq receivers (copies them into a List and passes them to the List version).
// The enumeration order is unspecified, so only order-independent operations and results sorted with OrderBy are checked.
// The expected values are the stdout of running the same program on CoreCLR
public class KeysValuesLinqDifferentialTests
{
    [CFact]
    public void KeysValues_AreLinqReceivers()
    {
        var output = Backends.AssertParity("""
            using TinySystem.Text;
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class P
            {
                public static void Main()
                {
                    var hp = new Dictionary<string, int> { { "slime", 5 }, { "bat", 3 }, { "orc", 12 } };
                    var ids = new Dictionary<int, string> { { 7, "a" }, { 2, "b" }, { 9, "c" } };
                    Console.WriteLine(hp.Values.Sum() + " " + hp.Values.Min() + " " + hp.Values.Max()
                        + " " + hp.Values.Count(v => v > 4) + " " + (hp.Values.Any(v => v > 10) ? 1 : 0)
                        + " " + (hp.Values.All(v => v > 0) ? 1 : 0) + " " + hp.Values.Sum(v => v * 2));
                    Console.WriteLine(string.Join(",", hp.Keys.OrderBy(k => k)) + " "
                        + string.Join(",", hp.Keys.Where(k => k.RuneCount() == 3).OrderBy(k => k)) + " "
                        + hp.Keys.Max() + " " + hp.Keys.MaxBy(k => k.RuneCount()));
                    Console.WriteLine(ids.Keys.Sum(k => k) + " " + ids.Keys.Min() + " " + ids.Keys.Max()
                        + " " + string.Join(",", ids.Values.Select(v => v + "!").OrderByDescending(v => v))
                        + " " + ids.Keys.OrderBy(k => -k)[0]);
                }
            }
            """, "P");
        Assert.Equal("""
            20 3 12 2 1 1 40
            bat,orc,slime bat,orc slime slime
            18 2 9 c!,b!,a! 9
            """, output);
    }
}
