namespace TinyCs.Tcs2c.Tests;

// 式の本文の constructor (`C(int x) => X = x;`) は本文を文の位置の式として実行する (class と struct)
public partial class DifferentialTests
{
    [CFact]
    public void ExpressionBodiedConstructor_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public class C
            {
                public int X;
                public static int Made;
                public C(int x) => X = x;
                public C(string s) => Count(s);
                void Count(string s) { X = s == "abc" ? 3 : 0; Made++; }
            }
            public struct V
            {
                public int X;
                public V(int x) => X = x * 2;
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(new C(7).X + " " + new C("abc").X + " " + C.Made + " " + new V(4).X);
                }
            }
            """, "P");
        Assert.Equal("7 3 1 8", output);
    }
}
