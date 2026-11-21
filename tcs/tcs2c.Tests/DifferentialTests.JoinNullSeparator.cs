namespace TinyCs.Tcs2c.Tests;

// string.Join の separator の null は空文字列 (.NET と同じ)。List / 配列 / 値型の要素 / enum の要素 / 並べた引数のどの形でも同じ。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void StringJoin_NullSeparator_IsEmpty()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public enum Color { Red, Green }
            public static class P
            {
                static string Sep() => null;
                public static void Main()
                {
                    string separator = null;
                    Console.WriteLine(string.Join(separator, new string[] { "a", "b" }));
                    Console.WriteLine(string.Join(separator, new List<string> { "c", null, "d" }));
                    Console.WriteLine(string.Join(Sep(), new int[] { 1, 2, 3 }));
                    Console.WriteLine(string.Join(separator, new List<float> { 1.5f, 2f }));
                    Console.WriteLine(string.Join(separator, new List<bool> { true, false }));
                    Console.WriteLine(string.Join(separator, new List<Color> { Color.Green, Color.Red }));
                    Console.WriteLine(string.Join(separator, "x", "y", "z"));
                    Console.WriteLine("[" + string.Join(separator, new string[0]) + "]");
                    Console.WriteLine(string.Join(separator, new string[] { "one" }));
                    // 対照: 空文字列の separator
                    Console.WriteLine(string.Join("", new string[] { "a", "b" }));
                }
            }
            """, "P");
        Assert.Equal("ab\ncd\n123\n1.52\nTrueFalse\nGreenRed\nxyz\n[]\none\nab", output);
    }
}
