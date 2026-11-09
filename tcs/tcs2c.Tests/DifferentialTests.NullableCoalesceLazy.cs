namespace TinyCs.Tcs2c.Tests;

// nullable の `??` の右辺は左に値が無いときだけ評価する (文、式、field 初期化子、lambda のどの位置でも)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void NullableCoalesce_DoesNotEvaluateUnselectedRight()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box { public int N; }
            public class P
            {
                static int Zero = 0;
                static int? Seven = 7;
                static int InitValue = Seven ?? (1 / Zero);
                static int Pick(int? n, Box? box) => n ?? box!.N;
                public static void Main()
                {
                    int? n = 7;
                    int? none = null;
                    int zero = 0;
                    Box? box = null;
                    int[] empty = new int[0];
                    Console.WriteLine(n ?? (1 / zero));
                    Console.WriteLine(n ?? box!.N);
                    Console.WriteLine(n ?? empty[3]);
                    int a = n ?? (1 / zero);
                    Console.WriteLine(a);
                    if ((n ?? (1 / zero)) > 0) Console.WriteLine("pos");
                    Console.WriteLine(Pick(7, null));
                    Console.WriteLine(none ?? n ?? (1 / zero));
                    Console.WriteLine(InitValue);
                    Func<int?, int> f = v => v ?? (1 / zero);
                    Console.WriteLine(f(9));
                    Console.WriteLine(none ?? new Box { N = 42 }.N);
                    int two = 2;
                    Console.WriteLine(none ?? (10 / two));
                    Console.WriteLine(Pick(null, new Box { N = 3 }));
                    Console.WriteLine(none.GetValueOrDefault() + n.GetValueOrDefault());
                }
            }
            """, "P");
        Assert.Equal("7\n7\n7\n7\npos\n7\n7\n7\n9\n42\n5\n3\n7", output);
    }
}
