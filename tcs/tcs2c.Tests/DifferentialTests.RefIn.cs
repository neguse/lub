namespace TinyCs.Tcs2c.Tests;

// ref / in はサブセットの外。どの形でも何が悪いか読める同じ TCS1001 で止まる
public partial class DifferentialTests
{
    [Theory]
    [InlineData("static float F(ref V v) { return v.X; }", "var v = new V(); F(ref v);", "RefParameter")]
    [InlineData("static void F(ref int a, ref int b) { a = b; }", "int x = 1, y = 2; F(ref x, ref y);", "RefParameter")]
    [InlineData("static float F(ref readonly V v) { return v.X; }", "var v = new V(); F(in v);", "RefParameter")]
    [InlineData("static float F(in V v) { return v.X; }", "var v = new V(); F(in v);", "InParameter")]
    [InlineData("static float F(in V v) { return v.X; }", "var v = new V(); F(v);", "InParameter")]
    [InlineData("static ref V F(V[] a) { return ref a[0]; }", "F(new V[1]);", "RefType")]
    [InlineData("", "var v = new V(); ref V r = ref v;", "RefType")]
    [InlineData("", "var v = new V(); ref readonly V r = ref v;", "RefType")]
    public void RefAndIn_BothBackendsStopWithDiagnostic(string member, string statement, string rule)
    {
        var program = $$"""
            public struct V { public float X; }
            public static class P
            {
                {{member}}
                public static void Main() { {{statement}} }
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
            Assert.DoesNotContain("expression:", ex.Message);
        }
    }
}
