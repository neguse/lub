namespace TinyCs.Tcs2c.Tests;

// 補間の穴の配置幅 ({式,幅})。幅は空白の数に上限なく埋め、raw の補間文字列でも通常の補間文字列と同じに読む。
// 幅の , は穴の式を読んだ後ろの , で、式の中の , (配列の初期化子、generic の型引数) は幅にしない
public partial class DifferentialTests
{
    [CFact]
    public void Interpolation_AlignmentPadsAnyNumberOfSpaces()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static string Spaces(int n)
                {
                    string s = "";
                    for (int i = 0; i < n; i++) s += " ";
                    return s;
                }

                public static void Main()
                {
                    Console.WriteLine($"{"x",514}" == $"{"x",513}");
                    Console.WriteLine($"{"x",513}" == Spaces(512) + "x");
                    Console.WriteLine($"{"x",600}" == Spaces(599) + "x");
                    Console.WriteLine($"{"x",-600}" == "x" + Spaces(599));
                    Console.WriteLine($"{"",513}" == Spaces(513));
                    Console.WriteLine($"{5,600:D3}" == Spaces(597) + "005");
                    Console.WriteLine($"{1.5f,-1000}|" == "1.5" + Spaces(997) + "|");
                    Console.WriteLine($"[{"ab",4}][{"ab",-4}][{"ab",1}]");
                }
            }
            """, "P");
        Assert.Equal("False\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n[  ab][ab  ][ab]", output);
    }

    // 幅 int.MinValue の結果は string の長さに収まらず、.NET は例外、tcs は確保の前に fault で止まる
    [CFact]
    public void Interpolation_AlignmentBeyondStringLengthFaults()
    {
        var output = Backends.AssertFaultParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine($"{"x",-2147483648}");
                    Console.WriteLine("after");
                }
            }
            """, "P");
        Assert.Equal("before", output);
    }

    [CFact]
    public void Interpolation_RawStringAlignment()
    {
        var output = Backends.AssertParity(""""
            using System;
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine($"""[{42,5}]""");
                    Console.WriteLine($"""[{42,-5}]""");
                    Console.WriteLine($"""[{42,7:D4}]""");
                    Console.WriteLine($$"""[{{42,5}}]""");
                    Console.WriteLine($$"""{[{{42,-3}}]}""");
                    Console.WriteLine($"""[{42,0}][{42}][{"ab" , -4}][{1 + 2,3}]""");
                    Console.WriteLine($"""
                        [{42,5}]
                          [{"a",-2}]
                        """);
                }
            }
            """", "P");
        Assert.Equal("[   42]\n[42   ]\n[   0042]\n[   42]\n{[42 ]}\n[42][42][ab  ][  3]\n[   42]\n  [a ]", output);
    }

    [CFact]
    public void Interpolation_CommaInsideHoleExpressionIsNotAlignment()
    {
        var output = Backends.AssertParity(""""
            using System;
            public class P
            {
                static int Second<A, B>(A a, B b) => 2;

                public static void Main()
                {
                    Console.WriteLine($"[{new int[] { 1, 2 }.Length,3}]");
                    Console.WriteLine($$"""[{{new int[] { 1, 2, 3 }.Length,3}}]""");
                    Console.WriteLine($"[{Second<int, int>(1, 2),3}][{Second<int, string>(1, "a")}]");
                    Console.WriteLine($"""[{Second<int, int>(1, 2),-3}]""");
                    Console.WriteLine($"[{Math.Max(1, 2),3:D2}]");
                }
            }
            """", "P");
        Assert.Equal("[  2]\n[  3]\n[  2][2]\n[2  ]\n[ 02]", output);
    }
}
