namespace TinyCs.Tcs2c.Tests;

// pattern の括弧は意味を持たない: switch 式 / 文、is の照合と designation は括弧を外した形と同じ。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void ParenthesizedPattern_IsTransparent()
    {
        var output = Backends.AssertParity("""
            using System;
            public interface Shape { }
            public class Sq : Shape { }
            public class Ci : Shape { }
            public class Tri : Shape { }
            public class Oth : Shape { }
            public class P
            {
                static int G(Shape v) => v switch { (Sq or Ci) or Tri => 1, _ => 0 };
                static int D(Shape v) => v switch { (Sq s) => s != null ? 3 : -1, ((Ci)) => 7, (_) => 0 };
                static int H(int v)
                {
                    switch (v)
                    {
                        case (1 or 2) or 3: return 1;
                        case (> 10): return 10;
                        default: return 0;
                    }
                }
                static int L(Shape v)
                {
                    switch (v)
                    {
                        case (Sq s): return s != null ? 3 : -1;
                        case (Ci or Tri): return 5;
                        default: return 0;
                    }
                }
                public static void Main()
                {
                    Console.WriteLine(G(new Sq()) + ":" + G(new Ci()) + ":" + G(new Tri()) + ":" + G(new Oth()));
                    Console.WriteLine(D(new Sq()) + ":" + D(new Ci()) + ":" + D(new Oth()));
                    Console.WriteLine(H(1) + ":" + H(3) + ":" + H(11) + ":" + H(5));
                    Console.WriteLine(L(new Sq()) + ":" + L(new Tri()) + ":" + L(new Oth()));
                    Shape x = new Ci();
                    Console.WriteLine((x is (Sq or Ci)) + ":" + (x is ((Tri))) + ":" + (x is not (Sq or Tri)));
                    int n = 4;
                    Console.WriteLine(n is (4 or 5));
                    Shape y = new Sq();
                    if (y is (Sq s)) Console.WriteLine(s != null);
                }
            }
            """, "P");
        Assert.Equal("1:1:1:0\n3:7:0\n1:1:10:0\n3:5:0\nTrue:False:True\nTrue\nTrue", output);
    }
}
