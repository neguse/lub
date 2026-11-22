namespace TinyCs.Tcs2c.Tests;

// Fields of type-parameter type in a generic record / record struct are also compared, like the synthesized Equals,
// with EqualityComparer<T>.Default (for T = float / double, two NaNs are
// equal, and +0 and -0 are also equal). The expected values are the stdout of running the same program on CoreCLR
public class GenericRecordEqualityDifferentialTests
{
    private const string GenericRecordEqualityProgram = """
        using System;
        public record Box<T>(T V);
        public record Pair<A, B>(A First, B Second);
        public record struct RBox<T>(T V);
        public record NBox<T>(T? V) where T : struct;
        public record Item(int K);
        public class P
        {
            static int B(bool b) => b ? 1 : 0;
            static bool Same<T>(T x, T y) => new Box<T>(x) == new Box<T>(y);
            public static void Main()
            {
                float z = 0.0f;
                float nan = z / z;
                float nz = -1.0f * z;
                Console.WriteLine("box:" + B(new Box<float>(nan) == new Box<float>(nan)) + ":" + B(new Box<float>(nan) != new Box<float>(nan)) + ":" + B(new Box<float>(nan) == new Box<float>(1.0f)) + ":" + B(new Box<float>(1.0f) == new Box<float>(nan)) + ":" + B(new Box<float>(z) == new Box<float>(nz)));
                Console.WriteLine("rbox:" + B(new RBox<float>(nan) == new RBox<float>(nan)) + ":" + B(new RBox<float>(nan) != new RBox<float>(nan)) + ":" + B(new RBox<float>(nan) == new RBox<float>(2.0f)) + ":" + B(new RBox<float>(z) == new RBox<float>(nz)));
                Console.WriteLine("pair:" + B(new Pair<float, int>(nan, 1) == new Pair<float, int>(nan, 1)) + ":" + B(new Pair<float, int>(nan, 1) == new Pair<float, int>(nan, 2)) + ":" + B(new Pair<int, float>(1, nan) == new Pair<int, float>(1, nan)));
                float? nn = nan;
                float? none = null;
                Console.WriteLine("nbox:" + B(new NBox<float>(nn) == new NBox<float>(nn)) + ":" + B(new NBox<float>(nn) == new NBox<float>(none)) + ":" + B(new NBox<float>(none) == new NBox<float>(none)));
                Console.WriteLine("generic:" + B(Same(nan, nan)) + ":" + B(Same(1.0f, nan)) + ":" + B(Same(3, 3)) + ":" + B(Same("a", "a")) + ":" + B(Same("a", "b")));
                Console.WriteLine("other:" + B(new Box<int>(1) == new Box<int>(1)) + ":" + B(new Box<string>("x") == new Box<string>("x")) + ":" + B(new Box<string>(null) == new Box<string>(null)) + ":" + B(new Box<Item>(new Item(1)) == new Box<Item>(new Item(1))) + ":" + B(new Box<Item>(new Item(1)) == new Box<Item>(new Item(2))));
                Console.WriteLine("nested:" + B(new Box<Box<float>>(new Box<float>(nan)) == new Box<Box<float>>(new Box<float>(nan))));
            }
        }
        """;

    [CFact]
    public void Records_GenericFieldsCompareWithEqualityComparerDefault()
    {
        Assert.Equal("""
            box:1:0:0:0:1
            rbox:1:0:0:1
            pair:1:0:1
            nbox:1:0:1
            generic:1:0:1:1:0
            other:1:1:1:1:0
            nested:1
            """.ReplaceLineEndings("\n"),
            Backends.AssertParity([GenericRecordEqualityProgram], "P"));
    }
}
