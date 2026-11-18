namespace TinyCs.Tcs2c.Tests;

// foreach の反復の変数に要素と違う型を書くと、変数は要素をその型へ明示の変換 (`(T)要素`) で変えた値になる (C# の foreach と同じ)
public partial class DifferentialTests
{
    [CFact]
    public void Foreach_ConvertsElementToVariableType()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public interface IShape { int Area(); }
            public class Sq : IShape { public int S; public int Area() => S * S; }
            public class P
            {
                public static void Main()
                {
                    float[] values = new float[] { 1.75f, -2.75f };
                    foreach (int value in values) Console.WriteLine(value);
                    foreach (int value in values) Console.WriteLine(value == 1 || value == -2);
                    foreach (int value in new List<float> { 3.5f, -0.5f }) Console.WriteLine(value);
                    var queue = new Queue<float>();
                    queue.Enqueue(4.5f);
                    foreach (int value in queue) Console.WriteLine(value);
                    foreach (int value in new Dictionary<int, float> { [1] = 5.5f }.Values) Console.WriteLine(value);
                    foreach (float f in new int[] { 1, 3 }) Console.WriteLine(f / 2);
                    foreach (float f in new HashSet<int> { 5 }) Console.WriteLine(f / 2);
                    foreach (int? n in new List<int> { 7 }) Console.WriteLine(n.HasValue + " " + n);
                    foreach (int n in new List<int?> { 8 }) Console.WriteLine(n + 1);
                    foreach (char c in new int[] { 65, 66 }) Console.WriteLine(c);
                    foreach (int c in new char[] { 'A' }) Console.WriteLine(c + 1);
                    foreach (IShape s in new List<Sq> { new Sq { S = 3 } }) Console.WriteLine(s.Area());
                    // 反復ごとの変数を lambda が捕まえる
                    var actions = new List<Func<int>>();
                    foreach (int v in values) actions.Add(() => v * 10);
                    foreach (var a in actions) Console.WriteLine(a());
                    // 入れ子の反復
                    foreach (int i in new float[] { 1.5f, 2.5f })
                    {
                        foreach (int j in new float[] { 0.5f, 3.5f }) Console.Write(i * 10 + j + " ");
                    }
                    Console.WriteLine();
                }
            }
            """, "P");
        Assert.Equal(string.Join("\n",
            "1", "-2", "True", "True", "3", "0", "4", "5", "0.5", "1.5", "2.5", "True 7", "9", "A", "B", "66", "9", "10", "-20", "10 13 20 23 "), output);
    }

    // 反復の変数への変換は cast と同じ規則で止める (byte への変換、char を object に入れる変換)
    [Theory]
    [InlineData("foreach (byte b in new int[] { 300 }) Console.WriteLine(b);", "ByteNarrowing")]
    [InlineData("foreach (object o in new char[] { 'a' }) Console.WriteLine(o);", "ValueBoxing(char)")]
    public void Foreach_ConversionFollowsCastRules(string statement, string rule)
    {
        var program = $$"""
            using System;
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

    // float から int への変換は cast と同じく、int に入らない値 (NaN) で tcs VM と C は fault する (il-spec §5。.NET は値を作る)
    [CFact]
    public void Foreach_FloatToIntOutOfRangeFaults()
    {
        var output = Backends.AssertBackendFaultParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    foreach (int value in new float[] { 1.5f, float.NaN }) Console.WriteLine(value);
                }
            }
            """, "P");
        Assert.Equal("1", output);
    }
}
