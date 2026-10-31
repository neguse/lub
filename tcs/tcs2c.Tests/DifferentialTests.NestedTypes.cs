namespace TinyCs.Tcs2c.Tests;

// 入れ子の型は外側の型の名前を `_` でつないだ独立の型 (`Outer_Inner`) になる。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void NestedTypes_AllKindsAndAccess()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public interface IShape { int Area(); }
            public interface IMark { }
            public class Outer
            {
                public struct Inner { public int A; public int Twice() { return A * 2; } }
                public class Node : IMark
                {
                    public int V;
                    public Node Next;
                    public static int Made;
                    public Node(int v) { V = v; Made = Made + 1; }
                    public int Sum() { return V + (Next != null ? Next.Sum() : 0); }
                    public int ReadOuter() { return Hidden + V; }
                }
                public enum Kind { Small, Big = 5 }
                public record Rec(int X, string Name) : IMark;
                public readonly record struct Pair(int L, int R);
                public interface IInner { int Get(); }
                public class Impl : IInner, IShape
                {
                    public int Get() { return 11; }
                    public int Area() { return 12; }
                }
                public static class Util
                {
                    public const int Limit = 9;
                    public static int Count = 3;
                    public static int Add(int a, int b) { return a + b; }
                }
                public class Box<T>
                {
                    public T Value;
                    public Box(T value) { Value = value; }
                    public T Get() { return Value; }
                }
                public class Deep
                {
                    public class Deeper
                    {
                        public enum Level { Low, High }
                        public static int Depth() { return 3; }
                        public Level L = Level.High;
                    }
                }
                private static int Hidden = 100;
                public Inner Held;
                public Kind State = Kind.Big;
                public static Node Head = new Node(7);
                public static int Run()
                {
                    var n = new Node(3);
                    var i = new Inner();
                    i.A = 4;
                    return n.V + i.A;
                }
            }
            public struct Host
            {
                public struct Part { public int P; }
                public class Tag { public string S = "tag"; }
                public Part Own;
            }
            public record Doc(int Id)
            {
                public enum Status { Open, Closed }
                public class Meta { public int M = 2; }
            }
            public interface IHolder
            {
                public class Marker { public int K = 8; }
                public enum Mode { On, Off }
            }
            namespace Game.Core
            {
                public class World
                {
                    public class Entity : IMark { public int Id = 42; }
                    public struct Pos { public int X; }
                    public static Entity Spawn() { return new Entity(); }
                }
            }
            public class Tagged : IMark { }
            public static class P
            {
                static string Describe(IMark o)
                {
                    if (o is Outer.Node n) return "node" + n.V;
                if (o is Outer.Rec r) return "rec" + r.X;
                if (o is Game.Core.World.Entity e) return "ent" + e.Id;
                return "other";
            }
            public static void Main()
                {
                    Console.WriteLine(Outer.Run());
                    var a = new Outer.Node(1);
                    a.Next = new Outer.Node(2);
                    Console.WriteLine(a.Sum());
                    Console.WriteLine(a.ReadOuter());
                    Console.WriteLine(Outer.Node.Made);
                    Console.WriteLine(Outer.Head.V);

                    var s = new Outer.Inner();
                    s.A = 5;
                    var copy = s;
                    copy.A = 6;
                    Console.WriteLine(s.A + ":" + copy.A + ":" + copy.Twice());

                    var o = new Outer();
                    o.Held.A = 9;
                    var o2 = o.Held;
                    o2.A = 1;
                    Console.WriteLine(o.Held.A);
                    Console.WriteLine(o.State);
                    Console.WriteLine((int)Outer.Kind.Big);
                    Console.WriteLine(Outer.Kind.Small.ToString());
                    Console.WriteLine(o.State == Outer.Kind.Big);
                    switch (o.State)
                    {
                        case Outer.Kind.Small: Console.WriteLine("small"); break;
                        case Outer.Kind.Big: Console.WriteLine("big"); break;
                    }

                    var r = new Outer.Rec(1, "x");
                    var r2 = r with { X = 2 };
                    Console.WriteLine(r == new Outer.Rec(1, "x"));
                    Console.WriteLine(r2.X + r2.Name);
                    var pr = new Outer.Pair(1, 2);
                    Console.WriteLine(pr == new Outer.Pair(1, 2));
                    Console.WriteLine(pr.L + pr.R);

                    Outer.IInner inner = new Outer.Impl();
                    IShape shape = new Outer.Impl();
                    Console.WriteLine(inner.Get() + shape.Area());

                    Console.WriteLine(Outer.Util.Limit + Outer.Util.Count + Outer.Util.Add(1, 2));
                    Outer.Util.Count = 10;
                    Console.WriteLine(Outer.Util.Count);

                    var box = new Outer.Box<int>(5);
                    var boxS = new Outer.Box<string>("s");
                    Console.WriteLine(box.Get() + boxS.Get());

                    Console.WriteLine(Outer.Deep.Deeper.Depth());
                    var d = new Outer.Deep.Deeper();
                    Console.WriteLine(d.L);
                    Console.WriteLine(d.L == Outer.Deep.Deeper.Level.High);

                    var h = new Host();
                    h.Own.P = 3;
                    var ht = new Host.Tag();
                    Console.WriteLine(h.Own.P + ht.S);
                    Console.WriteLine(new Doc.Meta().M + ":" + Doc.Status.Closed);
                    Console.WriteLine(new IHolder.Marker().K + ":" + IHolder.Mode.Off);

                    var e = Game.Core.World.Spawn();
                    var pos = new Game.Core.World.Pos();
                    pos.X = 2;
                    Console.WriteLine(e.Id + pos.X);

                    Console.WriteLine(Describe(a) + Describe(r) + Describe(e) + Describe(new Tagged()));
                    var list = new List<Outer.Inner>();
                    list.Add(s);
                    var dict = new Dictionary<Outer.Kind, Outer.Node>();
                    dict[Outer.Kind.Big] = a;
                    Console.WriteLine(list[0].A + ":" + dict[Outer.Kind.Big].V);
                    Func<Outer.Node, int> f = x => x.V;
                    Console.WriteLine(f(a));
                    Func<int, int, int> add = Outer.Util.Add;
                    Console.WriteLine(add(2, 3));
                    int Outer_Node = 5;
                    Console.WriteLine(Outer_Node + new Outer.Node(1).V);
                }
            }
            """, "P");
        Assert.Equal("""
            7
            3
            101
            4
            7
            5:6:12
            9
            Big
            5
            Small
            True
            big
            True
            2x
            True
            3
            23
            15
            10
            5s
            3
            High
            True
            3tag
            2:Closed
            8:Off
            44
            node1rec1ent42other
            5:1
            1
            5
            6
            """.ReplaceLineEndings("\n"), output);
    }
}
