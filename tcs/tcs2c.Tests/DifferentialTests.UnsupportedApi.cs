namespace TinyCs.Tcs2c.Tests;

// サブセット外の API (TCS1002) は両 backend とも IL を作る前に同じ診断で止まる。
// MathF は TinySystem の runtime に無い (Lua は global の MathF が nil)
public partial class DifferentialTests
{
    [Theory]
    [InlineData("""Console.WriteLine(MathF.Sqrt(4f));""", "System.MathF.Sqrt")]
    [InlineData("""Console.WriteLine(new List<int> { 1 }.First());""", "System.Linq.Enumerable.First")]
    public void UnsupportedApi_BothBackendsStopWithDiagnostic(string statement, string api)
    {
        var program = $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
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
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"error TCS1002: unsupported API: {api}", ex.Message);
        }
    }
}
