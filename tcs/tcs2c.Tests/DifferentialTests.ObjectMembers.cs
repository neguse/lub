namespace TinyCs.Tcs2c.Tests;

// object の member (Equals / GetHashCode / ReferenceEquals / GetType / MemberwiseClone、record の
// 合成するものも) は Lua / C に対応物が無く、両 backend とも同じ TCS1002 で止まる。値の比較は ==
public partial class DifferentialTests
{
    [Theory]
    [InlineData("Console.WriteLine(c.Equals(c));", "object.Equals")]
    [InlineData("Console.WriteLine(c.GetHashCode());", "object.GetHashCode")]
    [InlineData("Console.WriteLine(r.Equals(r));", "R.Equals")]
    [InlineData("Console.WriteLine(rs.GetHashCode());", "RS.GetHashCode")]
    [InlineData("Console.WriteLine(1.Equals(1));", "int.Equals")]
    [InlineData("Console.WriteLine(object.ReferenceEquals(c, c));", "object.ReferenceEquals")]
    [InlineData("Console.WriteLine(Color.A.Equals(Color.A));", "System.Enum.Equals")]
    public void ObjectMembers_BothBackendsStopWithDiagnostic(string statement, string api)
    {
        var program = $$"""
            using System;
            public class C { }
            public record R(int A);
            public readonly record struct RS(int A);
            public enum Color { A }
            public static class P
            {
                public static void Main()
                {
                    var c = new C();
                    var r = new R(1);
                    var rs = new RS(1);
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
            Assert.Contains("error TCS1002: unsupported API:", ex.Message);
            Assert.Contains(api, ex.Message);
        }
    }
}
