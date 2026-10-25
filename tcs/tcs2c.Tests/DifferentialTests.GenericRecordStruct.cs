namespace TinyCs.Tcs2c.Tests;

// generic な record / struct / record struct と、generic class の static property、generic method の
// method group。Lua は型引数の既定値を隠れ引数で受け (struct は zero 値と copy にも持たせる)、
// C は閉じた copy に特殊化する。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void GenericRecordStruct_DefaultsAndSpecialization()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public readonly record struct V(int A);
            public record Pair<T>(T First, T Second)
            {
                public T Empty() => default!;
                public Pair<T> Swap() => new Pair<T>(Second, First);
            }
            public struct Slot<T>
            {
                public T Value;
                public bool Has;
                public T OrDefault() => Has ? Value : default!;
            }
            public readonly record struct Wrap<T>(T Item);
            public class Box<T>
            {
                public static T Zero => default!;
                public T Get() => Zero;
            }
            public static class G
            {
                public static T Id<T>(T x) => x;
                public static T Pick<T>(List<T> list, int i) => i < list.Count ? list[i] : default!;
            }
            public static class P
            {
                public static void Main()
                {
                    var p = new Pair<int>(1, 2);
                    Console.WriteLine(p.Empty() + 1);
                    Console.WriteLine(p.Swap().First + ":" + (p == new Pair<int>(1, 2) ? "eq" : "ne"));
                    var s = new Slot<int>();
                    Console.WriteLine(s.OrDefault() + 2 + s.Value);
                    var sv = new Slot<V>();
                    Console.WriteLine(sv.OrDefault().A + sv.Value.A);
                    var w = new Wrap<string>("x");
                    Console.WriteLine(w.Item + (w == new Wrap<string>("x") ? " eq" : " ne"));
                    Console.WriteLine(new Box<int>().Get() + 3);
                    Func<int, int> id = G.Id<int>;
                    Func<List<int>, int, int> pick = G.Pick<int>;
                    Console.WriteLine(id(4) + pick(new List<int>(), 0));
                }
            }
            """, "P");
        Assert.Equal("""
            1
            2:eq
            2
            0
            x eq
            3
            4
            """, output);
    }

    // generic struct の copy は型引数の値も写す (Lua は型引数ごとの copy 関数を呼び出し側が渡す)
    [CFact]
    public void GenericStructCopy_CopiesTypeArgumentStructs()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct M { public int X; }
            public struct Slot<T> { public T Value; public int N; }
            public struct Pair<T> { public Slot<T> A; public T B; }
            public static class P
            {
                public static void Main()
                {
                    var a = new Slot<M>();
                    var b = a;
                    b.Value.X = 5;
                    var p = new Pair<M>();
                    var q = p;
                    q.A.Value.X = 7;
                    q.B.X = 8;
                    var ss = new Slot<Slot<M>>();
                    var tt = ss;
                    tt.Value.Value.X = 9;
                    Console.WriteLine(a.Value.X + ":" + b.Value.X + ":" + p.A.Value.X + ":" + p.B.X + ":" + q.A.Value.X + ":" + ss.Value.Value.X + ":" + tt.Value.Value.X);
                }
            }
            """, "P");
        Assert.Equal("0:5:0:0:7:0:9", output);
    }
}
