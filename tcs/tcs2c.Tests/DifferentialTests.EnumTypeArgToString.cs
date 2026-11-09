namespace TinyCs.Tcs2c.Tests;

// enum を入れた型引数の値と、enum の List / 配列を渡した string.Join の文字列化は member 名
// (Lua は型を消すので、型引数の既定値と一緒に名前の引き方を渡す)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void EnumTypeArgumentAndJoin_UseMemberNames()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public enum Color { Red, Green, Blue }
            public static class G
            {
                public static string Show<T>(T x) => "" + x + "|" + $"{x}";
                public static string Twice<T>(T x) => Show(x) + "/" + Show(x);
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(G.Show(Color.Green) + "|" + G.Twice(Color.Blue) + "|" + G.Show(3));
                    Console.WriteLine(string.Join(",", new List<Color> { Color.Red, Color.Blue }));
                    Console.WriteLine(string.Join("-", new[] { Color.Green, Color.Red }));
                }
            }
            """, "P");
        Assert.Equal("""
            Green|Green|Blue|Blue/Blue|Blue|3|3
            Red,Blue
            Green-Red
            """, output);
    }
}
