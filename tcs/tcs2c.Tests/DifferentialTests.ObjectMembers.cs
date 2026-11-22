namespace TinyCs.Tcs2c.Tests;

// object members (Equals / GetHashCode / ReferenceEquals / GetType / MemberwiseClone, including those
// synthesized for records) have no counterpart in Lua / C, and both backends stop with the same TCS1002. Values are compared with ==
public class ObjectMembersDifferentialTests
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
        foreach (var run in new Func<object>[]
                 {
                     () => Backends.EmitVm([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains("error TCS1002: unsupported API:", ex.Message);
            Assert.Contains(api, ex.Message);
        }
    }
}
