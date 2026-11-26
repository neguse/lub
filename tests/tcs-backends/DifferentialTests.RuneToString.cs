namespace TinyCs.Tcs2c.Tests;

// The ways of making a string from a codepoint (string conversion of Rune and char.ConvertFromUtf32) return UTF-8 strings.
// The expected values are the stdout of running the same program on CoreCLR. Strings stay byte-based, so Length is not compared
public class RuneToStringDifferentialTests
{
    [CFact]
    public void Rune_ToStringConcatAndInterpolation_AreUtf8()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                public static void Main()
                {
                    var s = "";
                    foreach (var r in "Aアé😀".EnumerateRunes())
                    {
                        Console.WriteLine("" + r + "|" + (r + "!") + "|" + r.ToString() + "|" + $"{r}" + "|" + $"[{r:X}]");
                        Console.WriteLine(r);
                        s += r;
                    }
                    Console.WriteLine(s);
                }
            }
            """, "P");
        Assert.Equal("""
            A|A!|A|A|[A]
            A
            ア|ア!|ア|ア|[ア]
            ア
            é|é!|é|é|[é]
            é
            😀|😀!|😀|😀|[😀]
            😀
            Aアé😀
            """, output);
    }

    [CFact]
    public void ConvertFromUtf32_RoundTripsEveryUtf8Length()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                public static int Id(int x) => x;
                public static void Main()
                {
                    Console.WriteLine(char.ConvertFromUtf32(0x30A2) + char.ConvertFromUtf32(Id(0x1F600)));
                    foreach (var cp in new[] { 0, 0x41, 0x7F, 0x80, 0x7FF, 0x800, 0xD7FF, 0xE000, 0xFFFF, 0x10000, 0x10FFFF })
                        foreach (var r in char.ConvertFromUtf32(cp).EnumerateRunes())
                            Console.WriteLine(r.Value);
                }
            }
            """, "P");
        Assert.Equal("""
            ア😀
            0
            65
            127
            128
            2047
            2048
            55295
            57344
            65535
            65536
            1114111
            """, output);
    }

    // CoreCLR throws ArgumentOutOfRangeException. Exceptions are outside the subset in tcs, so it faults
    [CTheory]
    [InlineData(-1)]
    [InlineData(0xD800)]
    [InlineData(0xDFFF)]
    [InlineData(0x110000)]
    [InlineData(int.MaxValue)]
    public void ConvertFromUtf32_OutOfRange_FaultsWithSameMessage(int codepoint)
    {
        var program = $$"""
            using System;
            public static class P
            {
                public static int Id(int x) => x;
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine(char.ConvertFromUtf32(Id({{codepoint}})));
                    Console.WriteLine("after");
                }
            }
            """;
        Assert.Equal("before", Backends.AssertFaultParity(program, "P"));
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunC([program], "P"),
                     () => Backends.RunVm([program], "P"),
                 })
            Assert.Contains("ConvertFromUtf32: not a Unicode scalar value",
                Assert.Throws<InvalidOperationException>(run).Message);
    }

    // Only Rune itself is converted to a string; string conversion of elements or T? does not extend the subset
    [Theory]
    [InlineData("Console.WriteLine(string.Join(\",\", new List<Rune>()));", "UnsupportedToString(System.Text.Rune)")]
    [InlineData("Rune? n = null; Console.WriteLine(\"\" + n);", "UnsupportedToString(System.Text.Rune?)")]
    [InlineData("Rune? n = null; Console.WriteLine(n?.ToString());", "UnsupportedToString(System.Text.Rune)")]
    public void Rune_ElementsAndNullable_StopWithDiagnostic(string statement, string rule)
    {
        var program = $$"""
            using System;
            using System.Collections.Generic;
            using System.Text;
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
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}",
                Assert.Throws<InvalidOperationException>(run).Message);
    }
}
