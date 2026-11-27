namespace TinyCs.Tcs2c.Tests;

// A for lowered to a numeric for (IlNumericFor) gives the same result as the for desugared to while.
// Writes to the upper bound variable and the control variable (a tuple deconstruction target, a write from another lambda) are not missed.
// The upper bound comparison stays C#'s i < limit / i <= limit (the int.MinValue / int.MaxValue boundaries and the wrapping increment).
// A for whose control variable and comparison are not int does not become a numeric for.
// The expected value is the stdout of the same program run on CoreCLR
public class NumericForDifferentialTests
{
    [CFact]
    public void NumericFor_BodyDeconstructionWritesBoundOrCounter()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                static string Bound()
                {
                    int limit = 4, visits = 0;
                    for (int i = 0; i < limit; i++)
                    {
                        visits++;
                        (limit, _) = (1, 0);
                    }
                    return visits + "," + limit;
                }
                static string NestedBound()
                {
                    int limit = 5, visits = 0, x = 0;
                    for (int i = 0; i <= limit; i++)
                    {
                        visits++;
                        (x, (limit, _)) = (i, (2, 0));
                    }
                    return visits + "," + limit + "," + x;
                }
                static int Counter()
                {
                    int visits = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        visits++;
                        if (visits == 1) (i, _) = (1, 0);
                    }
                    return visits;
                }
                static int CounterSwap()
                {
                    int visits = 0, j = 10;
                    for (int i = 0; i < 5; i += 1)
                    {
                        visits++;
                        (i, j) = (j, i);
                    }
                    return visits * 100 + j;
                }
                // Control: a prefix increment (desugared to while) and a non-deconstructing assignment
                static string Controls()
                {
                    int limit = 4, a = 0;
                    for (int i = 0; i < limit; ++i) { a++; (limit, _) = (1, 0); }
                    int limit2 = 4, b = 0;
                    for (int i = 0; i < limit2; i++) { b++; limit2 = 1; }
                    int c = 0;
                    for (int i = 0; i < 3; i++) { c++; if (c == 1) i = 1; }
                    return a + "," + b + "," + c;
                }
                public static void Main()
                {
                    Console.WriteLine(Bound());
                    Console.WriteLine(NestedBound());
                    Console.WriteLine(Counter());
                    Console.WriteLine(CounterSwap());
                    Console.WriteLine(Controls());
                }
            }
            """, "P");
        Assert.Equal("1,1\n3,2,2\n2\n100\n1,1,2", output);
    }

    // Another lambda called from the body rewrites the upper bound variable
    [CFact]
    public void NumericFor_BoundWrittenThroughAnotherLambda()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                static int InLambda()
                {
                    int n = 3;
                    Action shrink = () => { n = 1; };
                    Func<int> count = () =>
                    {
                        int c = 0;
                        for (int i = 0; i < n; i++) { c++; shrink(); }
                        return c;
                    };
                    return count();
                }
                static int InMethod()
                {
                    int n = 3, c = 0;
                    Action shrink = () => { n = 1; };
                    for (int i = 0; i < n; i++) { c++; shrink(); }
                    return c;
                }
                // Control: a write before the call goes into the upper bound taken on entering the loop
                static int WrittenBefore()
                {
                    int n = 3;
                    Func<int> count = () =>
                    {
                        int c = 0;
                        for (int i = 0; i < n; i++) c++;
                        return c;
                    };
                    n = 5;
                    return count();
                }
                public static void Main()
                {
                    Console.WriteLine(InLambda() + "," + InMethod() + "," + WrittenBefore());
                }
            }
            """, "P");
        Assert.Equal("1,1,5", output);
    }

    // With i < limit, a limit of int.MinValue runs zero iterations (limit - 1 would wrap to int.MaxValue)
    [CFact]
    public void NumericFor_ExclusiveBoundAtIntMinValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                static int First(int limit)
                {
                    for (int i = 0; i < limit; i++)
                        if (i == 0) return 1;
                    return 0;
                }
                static int FirstStep(int limit)
                {
                    for (int i = 0; i < limit; i += 1)
                        if (i == 0) return 1;
                    return 0;
                }
                static int Count(int start, int limit)
                {
                    int n = 0;
                    for (int i = start; i < limit; i++)
                    {
                        n++;
                        if (n == 5) break;
                    }
                    return n;
                }
                public static void Main()
                {
                    Console.WriteLine(First(int.MinValue) + "," + First(0) + "," + First(1));
                    Console.WriteLine(FirstStep(int.MinValue) + "," + FirstStep(1));
                    Console.WriteLine(Count(int.MinValue, int.MinValue) + "," + Count(int.MaxValue, int.MinValue) + "," + Count(int.MinValue, int.MinValue + 2) + "," + Count(int.MaxValue - 1, int.MaxValue));
                }
            }
            """, "P");
        Assert.Equal("0,0,1\n0,1\n0,0,2,1", output);
    }

    // With i <= limit, a limit of int.MaxValue keeps iterating after i++ wraps to int.MinValue
    [CFact]
    public void NumericFor_InclusiveBoundAtIntMaxValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                static int Visits(int start, int limit)
                {
                    int n = 0;
                    for (int i = start; i <= limit; i++)
                    {
                        n++;
                        if (n == 2) break;
                    }
                    return n;
                }
                static string Seen(int start, int limit)
                {
                    var s = "";
                    int n = 0;
                    for (int i = start; i <= limit; i++)
                    {
                        s += i + ";";
                        n++;
                        if (n == 3) break;
                    }
                    return s;
                }
                public static void Main()
                {
                    Console.WriteLine(Visits(int.MaxValue, int.MaxValue) + "," + Visits(3, 3) + "," + Visits(int.MinValue, int.MinValue));
                    Console.WriteLine(Seen(int.MaxValue - 1, int.MaxValue));
                    Console.WriteLine(Seen(int.MaxValue, int.MinValue) + "|" + Seen(-1, 0));
                }
            }
            """, "P");
        Assert.Equal("2,1,1\n2147483646;2147483647;-2147483648;\n|-1;0;", output);
    }

    // A for whose control variable or comparison is not int (float, int?) also iterates as in C#
    [CFact]
    public void NumericFor_NonIntCounterOrComparison()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                public static void Main()
                {
                    int a = 0;
                    for (int i = 0; i < 2.5f; i++) a++;
                    float bound = 2.5f;
                    int b = 0;
                    for (int i = 0; i <= bound; i++) b++;
                    int c = 0;
                    for (float f = 0.5f; f < 3; f++) c++;
                    int d = 0;
                    for (int? k = 0; k < 3; k++) d++;
                    int? m = 2;
                    int e = 0;
                    for (int i = 0; i < m; i++) e++;
                    int g = 0;
                    for (char ch = 'a'; ch < 'e'; ch++) g++;
                    float sum = 0;
                    for (float f = 0.5f; f <= 2; f += 1) sum += f;
                    Console.WriteLine(a + "," + b + "," + c + "," + d + "," + e + "," + g + "," + sum);
                }
            }
            """, "P");
        Assert.Equal("3,3,3,3,2,4,2", output);
    }

    // Only a for whose control variable and comparison are int, and whose body writes to neither the upper bound variable nor the control variable, becomes a numeric for.
    // An ordinary for (the upper bound is a local variable or a constant, i++ / i += 1) stays a numeric for
    [Fact]
    public void NumericFor_LowersOnlyProvablyEquivalentLoops()
    {
        var session = new Tcs.Session("") { ExportOnly = true };
        session.SetSource("p.cs", """
            public static class P
            {
                public static int Numeric(int n, int[] a)
                {
                    int s = 0;
                    for (int i = 0; i < n; i++) s += a[i];
                    for (int i = 1; i <= n; i += 1) s += i;
                    for (int i = n; i < 10; i++) s += i;
                    for (char c = 'a'; c <= 'z'; c++) s += c;
                    return s;
                }
                public static int General(int n)
                {
                    int s = 0, x = 0;
                    for (int i = 0; i < n; i++) (n, x) = (x, n);
                    int m = 3;
                    for (int i = 0; i < m; i++) (i, x) = (x, i);
                    for (int i = 0; i < 2.5f; i++) s++;
                    for (float f = 0; f < 3; f++) s++;
                    for (int? k = 0; k < 3; k++) s++;
                    return s + x;
                }
            }
            """);
        var result = session.Compile();
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        var il = Tcs.TcsToVm.IlText(result.Export!);
        var methods = il.Split("  method ");
        var numeric = methods.Single(m => m.StartsWith("static Numeric("));
        var general = methods.Single(m => m.StartsWith("static General("));
        Assert.Equal(4, Count(numeric, "IlNumericFor("));
        Assert.Equal(0, Count(numeric, "IlWhile("));
        Assert.Equal(0, Count(general, "IlNumericFor("));
        Assert.Equal(5, Count(general, "IlWhile("));
    }

    private static int Count(string text, string word) =>
        (text.Length - text.Replace(word, "").Length) / word.Length;
}
