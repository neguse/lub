namespace TinyCs.Tcs2c.Tests;

// instance method の overload。interface の method を実装する method は interface の method と
// 同じ名前になり、interface 経由の呼び出しと class 経由の呼び出しが同じ method を選ぶ。
// static と instance が同じ名前の型、struct の instance method も含む。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void InstanceOverload_DispatchesBySignature()
    {
        var output = Backends.AssertParity("""
            using System;
            public interface IShape
            {
                string Area();
                string Area(int scale);
            }
            public interface INamed { string Name(); }
            public class Square : IShape, INamed
            {
                public int Side = 3;
                public string Area() => "area " + Side * Side;
                public string Area(int scale) => "scaled " + Side * Side * scale;
                public string Area(float scale) => "float " + scale;
                public string Name() => "square";
                public string Name(string prefix) => prefix + "square";
                public static string Name(int n) => "static " + n;
            }
            public struct Counter
            {
                public int N;
                public void Add() { N += 1; }
                public void Add(int k) { N += k; }
            }
            public static class P
            {
                public static void Main()
                {
                    var sq = new Square();
                    IShape shape = sq;
                    INamed named = sq;
                    Console.WriteLine(sq.Area() + "|" + sq.Area(2) + "|" + sq.Area(1.5f));
                    Console.WriteLine(shape.Area() + "|" + shape.Area(3));
                    Console.WriteLine(named.Name() + "|" + sq.Name("a ") + "|" + Square.Name(4));
                    var c = new Counter();
                    c.Add();
                    c.Add(5);
                    Console.WriteLine(c.N);
                }
            }
            """, "P");
        Assert.Equal("""
            area 9|scaled 18|float 1.5
            area 9|scaled 27
            square|a square|static 4
            6
            """, output);
    }
}
