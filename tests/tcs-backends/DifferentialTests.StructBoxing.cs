namespace TinyCs.Tcs2c.Tests;

// Putting a struct into object / interface (boxing) is outside the subset. Lua structs are untyped
// tables, so `is` / calls through an interface do not work, and C does not accept it, so both backends stop with the same
// TCS1001. Putting a struct into a type argument with an interface constraint is the same, because generic Lua code
// turns interface methods into method calls (`x:Area()`)
public class StructBoxingDifferentialTests
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
}
