namespace TinyCs.Tcs2c.Tests;

// record / record struct の合成 Equals は float field を float.Equals で比べる
// (NaN 同士は等しく、+0 と -0 も等しい)。struct の ValueType.Equals と
// T? の Equals も同じ。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Records_FloatFieldsCompareWithFloatEquals()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public record F(float V);
            public record G(float V, int K);
            public record H(float V) : F(V);
            public record NF(float? V);
            public record struct RS(float V);
            public struct S { public float V; }
            public record WithS(S Item);
            public record Outer(F Inner);
            public record struct RSN(RS Item);
            public class P
            {
                public static int B(bool b) => b ? 1 : 0;
                public static void Main()
                {
                    float z = 0.0f;
                    float nan = z / z;
                    float nz = -1.0f * z;
                    Console.WriteLine("record-nan:" + B(new F(nan) == new F(nan)) + ":" + B(new F(nan) != new F(nan)) + ":" + B(new F(nan) == new F(1.0f)) + ":" + B(new F(1.0f) == new F(nan)));
                    Console.WriteLine("record-zero:" + B(new F(z) == new F(nz)) + ":" + B(new F(nz) != new F(z)));
                    Console.WriteLine("record-multi:" + B(new G(nan, 1) == new G(nan, 1)) + ":" + B(new G(nan, 1) == new G(nan, 2)));
                    Console.WriteLine("record-derived:" + B(new H(nan) == new H(nan)));
                    float? nn = nan;
                    float? none = null;
                    Console.WriteLine("record-nullable:" + B(new NF(nn) == new NF(nn)) + ":" + B(new NF(nn) == new NF(none)) + ":" + B(new NF(none) == new NF(none)) + ":" + B(nn == nn));
                    Console.WriteLine("record-struct:" + B(new RS(nan) == new RS(nan)) + ":" + B(new RS(nan) != new RS(nan)) + ":" + B(new RS(z) == new RS(nz)));
                    Console.WriteLine("nested:" + B(new WithS(new S { V = nan }) == new WithS(new S { V = nan })) + ":" + B(new Outer(new F(nan)) == new Outer(new F(nan))) + ":" + B(new RSN(new RS(nan)) == new RSN(new RS(nan))));
                    var list = new List<S>();
                    list.Add(new S { V = nan });
                    Console.WriteLine("struct-list:" + B(list.Contains(new S { V = nan })) + ":" + list.IndexOf(new S { V = nan }));
                    var rl = new List<F>();
                    rl.Add(new F(nan));
                    Console.WriteLine("record-list:" + B(rl.Contains(new F(nan))));
                    var nl = new List<float?>();
                    nl.Add(nan);
                    Console.WriteLine("nullable-list:" + B(nl.Contains(nan)));
                }
            }
            """, "P");
        Assert.Equal("""
            record-nan:1:0:0:0
            record-zero:1:0
            record-multi:1:0
            record-derived:1
            record-nullable:1:0:1:0
            record-struct:1:0:1
            nested:1:1:1
            struct-list:1:0
            record-list:1
            nullable-list:1
            """.ReplaceLineEndings("\n"), output);
    }
}
