namespace TinyCs.Tcs2c.Tests;

// ==/!= between object / interface / type arguments is a reference comparison (does not call record value equality).
// If the static type is a record, it is value equality. The expected values are the stdout of running the same program on CoreCLR
public class ReferenceEqualityDifferentialTests
{
    [CFact]
    public void ReferenceEquality_ObjectAndInterfaceOperands()
    {
        const string source = """
            using System;
            public record Rr(int X);
            public interface IShape { }
            public record Sq(int S) : IShape;
            public class P
            {
                static bool Same<T>(T a, T b) where T : class => a == b;
                public static void Main()
                {
                    object a = new Rr(1);
                    object b = new Rr(1);
                    Console.WriteLine(a == b);
                    Console.WriteLine(a != b);
                    Rr c = new Rr(1);
                    Rr d = new Rr(1);
                    Console.WriteLine(c == d);
                    Console.WriteLine((object)c == (object)d);
                    IShape s1 = new Sq(2);
                    IShape s2 = new Sq(2);
                    Console.WriteLine(s1 == s2);
                    Console.WriteLine(s1 != s2);
                    IShape s3 = s1;
                    Console.WriteLine(s1 == s3);
                    object e = a;
                    Console.WriteLine(e == a);
                    Console.WriteLine(a == null);
                    Console.WriteLine(Same(c, d));
                    Console.WriteLine(Same(c, c));
                }
            }
            """;
        var output = Backends.AssertParity(source, "P");
        Assert.Equal("""
            False
            True
            True
            False
            False
            True
            True
            True
            False
            False
            True
            """, output);
    }
}
