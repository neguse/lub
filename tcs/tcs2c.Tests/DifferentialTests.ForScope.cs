namespace TinyCs.Tcs2c.Tests;

// numeric for に落ちない for (while に脱糖する for) のループ変数は、その for
// の中だけの変数。lambda の中で外の local と同じ名前のループ変数を宣言しても、
// ループの後の参照は外の local を捕捉する (#182)。宣言子が複数・入れ子・
// continue・break・ループ変数の捕捉も見る。期待値は同じ program を CoreCLR で
// 実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Closures_WhileForVariableShadowsCapturedOuterLocal()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Fs
            {
                public static void Main()
                {
                    int i = 9;
                    Func<int> f1 = () => { var s = 0; for (int i = 0; i < 4; i += 2) s += i; return s + i; };
                    int j = 9;
                    Func<int> f2 = () => { for (int j = 0; j < 4; j += 2) { } return j; };
                    int a = 1, b = 2;
                    Func<int> f3 = () => { var s = 0; for (int a = 0, b = 10; a < b; a += 3, b--) s += a * b; return s * 100 + a * 10 + b; };
                    int k = 7;
                    Func<int> f4 = () =>
                    {
                        var s = 0;
                        for (int k = 0; k < 6; k += 1)
                        {
                            if (k % 2 == 0) continue;
                            for (int i = k; i > 0; i -= 2) s += i;
                            s += i;
                        }
                        return s * 10 + k;
                    };
                    int n = 3;
                    Func<Func<int>> f5 = () =>
                    {
                        var fs = new List<Func<int>>();
                        for (int n = 0; n < 3; n = n + 1) fs.Add(() => n);
                        return () => fs[0]() + fs.Count * 10 + n * 100;
                    };
                    int m = 4;
                    Func<int> f6 = () => { var s = 0; for (int m = 1; m < 50; m *= 2) { if (m == 8) continue; s += m; } return s * 10 + m; };
                    var l = new List<int> { 1, 2, 3 };
                    int c = 2;
                    Func<int> f7 = () => { var s = 0; for (int c = 0; c < l.Count; c++) { l.Add(0); if (l.Count > 6) break; s += c; } return s * 10 + c; };
                    Console.WriteLine(f1() + ":" + f2() + ":" + f3() + ":" + f4() + ":" + f5()() + ":" + f6() + ":" + f7());
                    Console.WriteLine(i + ":" + j + ":" + a + b + ":" + k + ":" + n + ":" + m + ":" + c);
                }
            }
            """, "Fs");
        Assert.Equal("""
            11:9:7512:417:333:554:32
            9:9:12:7:3:4:2
            """.ReplaceLineEndings("\n"), output);
    }
}
