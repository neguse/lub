namespace TinyCs.Tcs2c.Tests;

// Sort / OrderBy の比較関数は、pointer の要素 (string、record) でも const を落とさずに読む。
// 生成 C を -Werror で compile する。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
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
