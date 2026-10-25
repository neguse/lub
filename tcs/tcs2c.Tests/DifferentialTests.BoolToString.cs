namespace TinyCs.Tcs2c.Tests;

// bool の文字列化は .NET と同じ `True` / `False` (Console.WriteLine、連結、補間、ToString、
// bool? (null は空文字列)、string.Join の要素)。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void BoolToString_MatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    bool t = true;
                    bool f = false;
                    bool? n = null;
                    bool? nf = false;
                    Console.WriteLine(t);
                    Console.WriteLine(f + "|" + t.ToString() + "|" + $"{f}" + "|" + n + "|" + nf + "|" + $"{nf}");
                    Console.WriteLine(nf);
                    Console.WriteLine(string.Join(",", new List<bool> { true, false }));
                    var s = "x";
                    s += t;
                    Console.WriteLine(s);
                }
            }
            """, "P");
        Assert.Equal("""
            True
            False|True|False||False|False
            False
            True,False
            xTrue
            """, output);
    }
}
