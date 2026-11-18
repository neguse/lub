namespace TinyCs.Tcs2c.Tests;

// When a foreach iteration variable is written with a type different from the element, the variable is the element converted to that type by an explicit conversion (`(T)element`) (same as C#'s foreach)
public class ForeachConversionDifferentialTests
{
    [CFact]
    public void Foreach_ConvertsElementToVariableType()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public interface IShape { int Area(); }
            public class Sq : IShape { public int S; public int Area() => S * S; }
            public class P
            {
                public static void Main()
                {
                    float[] values = new float[] { 1.75f, -2.75f };
                    foreach (int value in values) Console.WriteLine(value);
                    foreach (int value in values) Console.WriteLine(value == 1 || value == -2);
                    foreach (int value in new List<float> { 3.5f, -0.5f }) Console.WriteLine(value);
                    var queue = new Queue<float>();
                    queue.Enqueue(4.5f);
                    foreach (int value in queue) Console.WriteLine(value);
                    foreach (int value in new Dictionary<int, float> { [1] = 5.5f }.Values) Console.WriteLine(value);
                    foreach (float f in new int[] { 1, 3 }) Console.WriteLine(f / 2);
                    foreach (float f in new HashSet<int> { 5 }) Console.WriteLine(f / 2);
                    foreach (int? n in new List<int> { 7 }) Console.WriteLine(n.HasValue + " " + n);
                    foreach (int n in new List<int?> { 8 }) Console.WriteLine(n + 1);
                    foreach (char c in new int[] { 65, 66 }) Console.WriteLine(c);
                    foreach (int c in new char[] { 'A' }) Console.WriteLine(c + 1);
                    foreach (IShape s in new List<Sq> { new Sq { S = 3 } }) Console.WriteLine(s.Area());
                    // A lambda captures the per-iteration variable
                    var actions = new List<Func<int>>();
                    foreach (int v in values) actions.Add(() => v * 10);
                    foreach (var a in actions) Console.WriteLine(a());
                    // Nested iteration
                    foreach (int i in new float[] { 1.5f, 2.5f })
                    {
                        foreach (int j in new float[] { 0.5f, 3.5f }) Console.Write(i * 10 + j + " ");
                    }
                    Console.WriteLine();
                }
            }
            """, "P");
        Assert.Equal(string.Join("\n",
            "1", "-2", "True", "True", "3", "0", "4", "5", "0.5", "1.5", "2.5", "True 7", "9", "A", "B", "66", "9", "10", "-20", "10 13 20 23 "), output);
    }

    // The conversion to the iteration variable is rejected by the same rules as a cast (a conversion to byte, a conversion that puts a char into an object)
    [Theory]
    [InlineData("foreach (byte b in new int[] { 300 }) Console.WriteLine(b);", "ByteNarrowing")]
    [InlineData("foreach (object o in new char[] { 'a' }) Console.WriteLine(o);", "ValueBoxing(char)")]
    public void Foreach_ConversionFollowsCastRules(string statement, string rule)
    {
        var program = $$"""
            using System;
            public static class P
            {
                public static void Main()
                {
                    {{statement}}
                }
            }
            """;
        foreach (var run in new Func<object>[]
                 {
                     () => Backends.EmitVm([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}", ex.Message);
        }
    }

    // As with a cast, the conversion from float to int faults on the tcs VM and C for a value that does not fit in int (NaN) (il-spec §5; .NET produces a value)
    [CFact]
    public void Foreach_FloatToIntOutOfRangeFaults()
    {
        var output = Backends.AssertBackendFaultParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    foreach (int value in new float[] { 1.5f, float.NaN }) Console.WriteLine(value);
                }
            }
            """, "P");
        Assert.Equal("1", output);
    }
}
