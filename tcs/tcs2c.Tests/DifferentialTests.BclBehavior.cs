namespace TinyCs.Tcs2c.Tests;

// BCL / 言語の .NET 意味論に合わせる 2 backend differential。期待値は
// CoreCLR (net10.0) の実測 stdout
public partial class DifferentialTests
{
    // 空の排他範囲 (Next(0) / Next(n, n)) は .NET と同じく下限を返す。
    // 3 行目は Lua / C が同じ列を引き続けることだけを見る
    [CFact]
    public void Random_EmptyExclusiveRangeReturnsLowerBound()
    {
        var output = Backends.AssertParity("""
            using System;
            using Random = TinySystem.Random;
            public class P
            {
                public static void Main()
                {
                    var r = new Random(42);
                    Console.WriteLine(r.Next(0) + ":" + r.Next(5, 5) + ":" + r.Next(-5, -5) + ":" + r.Next(0, 0) + ":" + r.Next(1) + ":" + r.Next(5, 6));
                    Console.WriteLine(Random.Shared.Next(0) + ":" + Random.Shared.Next(5, 5) + ":" + Random.Shared.Next(-5, -5) + ":" + Random.Shared.Next(int.MinValue, int.MinValue));
                    Console.WriteLine(r.Next(1000));
                }
            }
            """, "P");
        Assert.StartsWith("0:5:-5:0:0:5\n0:5:-5:-2147483648\n", output);
    }

    // OrderBy / OrderByDescending は安定 sort (同じ key の要素は元の順)
    [CFact]
    public void Linq_OrderByKeepsOrderOfEqualKeys()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class P
            {
                public static void Main()
                {
                    var source = new List<int> { 1, 2, 3, 4 };
                    Console.WriteLine(string.Join(",", source.OrderBy(x => 0).Select(x => x.ToString())));
                    Console.WriteLine(string.Join(",", source.OrderByDescending(x => 0).Select(x => x.ToString())));
                    var control = new List<int> { 4, 2, 1, 3 };
                    Console.WriteLine(string.Join(",", control.OrderBy(x => x).Select(x => x.ToString())));
                    Console.WriteLine(string.Join(",", control.OrderByDescending(x => x).Select(x => x.ToString())));
                    var mixed = new List<int> { 12, 21, 11, 22 };
                    Console.WriteLine(string.Join(",", mixed.OrderBy(x => x / 10).Select(x => x.ToString())));
                    Console.WriteLine(string.Join(",", mixed.OrderByDescending(x => x / 10).Select(x => x.ToString())));
                    var words = new string[] { "bb", "a", "cc", "d", "ee" };
                    Console.WriteLine(string.Join(",", new List<string>(words).OrderBy(w => w.Length)));
                    Console.WriteLine(string.Join(",", new List<string>(words).OrderByDescending(w => w.Length)));
                    var many = new List<int>();
                    for (int i = 0; i < 40; i++) many.Add(i);
                    Console.WriteLine(string.Join(",", many.OrderBy(x => x % 3).Select(x => x.ToString())));
                }
            }
            """, "P");
        Assert.Equal("""
            1,2,3,4
            1,2,3,4
            1,2,3,4
            4,3,2,1
            12,11,21,22
            21,22,12,11
            a,d,bb,cc,ee
            bb,cc,ee,a,d
            0,3,6,9,12,15,18,21,24,27,30,33,36,39,1,4,7,10,13,16,19,22,25,28,31,34,37,2,5,8,11,14,17,20,23,26,29,32,35,38
            """.ReplaceLineEndings("\n"), output);
    }

    // string.Replace の置換後文字列が null なら一致部分を削除する
    [CFact]
    public void String_ReplaceWithNullDeletes()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    string replacement = null;
                    Console.WriteLine("result=" + "banana".Replace("an", replacement));
                    Console.WriteLine("aXbXc".Replace("X", null) + ":" + "none".Replace("X", null) + ":" + "XX".Replace("X", null).Length);
                }
            }
            """, "P");
        Assert.Equal("result=ba\nabc:none:0", output);
    }

    // 省略引数の既定値は呼び出しが静的に束縛した宣言 (interface) のものを
    // 使う。実装側の既定値は実装型で呼んだときだけ
    [CFact]
    public void OptionalArguments_UseStaticallyBoundDeclarationDefault()
    {
        var output = Backends.AssertParity([
            """
            using System;
            public class C : IReader
            {
                public int Read(int x = 7) { return x; }
                public float Scale(float f = 2.5f) { return f; }
                public Dir Face(Dir d = Dir.West) { return d; }
            }
            public class P
            {
                public static void Main()
                {
                    var c = new C();
                    IReader i = c;
                    Console.WriteLine(c.Read() + ":" + i.Read() + ":" + i.Read(11));
                    Console.WriteLine(c.Scale() + ":" + i.Scale() + ":" + (int)c.Face() + ":" + (int)i.Face());
                }
            }
            """,
            """
            public enum Dir { North, East, South, West }
            public static class K { public const int Three = 3; }
            public interface IReader
            {
                int Read(int x = K.Three);
                float Scale(float f = 0.5f);
                Dir Face(Dir d = Dir.East);
            }
            """], "P");
        Assert.Equal("7:3:11\n2.5:0.5:3:1", output);
    }
}
