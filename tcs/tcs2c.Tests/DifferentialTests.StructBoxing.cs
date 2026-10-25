namespace TinyCs.Tcs2c.Tests;

// struct を object / interface に入れる (boxing) のはサブセット外。Lua の struct は型を持たない
// table で `is` / interface 経由の呼び出しが効かず、C は受け付けないので、両 backend とも同じ
// TCS1001 で止まる。interface の制約を持つ型引数に struct を入れるのも、Lua の generic なコードが
// interface の method を method の呼び出し (`x:Area()`) にするので同じ
public partial class DifferentialTests
{
    [Theory]
    [InlineData("object o = new M { X = 3 };", "StructBoxing")]
    [InlineData("IShape s = new M { X = 3 };", "StructBoxing")]
    [InlineData("var o = (object)new M();", "StructBoxing")]
    [InlineData("Console.WriteLine(new M());", "StructBoxing")]
    [InlineData("foreach (IShape s in new List<M> { new M() }) Console.WriteLine(s.Area());", "StructBoxing")]
    [InlineData("Console.WriteLine(G.AreaOf(new M { X = 3 }));", "StructInterfaceConstraint(M)")]
    [InlineData("Console.WriteLine(new Holder<M>().Count);", "StructInterfaceConstraint(M)")]
    public void StructBoxing_BothBackendsStopWithDiagnostic(string statement, string rule)
    {
        var program = $$"""
            using System;
            using System.Collections.Generic;
            public interface IShape { int Area(); }
            public struct M : IShape { public int X; public int Area() => X * X; }
            public static class G { public static int AreaOf<T>(T x) where T : IShape => x.Area(); }
            public class Holder<T> where T : IShape { public int Count; }
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
