namespace TinyCs.Tcs2c.Tests;

// int.TryParse / float.TryParse on a null string return false with out set to 0 (#429). C stopped with a null fault
public class TryParseNullDifferentialTests
{
    [CFact]
    public void TryParse_Null_ReturnsFalse()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    string s = null;
                    int value = 123;
                    bool ok = int.TryParse(s, out value);
                    float f = 123f;
                    bool fok = float.TryParse(s, out f);
                    Console.WriteLine((ok ? 1 : 0) + "," + value + "," + (fok ? 1 : 0) + "," + (f == 0f ? 1 : 0));
                }
            }
            """, "P");
        Assert.Equal("0,0,0,1", output);
    }
}
