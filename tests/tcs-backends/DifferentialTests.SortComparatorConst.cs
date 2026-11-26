namespace TinyCs.Tcs2c.Tests;

// Sort / OrderBy comparison functions read pointer elements (string, record) without dropping const.
// Compiles the generated C with -Werror. The expected values are the stdout of running the same program on CoreCLR
public class SortComparatorConstDifferentialTests
{
    [CFact]
    public void SortComparators_OfPointerElements_CompileWithoutWarnings()
    {
        var output = Backends.RunC(["""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public record R(string Name, int N);
            public class P
            {
                public static void Main()
                {
                    var s = new List<string> { "b", "c", "a" };
                    s.Sort();
                    Console.WriteLine(string.Join(",", s));
                    var rs = new List<R> { new R("x", 2), new R("y", 1), new R("z", 3) };
                    rs.Sort((a, b) => a.N - b.N);
                    Console.WriteLine(string.Join(",", rs.Select(r => r.Name)));
                    Console.WriteLine(string.Join(",", rs.OrderBy(r => r.Name).Select(r => r.Name + r.N)));
                    Console.WriteLine(string.Join(",", rs.OrderByDescending(r => r.Name).Select(r => r.Name + r.N)));
                    Console.WriteLine(string.Join(",", s.OrderBy(x => x)));
                }
            }
            """], "P", extraFlags: ["-Werror"]);
        Assert.Equal("a,b,c\ny,x,z\nx2,y1,z3\nz3,y1,x2\na,b,c", output);
    }
}
