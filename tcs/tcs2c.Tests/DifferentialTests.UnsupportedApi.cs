namespace TinyCs.Tcs2c.Tests;

// サブセット外の API (TCS1002) は両 backend とも IL を作る前に同じ診断で止まる。
// MathF は TinySystem の runtime に無い (Lua は global の MathF が nil)
public partial class DifferentialTests
{
    [Theory]
    [InlineData("""Console.WriteLine(MathF.Sqrt(4f));""", "System.MathF.Sqrt")]
    [InlineData("""Console.WriteLine(new List<int> { 1 }.First());""", "System.Linq.Enumerable.First")]
    [InlineData("""var a = new int[] { 2, 1 }; Array.Sort(a); Console.WriteLine(a[0]);""", "System.Array.Sort")]
    [InlineData("""var d = new SortedDictionary<string, int>(); Console.WriteLine(d.Count);""", "System.Collections.Generic.SortedDictionary<TKey, TValue>")]
    [InlineData("""var s = new Stack<int>(); s.Push(1); Console.WriteLine(s.Count);""", "System.Collections.Generic.Stack<T>")]
    [InlineData("""Console.WriteLine(Convert.ToInt32("1"));""", "System.Convert.ToInt32")]
    [InlineData("""Console.WriteLine(BitConverter.IsLittleEndian);""", "System.BitConverter.IsLittleEndian")]
    [InlineData("""Stack<int>? s = null; Console.WriteLine(s == null);""", "System.Collections.Generic.Stack<T>")]
    [InlineData("""Console.WriteLine(Enum.GetName(typeof(int), 1));""", "System.Enum.GetName")]
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
