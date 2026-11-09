namespace TinyCs.Tcs2c.Tests;

// record class / record struct の == のうち、Nullable の struct field と
// field の無い record。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Records_NullableStructFieldsCompareByValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; }
            public record Opt(S? value);
            public record struct OptStruct(S? value, int k);
            public record E;
            public class P
            {
                public static int B(bool b) => b ? 1 : 0;
                public static void Main()
                {
                    var one = new S { N = 1 };
                    var two = new S { N = 2 };
                    Console.WriteLine("record:" + B(new Opt(one) == new Opt(one)) + ":" + B(new Opt(one) == new Opt(two))
                        + ":" + B(new Opt(null) == new Opt(null)) + ":" + B(new Opt(one) == new Opt(null)) + ":" + B(new Opt(null) == new Opt(one)));
                    Console.WriteLine("record-struct:" + B(new OptStruct(one, 1) == new OptStruct(one, 1)) + ":" + B(new OptStruct(one, 1) == new OptStruct(two, 1))
                        + ":" + B(new OptStruct(null, 1) == new OptStruct(null, 1)) + ":" + B(new OptStruct(null, 1) == new OptStruct(one, 1)));
                    Console.WriteLine("no-field:" + B(new E() == new E()));
                }
            }
            """, "P");
        Assert.Equal("""
            record:1:0:1:0:0
            record-struct:1:0:1:0
            no-field:1
            """.ReplaceLineEndings("\n"), output);
    }
}
