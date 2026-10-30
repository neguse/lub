namespace TinyCs.Tcs2c.Tests;

// codepoint から文字列を作る手段 (Rune の文字列化と char.ConvertFromUtf32) は UTF-8 の文字列を返す。
// 期待値は同じ program を CoreCLR で実行した stdout。文字列は byte 単位のままなので Length は比べない
public partial class DifferentialTests
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

    // CoreCLR は ArgumentOutOfRangeException。tcs は例外がサブセット外なので fault
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
                     () => Backends.RunLua([program], "P"),
                     () => Backends.RunC([program], "P"),
                 })
            Assert.Contains("ConvertFromUtf32: not a Unicode scalar value",
                Assert.Throws<InvalidOperationException>(run).Message);
    }

    // 文字列化の対象は Rune そのものだけで、要素や T? の文字列化はサブセットを広げない
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
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}",
                Assert.Throws<InvalidOperationException>(run).Message);
    }
}
