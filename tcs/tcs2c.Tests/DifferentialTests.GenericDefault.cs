namespace TinyCs.Tcs2c.Tests;

// 型引数の既定値 (`default(T)`、`default`、generic class の initializer の無い field)。
// Lua は型を消すので、呼び出し側が型引数ごとの既定値を渡す。型引数を受け渡す
// generic method、lambda、generic class の instance / static method を含む。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void GenericDefault_IsDefaultOfTypeArgument()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public readonly record struct V(int A);
            public struct M { public int N; }
            public enum E { A, B }
            public class Box<T>
            {
                public T Value;
                public T Other = default!;
                public T Get() => Value;
                public T Fresh() { T t = default!; return t; }
                public U Map<U>(Func<T, U> f) => f(Value);
                public U Empty<U>() => default!;
                public static T Zero() => default!;
                public T ViaStatic() => Zero();
            }
            public static class G
            {
                public static T Pick<T>(List<T> list, int i) => i < list.Count ? list[i] : default!;
                public static T Forward<T>(List<T> list) => Pick(list, 99);
                public static Func<T> Later<T>() => () => default!;
                public static U Second<T, U>(T t) => default!;
                public static T OrDefault<T>(this List<T> list, int i) => Pick(list, i);
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(G.Pick(new List<int> { 1 }, 5) + 1);
                    Console.WriteLine(G.Pick(new List<float> { 1 }, 5) + 0.5f);
                    Console.WriteLine(G.Pick(new List<bool> { true }, 5) ? "T" : "F");
                    Console.WriteLine(G.Pick(new List<string> { "a" }, 5) == null ? "null" : "set");
                    Console.WriteLine(G.Pick(new List<V> { new V(3) }, 5).A);
                    Console.WriteLine(G.Pick(new List<M> { new M() }, 5).N);
                    Console.WriteLine(G.Pick(new List<int?> { 1 }, 5) == null ? "null" : "set");
                    Console.WriteLine(G.Pick(new List<E> { E.B }, 5));
                    Console.WriteLine(G.Forward(new List<int>()) + 2);
                    Console.WriteLine(G.Later<int>()() + 3);
                    Console.WriteLine(G.Second<string, int>("x") + 4);
                    Console.WriteLine(new List<int>().OrDefault(0) + 5);
                    Console.WriteLine(G.OrDefault(new List<int>(), 0) + 6);
                    var b = new Box<int>();
                    Console.WriteLine(b.Value + b.Other + b.Get() + b.Fresh() + b.ViaStatic() + 8);
                    Console.WriteLine(b.Map(x => x + 9) + b.Empty<int>());
                    var bv = new Box<V>();
                    Console.WriteLine(bv.Value.A + bv.Get().A + bv.Fresh().A);
                    var bs = new Box<string>();
                    Console.WriteLine(bs.Value == null ? "null" : "set");
                }
            }
            """, "P");
        Assert.Equal("""
            1
            0.5
            F
            null
            0
            0
            null
            A
            2
            3
            4
            5
            6
            8
            9
            0
            null
            """, output);
    }
}
