namespace TinyCs.Tcs2c.Tests;

// struct を object / interface に入れる (boxing) のはサブセット外。Lua の struct は型を持たない
// table で `is` / interface 経由の呼び出しが効かず、C は受け付けないので、両 backend とも同じ
// TCS1001 で止まる
public partial class DifferentialTests
{
    [Theory]
    [InlineData("object o = new M { X = 3 };", "StructBoxing")]
    [InlineData("IShape s = new M { X = 3 };", "StructBoxing")]
    [InlineData("var o = (object)new M();", "StructBoxing")]
    [InlineData("Console.WriteLine(new M());", "StructBoxing")]
    public void StructBoxing_BothBackendsStopWithDiagnostic(string statement, string rule)
    {
        var program = $$"""
            using System;
            public interface IShape { int Area(); }
            public struct M : IShape { public int X; public int Area() => X * X; }
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
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}", ex.Message);
        }
    }
}
