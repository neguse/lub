namespace TinyCs.Tcs2c.Tests;

// `??=` の受け手は右辺より先に確定する: 右辺が受け手の local を差し替えても元の object に書き、式の値は代入した値。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void CoalesceAssignment_RightReplacesLocalReceiver()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box { public string? Name; public int? Opt; public string? Prop { get; set; } }
            public class P
            {
                static string Text(string? s) { return s ?? "null"; }
                public static void Main()
                {
                    var original = new Box();
                    var other = new Box { Name = "other", Opt = 5 };
                    var box = original;
                    string result = (box.Name ??= ((box = other).Name + "!"));
                    Console.WriteLine(result + ":" + Text(original.Name) + ":" + other.Name);

                    var first = new Box();
                    var second = new Box { Name = "second" };
                    var b2 = first;
                    b2.Name ??= (b2 = second).Name + "?";
                    Console.WriteLine(Text(first.Name) + ":" + second.Name);

                    var o1 = new Box();
                    var o2 = new Box { Opt = 5 };
                    var b3 = o1;
                    int value = (b3.Opt ??= (b3 = o2).Opt.Value + 1);
                    Console.WriteLine(value + ":" + o1.Opt.Value + ":" + o2.Opt.Value);

                    var unchanged = new Box { Name = "kept" };
                    var b4 = unchanged;
                    string kept = (b4.Name ??= (b4 = other).Name);
                    Console.WriteLine(kept + ":" + b4.Name);

                    var p1 = new Box();
                    var p2 = new Box { Prop = "p2" };
                    var b5 = p1;
                    string pv = (b5.Prop ??= ((b5 = p2).Prop + "!"));
                    Console.WriteLine(pv + ":" + Text(p1.Prop) + ":" + p2.Prop);
                }
            }
            """, "P");
        Assert.Equal("other!:other!:other\nsecond?:second\n6:6:5\nkept:kept\np2!:p2!:p2", output);
    }
}
