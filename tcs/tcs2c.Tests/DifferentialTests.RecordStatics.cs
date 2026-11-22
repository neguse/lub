namespace TinyCs.Tcs2c.Tests;

// static fields / static auto properties of record classes behave the same as in classes (pre-zero +
// initialization order across types),
// and consts of struct / record struct are folded into values at their use sites (#154). The expected values are the
// stdout of running the same program on CoreCLR
public class RecordStaticsDifferentialTests
{
    [CFact]
    public void Records_StaticFieldsBehaveLikeClassStatics()
    {
        var output = Backends.AssertParity("""
            using System;
            public record R(int a)
            {
                public static int Y = 2;
                public static readonly string Name = "r";
                public static int Count;
                public static R Zero = new R(0);
                public const int K = 7;
                public static int Counter { get; set; } = Y + 2;
                public static string Label { get; } = Name + "!";
                public static int Next() { Count = Count + 1; return Count; }
            }
            public record D(int a, int b) { public static int Z = R.Y + Holder.W; }
            public static class Holder { public static int W = R.Y * 5; }
            public struct S { public int V; public const int K = 5; public int Scaled() { return V * K; } }
            public record struct RS(int a) { public const float F = 1.5f; }
            public static class P
            {
                public static int B(bool b) => b ? 1 : 0;
                public static void Main()
                {
                    Console.WriteLine("init:" + R.Y + ":" + R.Name + ":" + R.K + ":" + R.Count + ":" + D.Z + ":" + Holder.W);
                    Console.WriteLine("zero:" + B(R.Zero == new R(0)) + ":" + R.Zero.a);
                    R.Y = R.Y + 1;
                    Console.WriteLine("write:" + R.Y);
                    Console.WriteLine("method:" + (R.Next() + R.Next()) + ":" + R.Count);
                    R.Counter = R.Counter + 10;
                    Console.WriteLine("prop:" + R.Counter + ":" + R.Label);
                    Console.WriteLine("eq:" + B(new D(1, 2) == new D(1, 2)));
                    var s = new S { V = 2 };
                    Console.WriteLine("struct-const:" + S.K + ":" + s.Scaled() + ":" + RS.F * 2.0f);
                }
            }
            """, "P");
        Assert.Equal("""
            init:2:r:7:0:12:10
            zero:1:0
            write:3
            method:3:2
            prop:14:r!
            eq:1
            struct-const:5:10:3
            """.ReplaceLineEndings("\n"), output);
    }
}
