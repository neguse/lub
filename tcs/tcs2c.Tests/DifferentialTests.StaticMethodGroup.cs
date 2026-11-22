namespace TinyCs.Tcs2c.Tests;

// A method group of a static method (`Func<int, int> f = Ext.Twice;`), even when qualified by the type name
// (even for extension methods), refers to the same function as a call. The expected values are the stdout of running the same
// program on CoreCLR
public class StaticMethodGroupDifferentialTests
{
    [CFact]
    public void MethodGroup_QualifiedStaticMethod()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            namespace Game.Util
            {
                public static class NsMath
                {
                    public static int Square(int x) { return x * x; }
                }
            }
            public static class Ext
            {
                public static int Twice(this int x) { return x * 2; }
            }
            public static class Calc
            {
                public static int AddOne(int x) { return x + 1; }
                public static int Apply(Func<int, int> f, int v) { return f(v); }
            }
            public static class P
            {
                static int Neg(int x) { return -x; }
                public static void Main()
                {
                    Func<int, int> f = Ext.Twice;
                    Func<int, int> g = Calc.AddOne;
                    Func<int, int> h = Game.Util.NsMath.Square;
                    Func<int, int> n = Neg;
                    Func<int, int> pn = P.Neg;
                    Console.WriteLine(f(5) + ":" + g(5) + ":" + h(5) + ":" + n(5) + ":" + pn(5));
                    Console.WriteLine(Calc.Apply(Ext.Twice, 7) + ":" + Calc.Apply(Calc.AddOne, 7));
                    var l = new List<int> { 1, 2, 3 };
                    Console.WriteLine(l.Select(Ext.Twice).Sum() + ":" + l.Count(x => Calc.AddOne(x) > 2));
                    Func<int, int> chain = x => f(g(x));
                    Console.WriteLine(chain(3));
                }
            }
            """, "P");
        Assert.Equal("""
            10:6:25:-5:-5
            14:8
            12:2
            8
            """.ReplaceLineEndings("\n"), output);
    }
}
